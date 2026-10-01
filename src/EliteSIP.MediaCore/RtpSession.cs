using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace EliteSIP.MediaCore;

/// <summary>Настройки медиа-потока одного разговора.</summary>
public sealed record RtpSessionConfiguration(
    AudioCodec Codec = AudioCodec.Pcmu,
    byte PayloadType = 0,
    int PacketTimeMilliseconds = AudioCodecInfo.DefaultPacketTimeMilliseconds,
    byte? TelephoneEventPayloadType = TelephoneEvent.DefaultPayloadType,
    MediaSecurity? Security = null)
{
    public MediaSecurity Security { get; init; } = Security ?? MediaSecurity.None;

    public static RtpSessionConfiguration FromNegotiated(NegotiatedMedia negotiated)
    {
        ArgumentNullException.ThrowIfNull(negotiated);
        return new RtpSessionConfiguration(
            negotiated.Codec,
            negotiated.PayloadType,
            negotiated.PacketTimeMilliseconds,
            negotiated.TelephoneEventPayloadType,
            negotiated.Security);
    }

    /// <summary>
    /// На сколько растёт метка времени за пакет.
    ///
    /// Названо через метку времени, а не через отсчёты, намеренно: у G.722 это
    /// 160 при 320 отсчётах звука в том же пакете, и всякий, кто прочитает
    /// здесь «отсчёты», рано или поздно подставит не то число.
    /// </summary>
    public uint TimestampIncrement => Codec.TimestampIncrement(PacketTimeMilliseconds);
}

/// <summary>Не нашлось свободной пары портов под RTP и RTCP.</summary>
public sealed class NoFreeRtpPortException : Exception
{
    public NoFreeRtpPortException(ushort lower, ushort upper)
        : base($"Не удалось занять порт для RTP в диапазоне {lower}–{upper}.")
    {
        Lower = lower;
        Upper = upper;
    }

    public NoFreeRtpPortException()
        : this(0, 0)
    {
    }

    public NoFreeRtpPortException(string message)
        : base(message)
    {
    }

    public NoFreeRtpPortException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ushort Lower { get; }

    public ushort Upper { get; }
}

/// <summary>
/// Медиа-поток одного разговора: приём и отправка RTP по UDP.
///
/// Сокет один и тот же на приём и на отправку — это симметричный RTP, и он не
/// прихоть: Asterisk у нас настроен с <c>nat=force_rport,comedia</c>, то есть
/// шлёт медиа туда, откуда его получил. Слушать на одном порту, а отправлять с
/// другого — надёжный способ получить звук в одну сторону.
///
/// Вместо очереди отправки из оригинала здесь блокировка: состояние отправителя
/// (номер, метка времени, счётчики) правится под ней, а в сокет пишется уже
/// готовый пакет. Смысл тот же, что у последовательной очереди, — номера не
/// разъезжаются, — но без второго потока на каждый разговор.
/// </summary>
public sealed class RtpSession : IDisposable
{
    /// <summary>
    /// Сколько отказов приёма подряд терпим, прежде чем перестать слушать.
    ///
    /// Выходить из приёма после первого нельзя. На UDP сокет «подключён», и
    /// каждая ICMP port unreachable приходит сюда ошибкой — а присылает их
    /// перезапускаемый Asterisk на каждый наш кадр, то есть полсотни раз в
    /// секунду. Разговор при этом жив и через пару секунд продолжится; молча
    /// выйти из цикла значит потерять его насовсем, и выглядеть это будет как
    /// «звонок идёт, звука нет» — симптом, который в этом проекте уже трижды
    /// уводил разбор не туда.
    ///
    /// Потолок всё-таки нужен: у мёртвого сокета отказ возвращается немедленно,
    /// и приём без предела превратился бы в холостой цикл на всю мощность ядра.
    /// </summary>
    internal const int MaximumConsecutiveReceiveFailures = 16;

    private static readonly IPEndPoint AnySource = new(IPAddress.Any, 0);

    /// <summary>Формат потока. Меняется только под <see cref="_lock"/> — см. <see cref="Reconfigure"/>.</summary>
    private RtpSessionConfiguration _configuration;
    private readonly Socket _socket;
    private IPEndPoint _remote;
    private readonly Lock _lock = new();

    /// <summary>Защита исходящего направления. `null` — поток открытый.</summary>
    ///
    /// <remarks>
    /// Правится под тем же замком, что и номера с метками времени: у контекста
    /// есть состояние (счётчик оборотов), и две отправки разом сбили бы его так
    /// же, как сбили бы нумерацию.
    /// </remarks>
    private SrtpContext? _outbound;

    /// <summary>Защита входящего направления. Замка не требует: приём один.</summary>
    ///
    /// <remarks>
    /// Подменяется целиком при смене ключа собеседника, и поток приёма
    /// подхватывает новый контекст со следующего пакета — отсюда volatile.
    /// </remarks>
    private volatile SrtpContext? _inbound;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Состояние отправителя. Трогается только под <see cref="_lock"/>.</summary>
    private ushort _sequenceNumber;
    private uint _timestamp;

    /// <summary>
    /// Первый пакет разговора помечается маркером — так принято, и по нему
    /// принимающая сторона понимает начало речи после тишины.
    /// </summary>
    private bool _needsMarker = true;

    private bool _isStopped;
    private Task? _receiveLoop;

    /// <summary>Счётчики для отчётов RTCP. Растут под той же блокировкой, что и отправка.</summary>
    private uint _packetsSent;
    private uint _octetsSent;

    /// <param name="boundSocket">
    /// Уже привязанный к <paramref name="localPort"/> сокет — обычно взятый у
    /// <see cref="RtpPortReservation.TakeRtpSocket"/>. Если он передан, порт не
    /// отпускается ни на мгновение и занять его между резервацией и разговором
    /// не может никто, включая соседний процесс. Без него сокет создаётся и
    /// привязывается здесь — так работают проверки, которым резервация не
    /// нужна.
    /// </param>
    public RtpSession(
        RtpSessionConfiguration configuration,
        ushort localPort,
        string remoteHost,
        ushort remotePort,
        Socket? boundSocket = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.Security is { IsEncrypted: true } security)
        {
            // Два независимых контекста: исходящий из нашего ключа SDES,
            // входящий из ключа сервера. Смешивать их нельзя — у направлений
            // разные ключи, счётчик оборотов и окно повторов.
            //
            // Отсутствие любого из ключей при `IsEncrypted` невозможно по
            // построению `MediaSecurity.Sdes`, но проверка стоит здесь, а не в
            // виде утверждения: молчаливая отправка открытого RTP там, где
            // согласован SRTP, — это downgrade, который без снятия трафика не
            // видно.
            _outbound = new SrtpContext(security.LocalKey
                ?? throw new InvalidOperationException("защищённый поток без своего ключа SDES"));
            _inbound = new SrtpContext(security.RemoteKey
                ?? throw new InvalidOperationException("защищённый поток без ключа сервера"));
        }

        _configuration = configuration;
        LocalPort = localPort;
        _remote = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort);

        // Начальные значения случайны по RFC 3550 §5.1: предсказуемые номера
        // упрощают подмешивание чужого звука в поток.
        _sequenceNumber = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        _timestamp = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        SynchronizationSource = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);

        if (boundSocket is not null)
        {
            _socket = boundSocket;
            return;
        }

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            // Без этого Windows разрешает второму сокету встать на тот же порт,
            // и половина пакетов уходит соседу: разговор идёт, звук рваный.
            ExclusiveAddressUse = true,
        };

        // Привязка к конкретному локальному порту — то, что делает RTP
        // симметричным: ответный поток придёт на этот же сокет.
        _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
        UdpSocketOptions.IgnoreIcmpResets(_socket);
    }

    /// <summary>
    /// Переводит поток на другой адрес собеседника, не трогая сокет.
    ///
    /// Так выглядит пересогласование, в котором сервер вернулся с другого
    /// плеча: адрес сменился, порт остался наш, разговор тот же.
    /// Пересобирать ради этого сокет нельзя дважды. Во-первых, новый пришлось
    /// бы привязывать к тому же порту после закрытия старого — а это зазор, в
    /// котором порт свободен для всей машины. Во-вторых, вместе с сокетом
    /// пропали бы номер последовательности и метка времени: собеседник увидел
    /// бы обрыв потока там, где всего лишь сменился адрес.
    /// </summary>
    public void Retarget(string remoteHost, ushort remotePort)
    {
        ArgumentNullException.ThrowIfNull(remoteHost);

        IPEndPoint replacement = new(IPAddress.Parse(remoteHost), remotePort);
        lock (_lock)
        {
            _remote = replacement;

            // Маркер: для собеседника это начало речи с нового плеча.
            _needsMarker = true;
        }
    }

    /// <summary>
    /// Меняет формат потока — кодек, тип нагрузки, пакетное время, ключи SRTP —
    /// не трогая сокет.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// Нужно ранним медиа. Поток поднимается по SDP из 183, а 200 OK вправе
    /// уточнить его: станция отвечает с другого узла, со своим ключом SDES, а
    /// изредка и с другим кодеком. Порт при этом наш и уже объявлен, отпускать
    /// его ради пересборки нельзя по той же причине, что в <see cref="Retarget"/>.
    /// </para>
    /// <para>
    /// SSRC остаётся прежним, номер последовательности и метка времени идут
    /// дальше, первый пакет в новом формате несёт маркер. Контекст отправки
    /// пересоздаётся, только если сменился наш ключ: иначе у него обнулился бы
    /// счётчик оборотов, и собеседник отверг бы пакеты как повторы.
    /// </para>
    /// </remarks>
    public void Reconfigure(RtpSessionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        SrtpContext? inbound = null;

        lock (_lock)
        {
            MediaSecurity previous = _configuration.Security;
            MediaSecurity next = configuration.Security;

            if (next.IsEncrypted)
            {
                if (next.LocalKey is not SrtpMasterKey local || next.RemoteKey is not SrtpMasterKey remote)
                {
                    // То же правило, что в конструкторе: открытый RTP там, где
                    // согласован SRTP, — это downgrade, и молча его не делают.
                    throw new InvalidOperationException("защищённый поток без одного из ключей SDES");
                }

                if (_outbound is null || !Equals(previous.LocalKey, local))
                {
                    _outbound = new SrtpContext(local);
                }

                inbound = _inbound is null || !Equals(previous.RemoteKey, remote)
                    ? new SrtpContext(remote)
                    : _inbound;
            }
            else
            {
                _outbound = null;
            }

            _configuration = configuration;
            _needsMarker = true;
        }

        // Приём подхватывает контекст со следующего пакета. Пакет, уже
        // прочитанный со старым ключом, доигрывает как есть — это десятки
        // миллисекунд, и склеивать тут нечего.
        _inbound = inbound;
    }

    /// <summary>Пришедший пакет. Вызывается на потоке приёма — не блокировать.</summary>
    public Action<RtpPacket>? OnReceivedPacket { get; set; }

    public Action<string>? OnFailure { get; set; }

    public ushort LocalPort { get; }

    /// <summary>Наш SSRC. Отчёты RTCP подписываются им же.</summary>
    public uint SynchronizationSource { get; }

    /// <summary>Поток защищён SRTP.</summary>
    public bool IsSecured => _configuration.Security.IsEncrypted;

    /// <summary>
    /// Что мы отправили — для отчётов RTCP.
    ///
    /// Читается под той же блокировкой, под которой растут счётчики: отдавать
    /// их из-под чужого потока значило бы читать рваные значения.
    /// </summary>
    public (uint Packets, uint Octets, uint Timestamp, uint Ssrc) SendStatistics
    {
        get
        {
            lock (_lock)
            {
                return (_packetsSent, _octetsSent, _timestamp, SynchronizationSource);
            }
        }
    }

    public void Start() => _receiveLoop = Task.Run(ReceiveLoopAsync);

    /// <summary>
    /// Закрывает поток и дожидается, пока порт действительно освободится.
    ///
    /// Ждать приходится ради пересогласования: при удержании собеседник может
    /// вернуться с другого адреса, и тогда поток пересобирается на том же
    /// локальном порту. Без ожидания новый сокет встаёт на ещё занятый порт —
    /// звонок при этом продолжается, а звука нет.
    /// </summary>
    public void Stop(TimeSpan? waitingForReleaseUpTo = null)
    {
        lock (_lock)
        {
            if (_isStopped)
            {
                return;
            }

            _isStopped = true;
        }

        _stopping.Cancel();
        _socket.Close();
        _receiveLoop?.Wait(waitingForReleaseUpTo ?? TimeSpan.FromMilliseconds(500));
    }

    /// <summary>Отправляет кадр звука. Номер и метка времени наращиваются сами.</summary>
    public void Send(ReadOnlyMemory<byte> encodedFrame)
    {
        byte[] data;
        lock (_lock)
        {
            if (_isStopped)
            {
                return;
            }

            RtpPacket packet = new(
                _configuration.PayloadType,
                _sequenceNumber,
                _timestamp,
                SynchronizationSource,
                encodedFrame,
                _needsMarker);

            _needsMarker = false;
            _sequenceNumber = unchecked((ushort)(_sequenceNumber + 1));
            _timestamp = unchecked(_timestamp + _configuration.TimestampIncrement);
            _packetsSent = unchecked(_packetsSent + 1);

            // По RFC 3550 считается только полезная нагрузка, без заголовков.
            _octetsSent = unchecked(_octetsSent + (uint)encodedFrame.Length);

            data = Protected(packet);
        }

        SendRaw(data);
    }

    /// <summary>
    /// Пропускает кадр: время идёт, звук не уходит.
    ///
    /// Так выглядит немой микрофон. Просто не отправить кадр нельзя: метка
    /// времени растёт только на отправке, и собеседник, получив после минуты
    /// молчания пакет с меткой минутной давности, услышит не паузу, а
    /// рассинхронизацию — его джиттер-буфер будет разгребать её как приход
    /// безнадёжно старых кадров.
    ///
    /// Маркер ставится по той же причине, по какой он ставится после события
    /// DTMF: следующий отправленный кадр начинает новый участок речи, и
    /// принимающей стороне надо об этом сказать.
    /// </summary>
    public void SkipFrame()
    {
        lock (_lock)
        {
            if (_isStopped)
            {
                return;
            }

            _timestamp = unchecked(_timestamp + _configuration.TimestampIncrement);
            _needsMarker = true;
        }
    }

    /// <summary>
    /// Отправляет пакет события DTMF (RFC 4733).
    ///
    /// Метка времени НЕ наращивается в течение всего события: все пакеты одного
    /// нажатия несут время его начала, а растёт только поле duration. Если
    /// наращивать метку, приёмник услышит серию отдельных коротких тонов вместо
    /// одного длинного.
    /// </summary>
    public void SendEvent(TelephoneEventPayload payload, bool isFirst)
    {
        if (_configuration.TelephoneEventPayloadType is not byte eventPayloadType)
        {
            return;
        }

        byte[] data;
        lock (_lock)
        {
            if (_isStopped)
            {
                return;
            }

            byte[] encoded = payload.Encoded();
            RtpPacket packet = new(
                eventPayloadType,
                _sequenceNumber,
                _timestamp,
                SynchronizationSource,
                encoded,
                isFirst);

            _sequenceNumber = unchecked((ushort)(_sequenceNumber + 1));

            // Событие — такой же отправленный RTP-пакет, как и кадр звука, и в
            // счёт отправителя оно входит наравне с ним (RFC 3550 §6.4.1: счёт
            // ведётся по всем отправленным пакетам данных). Не считать их
            // значит занизить свой же Sender Report ровно на набранные цифры.
            _packetsSent = unchecked(_packetsSent + 1);
            _octetsSent = unchecked(_octetsSent + (uint)encoded.Length);

            data = Protected(packet);
        }

        SendRaw(data);
    }

    /// <summary>
    /// Завершает событие DTMF и возвращает поток к звуку.
    ///
    /// Метка времени сдвигается на всю длительность тона, а не на один пакет:
    /// внутри события она не росла, но время шло, и без этого сдвига весь
    /// остаток разговора уедет назад относительно часов отправителя.
    /// </summary>
    public void FinishEvent(uint? advancingTimestampBy = null)
    {
        lock (_lock)
        {
            _timestamp = unchecked(_timestamp + (advancingTimestampBy ?? _configuration.TimestampIncrement));
            _needsMarker = true;
        }
    }

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
        _socket.Dispose();
    }

    /// <summary>
    /// Готовит пакет к отправке: открытый — как есть, защищённый — через SRTP.
    /// </summary>
    ///
    /// <remarks>
    /// Зовётся только под <see cref="_lock"/> — оба места отправки уже под ним.
    /// Иначе счётчик оборотов исходящего контекста сбивался бы ровно так же, как
    /// сбивалась бы нумерация: два потока, один счётчик.
    /// </remarks>
    private byte[] Protected(RtpPacket packet)
        => _outbound is null ? packet.Encoded() : _outbound.Protect(packet);

    private void SendRaw(byte[] data)
    {
        try
        {
            _socket.SendTo(data, _remote);
        }
        catch (SocketException error)
        {
            OnFailure?.Invoke(error.Message);
        }
        catch (ObjectDisposedException)
        {
            // Сокет закрыли прямо во время отправки — это штатная остановка.
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[2048];
        int consecutiveFailures = 0;

        while (!_stopping.IsCancellationRequested)
        {
            int received;
            try
            {
                // Приём именно «откуда угодно», а не с подключённого сокета:
                // при comedia собеседник может начать слать медиа с другого
                // порта, чем объявил в SDP, и подключённый сокет такие пакеты
                // молча выбросит.
                SocketReceiveFromResult result = await _socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, AnySource, _stopping.Token)
                    .ConfigureAwait(false);
                received = result.ReceivedBytes;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException error)
            {
                consecutiveFailures++;

                // Говорим о первом отказе в череде и о том, на котором сдались.
                // Строка на каждый отказ залила бы журнал одним и тем же
                // текстом полсотни раз в секунду — то есть сделала бы его
                // бесполезным ровно там, где он нужен.
                if (consecutiveFailures == 1)
                {
                    OnFailure?.Invoke($"приём RTP: {error.Message}");
                }

                if (consecutiveFailures >= MaximumConsecutiveReceiveFailures)
                {
                    OnFailure?.Invoke(
                        $"приём RTP остановлен: {consecutiveFailures} отказов подряд ({error.Message})");
                    return;
                }

                continue;
            }

            // Приём состоялся — значит череда отказов кончилась, и считать её
            // дальше незачем.
            consecutiveFailures = 0;

            if (received <= 0)
            {
                continue;
            }

            // Битый или чужой пакет молча пропускаем: на открытый UDP-порт
            // прилетает что угодно, и рвать разговор из-за этого нельзя.
            //
            // На защищённом потоке сюда же попадает всё, что не прошло проверку
            // подлинности, и это не досадная мелочь, а главное свойство SRTP:
            // подделанный или повторённый пакет не должен доходить до разбора
            // RTP, не говоря о звуковом тракте.
            RtpPacket packet;
            try
            {
                // Один раз в локальную: контекст подменяется на ходу
                // (<see cref="Reconfigure"/>), и проверка на null с вызовом по
                // второму чтению разошлись бы.
                SrtpContext? inbound = _inbound;
                packet = inbound is null
                    ? RtpPacket.Parse(buffer.AsSpan(0, received))
                    : inbound.Unprotect(buffer.AsSpan(0, received));
            }
            catch (Exception error) when (error is RtpParseException or SrtpException)
            {
                continue;
            }

            OnReceivedPacket?.Invoke(packet);
        }
    }
}

/// <summary>
/// Владение парой локальных портов RTP/RTCP.
///
/// Одного номера недостаточно: между сборкой SDP и ответом на INVITE проходят
/// секунды, а линии готовятся параллельно. Резервация держит оба UDP-сокета
/// связанными до запуска потока и дополнительно не даёт сессиям этого процесса
/// выбрать ту же пару до завершения разговора.
/// </summary>
public sealed class RtpPortReservation : IDisposable
{
    /// <summary>Диапазон должен совпадать с <c>rtp.conf</c> и публикацией портов лаборатории.</summary>
    public const ushort DefaultPortRangeLower = 16384;

    public const ushort DefaultPortRangeUpper = 16482;

    private static readonly Lock RegistryLock = new();
    private static readonly HashSet<ushort> ClaimedPorts = [];

    private readonly Lock _stateLock = new();
    private List<Socket> _sockets;
    private bool _isReleased;

    private RtpPortReservation(ushort rtpPort, IPAddress localAddress, List<Socket> sockets)
    {
        RtpPort = rtpPort;
        LocalAddress = localAddress;
        _sockets = sockets;
    }

    public ushort RtpPort { get; }

    public ushort RtcpPort => (ushort)(RtpPort + 1);

    /// <summary>
    /// Адрес, к которому привязаны сокеты. <see cref="IPAddress.Any"/> — ко
    /// всем сразу, маршрут выбирает система на каждом пакете.
    /// </summary>
    public IPAddress LocalAddress { get; }

    /// <summary>Занимает сразу RTP и следующий за ним RTCP-порт.</summary>
    /// <param name="lower">Нижняя граница диапазона.</param>
    /// <param name="upper">Верхняя граница диапазона.</param>
    /// <param name="localAddress">
    /// Локальный адрес сигнализации. Медиа привязывается к нему, чтобы уходить
    /// тем же интерфейсом, что и SIP: сокет «к любому» получает маршрут на
    /// каждом пакете заново, и появившийся маршрут VPN уводит RTP под чужим
    /// адресом, пока SDP объявляет прежний. Неизвестный, пустой или не IPv4
    /// адрес — привязка «к любому», как раньше: лучше звонок по системному
    /// маршруту, чем отказ от звонка.
    /// </param>
    public static RtpPortReservation Reserve(
        ushort lower = DefaultPortRangeLower,
        ushort upper = DefaultPortRangeUpper,
        string? localAddress = null)
    {
        IPAddress bindAddress = BindAddress(localAddress);

        // Адрес сигнализации мог пропасть между подключением и звонком: кабель
        // выдернули, адаптер пересобрался. Тогда привязка к нему не проходит ни
        // на одном порту, и без второго круга оператор получил бы «нет
        // свободного порта» — неправду, по которой чинят не то.
        if (bindAddress.Equals(IPAddress.Any))
        {
            return ReserveOn(IPAddress.Any, lower, upper)
                ?? throw new NoFreeRtpPortException(lower, upper);
        }

        return ReserveOn(bindAddress, lower, upper)
            ?? ReserveOn(IPAddress.Any, lower, upper)
            ?? throw new NoFreeRtpPortException(lower, upper);
    }

    private static RtpPortReservation? ReserveOn(IPAddress bindAddress, ushort lower, ushort upper)
    {
        lock (RegistryLock)
        {
            int candidate = lower % 2 == 0 ? lower : lower + 1;

            // Пара обязана целиком лечь в диапазон: RTCP живёт на порту RTP плюс
            // один. Условие написано именно так, чтобы это было видно на месте —
            // из «candidate < upper» тот же смысл вычитается не сразу, и при
            // следующей правке границы легко получить пару, у которой второй
            // порт уже снаружи. Верхний порт диапазона при этом остаётся
            // неиспользуемым как RTP, и это правильно, а не потеря.
            while (candidate + 1 <= upper)
            {
                if (!ClaimedPorts.Contains((ushort)candidate)
                    && BoundDatagramSocket(bindAddress, (ushort)candidate) is Socket rtp)
                {
                    if (BoundDatagramSocket(bindAddress, (ushort)(candidate + 1)) is Socket rtcp)
                    {
                        ClaimedPorts.Add((ushort)candidate);
                        return new RtpPortReservation((ushort)candidate, bindAddress, [rtp, rtcp]);
                    }

                    rtp.Dispose();
                }

                candidate += 2;
            }
        }

        return null;
    }

    /// <summary>
    /// Отдаёт уже привязанный сокет RTP тому, кто будет им говорить.
    ///
    /// <b>Это замена «освободить и привязать заново», и разница не косметическая.</b>
    /// Между освобождением проверочного сокета и привязкой рабочего есть зазор,
    /// в котором порт свободен для всей машины. Внутрипроцессный учёт этот
    /// зазор не закрывает: другой процесс — второй экземпляр приложения или
    /// стенд рядом — про наш список занятых портов не знает вовсе и займёт
    /// порт совершенно законно. Отказ вылезет там, где его никто не ждёт: в
    /// конструкторе потока, посреди уже принятого звонка.
    ///
    /// Отданный сокет больше не принадлежит резервации: закрывать его будет
    /// поток. Порт при этом не отпускается ни на мгновение.
    /// </summary>
    public Socket TakeRtpSocket() => Take(0);

    /// <summary>Тот же приём для RTCP: порт RTP плюс один.</summary>
    public Socket TakeRtcpSocket() => Take(1);

    private Socket Take(int index)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_isReleased, this);

            if (index >= _sockets.Count || _sockets[index] is not Socket socket)
            {
                throw new InvalidOperationException(
                    "сокет уже отдан или резервация активирована — порт придётся занимать заново");
            }

            // Место занимается null, а не удаляется: индексы у пары
            // постоянные, и «второй» обязан остаться вторым даже после того,
            // как первый ушёл.
            _sockets[index] = null!;
            return socket;
        }
    }

    /// <summary>
    /// Освобождает проверочные сокеты непосредственно перед запуском RTP/RTCP.
    ///
    /// Логическое владение парой остаётся за объектом до <see cref="Release"/>,
    /// поэтому другая линия этого процесса не сможет забрать порт в коротком
    /// зазоре, пока создаются рабочие сокеты.
    ///
    /// <b>Оставлено ради проверок и совместимости.</b> Рабочий путь —
    /// <see cref="TakeRtpSocket"/>: он не оставляет зазора вовсе.
    /// </summary>
    public void Activate()
    {
        lock (_stateLock)
        {
            if (_isReleased)
            {
                return;
            }

            foreach (Socket socket in _sockets)
            {
                socket?.Dispose();
            }

            _sockets = [];
        }
    }

    /// <summary>Снимает и системную, и внутрипроцессную резервацию.</summary>
    public void Release()
    {
        List<Socket> held;
        lock (_stateLock)
        {
            if (_isReleased)
            {
                return;
            }

            _isReleased = true;
            held = _sockets;
            _sockets = [];
        }

        foreach (Socket socket in held)
        {
            socket?.Dispose();
        }

        lock (RegistryLock)
        {
            ClaimedPorts.Remove(RtpPort);
        }
    }

    public void Dispose() => Release();

    /// <summary>
    /// К чему привязывать сокеты. Всё, что не конкретный IPv4-адрес, —
    /// «к любому»: сокеты резервации только IPv4, а «0.0.0.0» — то, что
    /// транспорт сообщает, когда своего адреса не узнал.
    /// </summary>
    internal static IPAddress BindAddress(string? localAddress) =>
        IPAddress.TryParse(localAddress, out IPAddress? parsed)
            && parsed.AddressFamily == AddressFamily.InterNetwork
            && !parsed.Equals(IPAddress.Any)
            && !parsed.Equals(IPAddress.Broadcast)
            ? parsed
            : IPAddress.Any;

    private static Socket? BoundDatagramSocket(IPAddress address, ushort port)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            // Резервация обязана быть настоящей: без исключительного владения
            // Windows позволит встать на этот же порт кому угодно, и проверка
            // «порт свободен» перестанет что-либо значить.
            ExclusiveAddressUse = true,
        };
        try
        {
            socket.Bind(new IPEndPoint(address, port));
            UdpSocketOptions.IgnoreIcmpResets(socket);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }
}
