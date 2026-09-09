using EliteSIP.MediaCore;

namespace EliteSIP.Audio;

/// <summary>Чем кончилось пересогласование.</summary>
public enum MediaRenegotiation
{
    /// <summary>Изменилось только направление: звук перенаправлен, поток тот же.</summary>
    DirectionOnly,

    /// <summary>
    /// Собеседник вернулся с другого адреса — поток RTP пересобран на том же
    /// локальном порту.
    /// </summary>
    StreamRebuilt,
}

/// <summary>
/// Задержка разговора, разложенная по местам, где звук ждёт.
///
/// <b>Складывается из наших же счётчиков — и потому это оценка, а не замер.</b>
/// Она отвечает на вопрос «сколько задержки мы себе устроили» и повторяет наши
/// собственные заблуждения: если кольцо держит больше, чем думает, число этого
/// не покажет. Настоящий замер делается звуком, прошедшим весь путь
/// (<see cref="MediaCore.AudioDelayEstimator"/>), и расхождение двух чисел само
/// по себе находка.
/// </summary>
/// <param name="CaptureMilliseconds">От микрофона до отправленного кадра.</param>
/// <param name="JitterMilliseconds">Сколько принятое ждёт в джиттер-буфере.</param>
/// <param name="PlaybackMilliseconds">От кадра из буфера до динамика.</param>
/// <param name="NetworkRoundTripMilliseconds">
/// Круговая задержка сети по отчётам RTCP. <c>null</c> — собеседник ещё не
/// прислал ни одного отчёта: первый приходит через пять секунд разговора.
/// </param>
public sealed record MediaLatency(
    double CaptureMilliseconds,
    double JitterMilliseconds,
    double PlaybackMilliseconds,
    double? NetworkRoundTripMilliseconds)
{
    /// <summary>Задержка внутри нас: без сети, но со всеми буферами.</summary>
    public double LocalMilliseconds => CaptureMilliseconds + JitterMilliseconds + PlaybackMilliseconds;

    /// <summary>
    /// Рот-в-ухо в одну сторону: наши буферы плюс половина круга по сети.
    ///
    /// Половина — потому что круг RTCP меряет путь туда и обратно, а слышимая
    /// задержка набирается в одну. Строго это верно лишь на симметричном
    /// маршруте, но других на телефонии практически не бывает.
    ///
    /// Норма разговора — до 150 мс в одну сторону (ITU-T G.114); после 300 мс
    /// собеседники начинают перебивать друг друга, и это слышно всем, кроме
    /// того, кто мерил только буферы.
    /// </summary>
    public double? MouthToEarMilliseconds => NetworkRoundTripMilliseconds is double roundTrip
        ? LocalMilliseconds + (roundTrip / 2)
        : null;

    public string Summary
    {
        get
        {
            string buffers = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"захват {CaptureMilliseconds:F0} мс + буфер {JitterMilliseconds:F0} мс"
                    + $" + вывод {PlaybackMilliseconds:F0} мс");

            return NetworkRoundTripMilliseconds is double roundTrip
                ? string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{buffers} + сеть {roundTrip / 2:F0} мс = {MouthToEarMilliseconds:F0} мс рот-в-ухо")
                : string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{buffers} = {LocalMilliseconds:F0} мс без сети");
        }
    }
}

/// <summary>Пересогласование сменило кодек: от него зависит вся цепочка звука.</summary>
public sealed class MediaCodecChangedException : Exception
{
    public MediaCodecChangedException(AudioCodec from, AudioCodec to)
        : base($"собеседник сменил кодек с {from.SdpName()} на {to.SdpName()} — тракт надо пересобрать")
    {
        From = from;
        To = to;
    }

    public MediaCodecChangedException()
    {
    }

    public MediaCodecChangedException(string message)
        : base(message)
    {
    }

    public MediaCodecChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AudioCodec From { get; }

    public AudioCodec To { get; }
}

/// <summary>
/// Медиа-половина разговора: RTP, джиттер-буфер и аудиотракт вместе.
///
/// О SIP не знает ничего: на вход — уже согласованные параметры потока, на
/// выход — звук и статистика. Склейку с сигнализацией делает приложение, и
/// разделение слоёв от этого не страдает: <c>EliteSIP.SipCore</c> так и не
/// узнаёт про кодеки, а тело SDP собирается здесь.
///
/// <b>Лежит в <c>EliteSIP.Audio</c>, а не в <c>EliteSIP.MediaCore</c>, и это не
/// перенос по недосмотру.</b> В оригинале сессия жила в пакете <c>MediaCore</c>
/// потому, что там же лежал и весь аудиотракт. Здесь они разведены (см.
/// <c>docs/PORT-PLAN.md</c>): <c>MediaCore</c> собирается под <c>net10.0</c> и
/// проверяется без звуковой карты. Сессия берёт тракт, то есть звуковую карту,
/// — значит её место по эту сторону границы. Обратное — затащить
/// <see cref="VoiceAudioBus"/> в чистый пакет — стоило бы ровно того
/// разделения, ради которого W3 и W4 писались порознь.
///
/// Причина, по которой сессия вообще есть, а не растворена в приложении, та же,
/// что в оригинале: собрать разговор целиком — RTP, буфер и звуковую карту — и
/// послушать, что получилось, нужно из <c>tools/SipCheck</c>, а тот к
/// приложению не линкуется.
/// </summary>
public sealed class MediaSession : IDisposable
{
    /// <summary>
    /// Очередь, на которой тракт поднимается и снимается.
    ///
    /// Общая на все сессии и последовательная. Это не экономия на потоках, а
    /// порядок: подъём одной линии обязан идти после снятия другой, иначе на
    /// устройстве окажутся два захвата разом. Тот же порядок держит у себя
    /// <see cref="VoiceAudioBus"/>, и эта очередь его не подменяет, а повторяет
    /// с другой стороны — со стороны того, кто зовёт.
    /// </summary>
    private static readonly SemaphoreSlim Lifecycle = new(1, 1);

    private readonly Lock _transportGate = new();
    private readonly Lock _bufferGate = new();
    private readonly Lock _dtmfGate = new();

    private readonly RtpPortReservation _reservation;
    private readonly VoiceAudioBus _bus;
    private readonly bool _ownsBus;
    private readonly VoiceAudioConfiguration _audioConfiguration;
    private readonly AudioOwnerToken _token = AudioOwnerToken.New();
    private readonly JitterBuffer _jitter;
    private readonly RemoteSourceFilter _remoteSource = new();
    private readonly List<DtmfJob> _dtmfQueue = [];

    private Transport? _transport;
    private RemoteMediaView? _remoteView;
    private CancellationTokenSource? _dtmfCancellation;
    private Task? _dtmfWorker;

    /// <summary>
    /// Отдавать принятое в звук. Выключается на удержании: музыка ожидания в ухо
    /// оператору, который в это время говорит с другим, — не то, чего от
    /// удержания ждут.
    /// </summary>
    private volatile bool _receivesAudio = true;

    /// <summary>Немой микрофон: кадры захвата в линию не уходят.</summary>
    private volatile bool _isMuted;

    /// <summary>Идёт событие DTMF: кадры звука в этот момент не отправляются.</summary>
    private volatile bool _isSendingTone;

    /// <summary>
    /// Наш ли тракт — признаком, а не вопросом к шине.
    ///
    /// <b>Спрашивать у шины прямо из обработчика нельзя: это взаимоблокировка,
    /// и она была поймана на первом же живом звонке.</b>
    /// <see cref="VoiceAudioBus.Release"/> держит свой замок всё время, пока
    /// тракт останавливается, а остановка ждёт выхода потока захвата. Поток
    /// захвата в этот момент стоит в нашем обработчике на том же замке —
    /// разговор кончился, а приложение висит намертво (в отладочной сборке —
    /// падает на проверке «поток не остановился за секунду»).
    ///
    /// Признак снимается ДО освобождения и ставится ПОСЛЕ захвата, поэтому
    /// ошибается он всегда в безопасную сторону: «звук уже не наш» на кадр
    /// раньше правды. Обратной ошибки — отправить кадр в чужой разговор — быть
    /// не может: шина подменяет обработчики только после остановки тракта, а
    /// остановленный тракт их не зовёт.
    /// </summary>
    private volatile bool _ownsAudio;

    /// <summary>
    /// Признак ведётся здесь, а не спрашивается у тракта: тракт считает себя
    /// запущенным и после отказа сборки, а решение «отпустили ли мы карту»
    /// принимаем мы.
    /// </summary>
    private volatile bool _isAudioRunning;

    private bool _disposed;
    private int _playedFrames;
    private int _starvedFrames;

    /// <param name="negotiated">Итог согласования SDP: кодек, адрес, порт, направление.</param>
    /// <param name="reservation">Занятая пара портов. Сессия её и освобождает.</param>
    /// <param name="bus">
    /// Общий аудиотракт приложения. Без него сессия заводит свой собственный, и
    /// это не поблажка вызывающему: так работают стенды и тесты, у которых
    /// разговор ровно один. Внутри приложения передавать общий обязательно —
    /// линий до трёх, а звуковая карта у них одна.
    /// </param>
    /// <param name="audio">
    /// Настройки тракта: устройства, АРУ, громкость. Кодек и пакетное время
    /// берутся из согласования и затирают то, что пришло сюда: договорённость с
    /// сервером сильнее настройки.
    /// </param>
    public MediaSession(
        NegotiatedMedia negotiated,
        RtpPortReservation reservation,
        VoiceAudioBus? bus = null,
        VoiceAudioConfiguration? audio = null)
    {
        ArgumentNullException.ThrowIfNull(negotiated);
        ArgumentNullException.ThrowIfNull(reservation);

        _reservation = reservation;
        LocalPort = reservation.RtpPort;

        _audioConfiguration = (audio ?? new VoiceAudioConfiguration()) with
        {
            Codec = negotiated.Codec,
            PacketTimeMilliseconds = negotiated.PacketTimeMilliseconds,
        };

        // Запас буфера считается от запаса тракта, а не берётся по умолчанию.
        //
        // Тракт наполняет своё кольцо на несколько кадров вперёд
        // (<see cref="VoiceAudioConfiguration.PlaybackLeadFrames"/>), то есть
        // просит звук раньше, чем тот успевает прийти по сети. Буфер, набравший
        // меньше этого запаса, опустошается на первом же наполнении кольца — а
        // дальше работает храповик: на пустом буфере отдаётся сокрытие,
        // ожидаемый номер уходит вперёд, и настоящий пакет, пришедший через
        // двадцать миллисекунд, объявляется опоздавшим и выбрасывается.
        // Обратного хода у этого нет: буфер выходит из цикла только полным
        // перенабором, теряя всё, что пришло за это время.
        //
        // На живом звонке это стоило 425 выброшенных пакетов из 738 и 432
        // сокрытий на пятнадцати секундах речи — разговор слышен, но больше
        // половины его повторы. Единица сверху — на неровность прихода;
        // адаптация по джиттеру дальше подстроит сама.
        int lead = _audioConfiguration.PlaybackLeadFrames;
        _jitter = new JitterBuffer(
            targetDepth: lead + 2,
            minimumDepth: lead + 1,
            codec: negotiated.Codec,
            packetTimeMilliseconds: negotiated.PacketTimeMilliseconds);

        if (bus is null)
        {
            _bus = new VoiceAudioBus(
                new WasapiVoiceAudioEngine(_audioConfiguration),
                configuration => new WasapiVoiceAudioEngine(configuration));
            _ownsBus = true;
        }
        else
        {
            _bus = bus;
        }

        // Сокеты берутся у резервации готовыми, а не создаются заново.
        //
        // Раньше здесь стояла активация: проверочные сокеты закрывались, и
        // рабочие тут же вставали на те же порты. Между этими двумя шагами порт
        // свободен для всей машины, и внутрипроцессный учёт занятых портов
        // такой зазор не закрывает — соседний процесс про наш список не знает.
        // Теперь порт не отпускается ни на мгновение: тот самый сокет, которым
        // резервация его держала, и становится сокетом разговора.
        _transport = MakeTransport(negotiated, reservation);

        // Обработчики вешаются здесь, а не в Start, и это не перестановка ради
        // порядка. В оригинале отсутствие одной такой строки — назначения
        // `rtp.onFailure` — и было ошибкой, из-за которой отказ сокета не
        // доезжал даже в журнал: пока проводка живёт отдельным шагом, её можно
        // забыть, а забытую заметить нечем. В конструкторе она обязательна по
        // построению, и непровязанной сессии больше не существует.
        //
        // Раньше Start ничего не случится: приём ещё не запущен, и звать
        // обработчики некому.
        WireTransport();
    }

    /// <summary>
    /// Локальный порт RTP. Нужен приложению: повторное предложение обязано
    /// нести тот же порт.
    /// </summary>
    public ushort LocalPort { get; }

    /// <summary>
    /// Куда писать подробности о форматах звука.
    ///
    /// Обработчики хранятся у сессии, а не у тракта: тракт общий, и висящее на
    /// нём замыкание фоновой линии писало бы в журнал за чужой разговор.
    /// </summary>
    public Action<string>? OnDiagnostic { get; set; }

    /// <summary>Распакованные отсчёты принятого звука — для диагностики.</summary>
    public Action<short[]>? OnDecodedSamples { get; set; }

    /// <summary>События тракта: пересборка после смены устройства, смена маршрута.</summary>
    public Action<VoiceAudioEvent>? OnAudioEvent { get; set; }

    /// <summary>
    /// Отказ транспорта медиа: сокет, отправка.
    ///
    /// Отдельно от <see cref="OnDiagnostic"/>, потому что это не подробность про
    /// формат, а причина, по которой разговор молчит, — и уровень у неё другой.
    /// Сигнализация в этот момент цела: диалог живёт, на экране «Разговор». Без
    /// этой строки разбор жалобы «звук был, потом пропал» начинается с пустого
    /// места.
    /// </summary>
    public Action<string>? OnTransportFailure { get; set; }

    /// <summary>Что собеседник видит про наш поток. Приезжает раз в пять секунд.</summary>
    public Action<RemoteMediaView>? OnRemoteView { get; set; }

    /// <summary>Последний отчёт собеседника о нашем потоке, если он был.</summary>
    public RemoteMediaView? RemoteView
    {
        get
        {
            lock (_transportGate)
            {
                return _remoteView;
            }
        }
    }

    /// <summary>Параметры, о которых договорились в последний раз.</summary>
    public NegotiatedMedia? Negotiated
    {
        get
        {
            lock (_transportGate)
            {
                return _transport?.Negotiated;
            }
        }
    }

    /// <summary>Наш ли сейчас общий тракт.</summary>
    public bool OwnsAudio => _bus.IsOwner(_token);

    /// <summary>Работает ли аудиотракт этой линии.</summary>
    public bool IsAudioActive => _isAudioRunning;

    /// <summary>
    /// Немой микрофон. Работает и как удержание, и как отдельная кнопка.
    ///
    /// Кадр не просто выбрасывается: метка времени потока всё равно сдвигается
    /// на его длительность (<see cref="RtpSession.SkipFrame"/>). Молчание — это
    /// не остановка часов, и собеседник, получив после паузы пакет со старой
    /// меткой, услышит не тишину, а рассинхронизацию.
    /// </summary>
    public bool IsMicrophoneMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    /// <summary>Отдавать принятое в звук.</summary>
    public bool IsReceivingAudio
    {
        get => _receivesAudio;
        set
        {
            _receivesAudio = value;

            // Буфер чистится на входе в удержание: то, что в нём лежит, к
            // возврату уже безнадёжно старое.
            if (!value)
            {
                lock (_bufferGate)
                {
                    _jitter.Reset();
                }
            }
        }
    }

    /// <summary>
    /// Разговор на удержании: микрофон нем, принятое в звук не идёт.
    ///
    /// Обе стороны глушатся сразу, независимо от того, что написано в SDP.
    /// Направление в SDP — договорённость с сервером о том, кому что отправлять;
    /// удержание же означает, что оператор ушёл к другому собеседнику, и ни его
    /// голос, ни музыка ожидания сюда попасть не должны.
    /// </summary>
    public bool IsHeld
    {
        get => IsMicrophoneMuted && !IsReceivingAudio;
        set
        {
            IsMicrophoneMuted = value;
            IsReceivingAudio = !value;
        }
    }

    /// <summary>Согласован ли telephone-event. Если нет, тоны отправить нечем.</summary>
    public bool SupportsTelephoneEvents
    {
        get
        {
            lock (_transportGate)
            {
                return _transport?.Configuration.TelephoneEventPayloadType is not null;
            }
        }
    }

    /// <summary>
    /// Задержка разговора прямо сейчас, разложенная по буферам.
    ///
    /// Глубина буфера берётся текущая, а не целевая: цель — это то, что мы
    /// хотим держать, а слышно то, что лежит.
    /// </summary>
    public MediaLatency Latency
    {
        get
        {
            double jitterMilliseconds;
            lock (_bufferGate)
            {
                jitterMilliseconds = _jitter.Depth * (double)_audioConfiguration.PacketTimeMilliseconds;
            }

            AudioLatencySnapshot tract = default;
            _bus.TryWithEngine(_token, engine => engine.Latency, out tract);

            return new MediaLatency(
                tract.CaptureMilliseconds,
                jitterMilliseconds,
                tract.PlaybackMilliseconds,
                RemoteView?.RoundTripTime?.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Проверочный сигнал вместо микрофона.
    ///
    /// Пока он задан, в линию уходит он, а не то, что услышал микрофон. Нужен
    /// замеру задержки: измерение сравнивает отправленное с вернувшимся, а в
    /// тихой комнате отправлять нечего — шумодав честно сводит фон к нулю, и
    /// сравнивать оказывается нечего с чем. Той же ручкой на этапе W8
    /// пользуется проверка «меня слышно?».
    ///
    /// Захват при этом продолжает работать: эхоподавитель обязан видеть, что
    /// творится в комнате, иначе его настройка на время проверки перестаёт
    /// соответствовать разговору.
    /// </summary>
    public AudioTestSignal? OutgoingTestSignal { get; set; }

    /// <summary>
    /// Кадр, ушедший в линию, — как есть, кодированным.
    ///
    /// Нужен замеру задержки: сравнивать надо то, что мы отправили, с тем, что
    /// вернулось, а другого места, где виден отправленный звук, нет. Зовётся с
    /// потока захвата — не блокировать.
    /// </summary>
    public Action<ReadOnlyMemory<byte>>? OnSentFrame { get; set; }

    /// <summary>Статистика джиттер-буфера.</summary>
    public JitterStatistics Statistics
    {
        get
        {
            lock (_bufferGate)
            {
                return _jitter.Statistics;
            }
        }
    }

    /// <summary>
    /// Собирает предложение SDP и занимает порт под RTP.
    ///
    /// Порт занимается ДО отправки INVITE: номер уходит в предложении, и узнать
    /// его потом уже негде. Возвращается сам объект предложения, а не только
    /// байты: разбор ответа сверяется с ним, чтобы понять итоговое направление
    /// потока.
    /// </summary>
    public static (SessionDescription Offer, RtpPortReservation Reservation) MakeOffer(
        string localAddress,
        IReadOnlyList<AudioCodec>? codecs = null,
        int packetTimeMilliseconds = AudioCodecInfo.DefaultPacketTimeMilliseconds,
        MediaSecurityPolicy security = MediaSecurityPolicy.None)
    {
        RtpPortReservation reservation = RtpPortReservation.Reserve();
        try
        {
            SessionDescription offer = SdpNegotiator.MakeOffer(
                localAddress,
                reservation.RtpPort,
                codecs,
                packetTimeMilliseconds: packetTimeMilliseconds,
                security: security);
            return (offer, reservation);
        }
        catch
        {
            reservation.Release();
            throw;
        }
    }

    /// <summary>
    /// Отвечает на чужое предложение и занимает порт под RTP.
    ///
    /// Зеркало <see cref="MakeOffer"/>, и порядок здесь так же обязателен: порт
    /// нужен уже в ответе. Разница в том, что выбор кодека сделан не нами —
    /// предложение задаёт рамки, а мы в них укладываемся.
    ///
    /// Требование защиты проверяется отдельно от согласования: открытое
    /// предложение на защищённом профиле надо отклонить звонком, а не принять
    /// молча. Незаметный откат на открытый RTP — ровно то, от чего защищались.
    /// </summary>
    public static (SessionDescription Answer, NegotiatedMedia Media, RtpPortReservation Reservation) MakeAnswer(
        SessionDescription offer,
        string localAddress,
        IReadOnlyList<AudioCodec>? codecs = null,
        int packetTimeMilliseconds = AudioCodecInfo.DefaultPacketTimeMilliseconds,
        MediaSecurityPolicy security = MediaSecurityPolicy.None)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (security is MediaSecurityPolicy.SdesRequired
            && !string.Equals(offer.Audio?.ProtocolName, "RTP/SAVP", StringComparison.OrdinalIgnoreCase))
        {
            throw new SdpNegotiationException(SdpNegotiationFailure.SecureMediaRequired);
        }

        RtpPortReservation reservation = RtpPortReservation.Reserve();
        try
        {
            (SessionDescription answer, NegotiatedMedia media) = SdpNegotiator.MakeAnswer(
                offer,
                localAddress,
                reservation.RtpPort,
                codecs,
                packetTimeMilliseconds: packetTimeMilliseconds);
            return (answer, media, reservation);
        }
        catch
        {
            reservation.Release();
            throw;
        }
    }

    /// <summary>Поднимает поток RTP и забирает аудиотракт.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Резервация активирована ещё в конструкторе, вместе с сокетами: см.
        // причину там же. Здесь остаётся начать приём и забрать тракт.
        StartTransport();
        try
        {
            ClaimAudio();
        }
        catch
        {
            Stop();
            throw;
        }

        _isAudioRunning = true;
    }

    /// <summary>
    /// <see cref="Start"/>, не задерживающий вызывающего.
    ///
    /// Подъём тракта стоит до 626 мс на открытии устройства (замер W4), и всё
    /// это время интерфейс стоял бы: панель не перерисовывается, кнопки не
    /// отвечают. Ждать всё равно приходится — 200 OK уходит после подъёма медиа,
    /// иначе первые кадры Asterisk летят в закрытый порт, — но ждать должен тот,
    /// кто позвал, а не весь интерфейс.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(Start, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Lifecycle.Release();
        }
    }

    /// <summary>
    /// <see cref="Stop"/>, не задерживающий вызывающего.
    ///
    /// Снятие дороже подъёма: остановка RTP ждёт закрытия сокета, тракт
    /// разбирается синхронно. Столько интерфейс не отвечал бы после отбоя —
    /// ровно в тот момент, когда оператор набирает следующий номер и жмёт по
    /// неотвечающей панели ещё раз.
    /// </summary>
    public async Task StopAsync()
    {
        await Lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(Stop).ConfigureAwait(false);
        }
        finally
        {
            Lifecycle.Release();
        }
    }

    /// <summary>
    /// Поднимает поток RTP, не трогая звуковую карту.
    ///
    /// Существует ради проверок: <see cref="Start"/> забирает тракт, а звуковой
    /// карты в сборочной машине нет. Всё, что касается приёма пакетов — фильтр
    /// источника, джиттер-буфер, счётчики, отчёты RTCP, — проверяется без
    /// единого звука.
    ///
    /// Прикладному коду не нужен и потому не публичен: ему нужен разговор
    /// целиком, а разговор без звука разговором не является.
    /// </summary>
    internal void StartWithoutAudio() => StartTransport();

    public void Stop()
    {
        CancelDtmf();
        _isAudioRunning = false;
        ReleaseAudio();

        Transport? current;
        lock (_transportGate)
        {
            current = _transport;
        }

        if (current is not null)
        {
            current.Rtcp.Stop();
            current.Rtp.Stop();
        }

        lock (_bufferGate)
        {
            _jitter.Reset();
        }

        _reservation.Release();
    }

    /// <summary>
    /// Отпускает звуковую карту, не трогая ни RTP, ни резервацию порта.
    ///
    /// Нужно многолинейности: тракт у оператора один, а разговоров до трёх.
    /// Держать три захвата на одном устройстве нельзя, а Bluetooth-гарнитуру они
    /// ещё и удерживают в режиме связи всё время, пока жива хоть одна линия.
    /// Сигнализация при этом продолжается полностью: линия остаётся в диалоге и
    /// держит свою пару портов, так что вернуть её в разговор можно без
    /// пересогласования.
    /// </summary>
    public void SuspendAudio()
    {
        // Без проверки «а был ли запущен»: линия могла уйти в фон и до того, как
        // поднялся её тракт, и заглушить её надо всё равно.
        _isAudioRunning = false;

        // Порядок тот же, что при удержании: сначала замолчать, потом отпускать
        // устройство. Иначе последний захваченный кадр успевает уйти в линию уже
        // после того, как оператор переключился на другую.
        IsHeld = true;
        ReleaseAudio();

        lock (_bufferGate)
        {
            _jitter.Reset();
        }
    }

    /// <summary>
    /// Возвращает линию в разговор: тракт собирается заново на том же потоке
    /// RTP. Слышно как короткий провал — ровно как при смене устройства посреди
    /// разговора, и по той же причине: тракт собирается с нуля.
    ///
    /// Условие выхода — владение трактом, а не собственный признак «я
    /// запущена». Линия может считать себя работающей и при этом не владеть
    /// трактом: его успела забрать другая. По признаку такая линия молча не
    /// вернула бы себе звук, а на экране оставалось бы «Разговор».
    /// </summary>
    public void ResumeAudio()
    {
        if (OwnsAudio)
        {
            return;
        }

        ClaimAudio();
        _isAudioRunning = true;
        IsHeld = false;
    }

    /// <summary>
    /// Применяет согласованное направление к звуку.
    ///
    /// Направление — это то, о чём договорились в SDP, и оно ортогонально
    /// удержанию по нашей воле: сервер может поставить нас на удержание сам
    /// (<c>a=sendonly</c> с его стороны), и тогда молчать надо, даже если кнопку
    /// никто не нажимал.
    /// </summary>
    public void Apply(MediaDirection direction)
    {
        IsMicrophoneMuted = !direction.SendsAudio();
        IsReceivingAudio = direction.ReceivesAudio();
    }

    /// <summary>
    /// Применяет новые параметры потока к идущему разговору.
    ///
    /// Дешёвый случай — сменилось только направление: удержание и возврат из
    /// него в подавляющем большинстве случаев именно такие, и трогать ради них
    /// звуковую карту нельзя. Пересборка тракта слышна как провал, а на
    /// Bluetooth-гарнитуре стоит ещё и переключения режима.
    ///
    /// Дорогой случай — собеседник вернулся с другого адреса или порта. Тогда
    /// поток RTP пересобирается целиком, но на том же локальном порту и с тем же
    /// трактом: порт мы объявили в SDP, и менять его посреди диалога нельзя.
    /// </summary>
    public MediaRenegotiation Renegotiate(NegotiatedMedia updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        Transport? current;
        lock (_transportGate)
        {
            current = _transport;
        }

        if (current is null)
        {
            return MediaRenegotiation.DirectionOnly;
        }

        if (current.Negotiated.Codec != updated.Codec)
        {
            throw new MediaCodecChangedException(current.Negotiated.Codec, updated.Codec);
        }

        // Старая запись удержания — адрес 0.0.0.0 или нулевой порт. Пересобирать
        // сокет на такой адрес нельзя и не нужно: она означает «мне сейчас
        // ничего не шли», а не «шли вот сюда». А вот удержание по a=sendonly
        // адрес сохраняет и вполне может его сменить: музыку ожидания Asterisk
        // отдаёт со своего порта, и не всегда с прежнего.
        bool endpointChanged = !updated.IsStreamDisabled
            && (updated.RemoteAddress != current.Negotiated.RemoteAddress
                || updated.RemotePort != current.Negotiated.RemotePort);

        if (!endpointChanged)
        {
            Apply(updated.Direction);
            lock (_transportGate)
            {
                _transport = current with { Negotiated = updated };
            }

            return MediaRenegotiation.DirectionOnly;
        }

        // <b>Поток не пересобирается — он перенацеливается.</b>
        //
        // Сначала здесь стояла пересборка, как в оригинале: новая пара сокетов
        // на тех же локальных портах. На Windows это оказалось хуже вдвойне.
        // Порт между закрытием старого сокета и привязкой нового свободен для
        // всей машины — соседний процесс займёт его совершенно законно, и
        // разговор останется без звука. А вместе с сокетом пропадали бы номер
        // последовательности и метка времени: собеседник увидел бы обрыв
        // потока там, где всего лишь сменилось плечо.
        //
        // Смена адреса — это ровно одно поле в сокете отправки, и меняется
        // только оно. Ни кадра не теряется, ни порт не отпускается.
        current.Rtp.Retarget(updated.RemoteAddress, updated.RemotePort);
        current.Rtcp.Retarget(updated.RemoteAddress, (ushort)(updated.RemotePort + 1));

        lock (_transportGate)
        {
            _transport = current with { Negotiated = updated };
            _remoteView = null;
        }

        lock (_bufferGate)
        {
            _jitter.Reset();
        }

        // Собеседник на новом плече — заново неизвестно кто: поток он мог
        // пересобрать вместе с адресом, и прежний SSRC про него ничего не
        // говорит.
        _remoteSource.Forget();
        Apply(updated.Direction);

        return MediaRenegotiation.StreamRebuilt;
    }

    /// <summary>
    /// Отправляет набор DTMF по RFC 4733.
    ///
    /// Возвращает <c>false</c>, если собеседник telephone-event не подтвердил:
    /// молча проглотить нажатие нельзя — оператор будет думать, что попал в
    /// меню, а на той стороне не произошло ничего.
    ///
    /// Нажатия, пришедшие во время передачи, встают в очередь, а не начинают
    /// второй тон поверх первого. Оператор набирает быстрее, чем идёт тон, и без
    /// очереди половина цифр терялась бы.
    /// </summary>
    public bool SendDtmf(DtmfSequence sequence, DtmfTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        if (!SupportsTelephoneEvents)
        {
            return false;
        }

        if (sequence.IsEmpty)
        {
            return true;
        }

        Enqueue(new DtmfJob(sequence, EffectiveTiming(timing), Completion: null));
        return true;
    }

    /// <summary>Отправляет один символ: цифру, звёздочку или решётку.</summary>
    public bool SendDtmf(char character, DtmfTiming? timing = null) =>
        TelephoneEventPayload.EventCode(character) is byte code
        && SendDtmf(new DtmfSequence([new DtmfStep.Tone(code)]), timing);

    /// <summary>
    /// Ждёт, пока последовательность действительно выйдет из очереди RTP.
    ///
    /// Это не подтверждение того, что сервер команду принял, но уже не простое
    /// «поставлено в очередь». При остановке сессии возвращает <c>false</c>.
    /// </summary>
    public Task<bool> SendDtmfAndWaitAsync(DtmfSequence sequence, DtmfTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        if (!SupportsTelephoneEvents)
        {
            return Task.FromResult(false);
        }

        if (sequence.IsEmpty)
        {
            return Task.FromResult(true);
        }

        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new DtmfJob(sequence, EffectiveTiming(timing), completion));
        return completion.Task;
    }

    /// <summary>Сводка разговора для журнала и приёмки.</summary>
    public string Summary()
    {
        JitterStatistics stats = Statistics;
        AudioCodec codec;
        double jitterMilliseconds;
        int targetDepth;

        lock (_transportGate)
        {
            codec = _transport?.Configuration.Codec ?? AudioCodec.Pcmu;
        }

        lock (_bufferGate)
        {
            jitterMilliseconds = _jitter.JitterMilliseconds;
            targetDepth = _jitter.TargetDepth;
        }

        // Длительность считается по кадрам, которые запросила звуковая карта:
        // это единственные часы в разговоре, которые нельзя оспорить. Ни
        // системное время, ни число принятых пакетов такой опорой не служат —
        // первое не знает про уход часов устройства, второе про потери.
        int played = Volatile.Read(ref _playedFrames);
        double seconds = played * (double)_audioConfiguration.PacketTimeMilliseconds / 1000;

        string summary = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"принято {stats.Received}, спрятано {stats.Concealed}, не по порядку {stats.Reordered}, "
                + $"опоздало {stats.Late}, выброшено {stats.Dropped}, дублей {stats.Duplicated}, "
                + $"недоборов {stats.Underruns}, проиграно {seconds:F1} с, "
                + $"пустых рендеров {Volatile.Read(ref _starvedFrames)}, "
                + $"джиттер {jitterMilliseconds:F1} мс, запас {targetDepth} кадр., кодек {codec.SdpName()}");

        return RemoteView is RemoteMediaView view ? $"{summary} | у собеседника: {view.Summary}" : summary;
    }

    /// <summary>
    /// Сессию нельзя выбросить работающей.
    ///
    /// <see cref="Stop"/> идемпотентен, поэтому обычный путь (снятие линии) от
    /// этого ничего не теряет. Нужно для путей, где ссылка теряется молча:
    /// звонок, успевший закончиться, пока поднимался звук, — тогда и порт, и
    /// сокет RTP, и звуковое устройство остались бы занятыми до выхода из
    /// приложения.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        Transport? current;
        lock (_transportGate)
        {
            current = _transport;
            _transport = null;
        }

        current?.Rtcp.Dispose();
        current?.Rtp.Dispose();
        _dtmfCancellation?.Dispose();

        // Свой тракт закрывается вместе с сессией, общий — нет: его дали на
        // время разговора, и закрыть его значило бы отобрать устройство у линии,
        // которая говорит прямо сейчас.
        if (_ownsBus)
        {
            _bus.Dispose();
        }
    }

    /// <summary>Собирает пару RTP и RTCP на портах, которые держит резервация.</summary>
    private static Transport MakeTransport(NegotiatedMedia negotiated, RtpPortReservation reservation)
    {
        ushort localPort = reservation.RtpPort;
        RtpSessionConfiguration configuration = RtpSessionConfiguration.FromNegotiated(negotiated);

        // Выключенное плечо (нулевой порт, адрес 0.0.0.0) адреса для отправки не
        // даёт вовсе, а сокет всё равно нужен: диалог жив, и собеседник вернётся
        // повторным предложением. Поток в этом случае направляется на себя —
        // принимать он продолжает как обычно, а отправлять ему нечего и некуда.
        bool disabled = negotiated.IsStreamDisabled;
        string remoteAddress = disabled ? "127.0.0.1" : negotiated.RemoteAddress;
        ushort remotePort = disabled ? localPort : negotiated.RemotePort;

        RtpSession rtp = new(configuration, localPort, remoteAddress, remotePort, reservation.TakeRtpSocket());

        // RTCP живёт на порту RTP плюс один — RFC 3550 §11. Ради этого порт под
        // RTP и выбирается чётным.
        //
        // Собирается после RTP, потому что отчёты подписываются тем же SSRC, что
        // и поток: с чужим номером собеседник не свяжет одно с другим и просто
        // выбросит наши отчёты, а выглядеть это будет как исправный обмен с
        // пустой статистикой.
        RtcpSession rtcp = new(
            rtp.SynchronizationSource,
            $"elitesip@{localPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            negotiated.Codec.RtpClockRate(),
            (ushort)(localPort + 1),
            remoteAddress,
            (ushort)(remotePort + 1),
            reservation.TakeRtcpSocket());

        return new Transport(rtp, rtcp, configuration, negotiated);
    }

    /// <summary>
    /// Вешает обработчики на текущий поток RTP и RTCP.
    ///
    /// Зовётся из конструктора — чтобы непровязанной сессии не бывало вовсе — и
    /// из <see cref="Renegotiate"/>, где поток подменяется целиком, а звук и
    /// буфер остаются те же. Обработчик приёма намеренно замыкается на свой тип
    /// нагрузки события, а не читает его из общего состояния: иначе каждый
    /// принятый пакет брал бы замок, который в этот момент держит
    /// пересогласование.
    /// </summary>
    private void WireTransport()
    {
        Transport? current;
        lock (_transportGate)
        {
            current = _transport;
        }

        if (current is null)
        {
            return;
        }

        byte? eventPayloadType = current.Configuration.TelephoneEventPayloadType;
        RtpSession rtp = current.Rtp;
        RtcpSession rtcp = current.Rtcp;

        rtp.OnReceivedPacket = packet =>
        {
            // События DTMF в звук не отдаём: их полезная нагрузка — не аудио, и
            // декодированная как G.711 она превратится в громкий треск.
            if (eventPayloadType is byte type && packet.PayloadType == type)
            {
                return;
            }

            // На удержании принятое просто выбрасывается. Копить его в буфере
            // нельзя: к возврату в разговор там будет минута протухшей музыки,
            // которую оператор услышит вместо собеседника.
            if (!_receivesAudio)
            {
                return;
            }

            switch (_remoteSource.Admit(packet.Ssrc))
            {
                case RemoteSourceVerdict.Known:
                    break;

                case RemoteSourceVerdict.Foreign:
                    // Молча: чужой поток может идти сколько угодно, и строка на
                    // каждый его пакет залила бы журнал вместо того, чтобы о
                    // чём-то сообщить.
                    return;

                case RemoteSourceVerdict.Adopted:
                    // Источник сменился по-настоящему. Буфер выбрасывается
                    // вместе с ним: в нём лежат кадры прежнего потока, а номера
                    // последовательности у нового свои, и склеивать одно с
                    // другим по номерам — верный способ получить кашу вместо
                    // речи.
                    lock (_bufferGate)
                    {
                        _jitter.Reset();
                    }

                    OnDiagnostic?.Invoke($"источник потока сменился, SSRC {packet.Ssrc}");
                    break;

                default:
                    break;
            }

            lock (_bufferGate)
            {
                _jitter.Push(packet);
            }
        };

        rtp.OnFailure = reason => OnTransportFailure?.Invoke(reason);

        rtcp.StatisticsProvider = () =>
        {
            (uint packets, uint octets, uint timestamp, uint _) = rtp.SendStatistics;

            lock (_bufferGate)
            {
                return new LocalMediaStatistics
                {
                    PacketsSent = packets,
                    OctetsSent = octets,
                    RtpTimestamp = timestamp,
                    RemoteSsrc = _remoteSource.Accepted,
                    FractionLost = _jitter.FractionLostSinceLastReport(),
                    CumulativeLost = _jitter.CumulativePacketsLost,
                    HighestSequenceNumber = _jitter.ExtendedHighestSequenceNumber,
                    Jitter = _jitter.JitterInClockUnits,
                };
            }
        };

        // Журнал RTCP выводится там же, где форматы звука: провал привязки порта
        // и прощание собеседника иначе не видны ничем.
        rtcp.OnDiagnostic = message => OnDiagnostic?.Invoke(message);

        rtcp.OnRemoteView = view =>
        {
            lock (_transportGate)
            {
                _remoteView = view;
            }

            OnRemoteView?.Invoke(view);
        };
    }

    private void StartTransport()
    {
        Transport? current;
        lock (_transportGate)
        {
            current = _transport;
        }

        current?.Rtp.Start();

        // На защищённом потоке RTCP выключен намеренно.
        //
        // По RFC 3711 §3.4 отчёты должны идти как SRTCP — со своим набором
        // ключей (метки вывода 3, 4, 5 вместо 0, 1, 2), собственным индексом и
        // обязательной аутентификацией. Этого нет ни здесь, ни в оригинале.
        // Отправлять открытые отчёты рядом с шифрованным звуком нельзя:
        // собеседник их всё равно отбросит, а наружу уйдут SSRC, счётчики
        // пакетов и тайминги разговора, который мы только что взялись прятать.
        //
        // Цена названа прямо: на защищённом профиле не видно, как нас слышит
        // собеседник, — ни потерь, ни джиттера, ни времени кругового обхода.
        if (current is { Rtp.IsSecured: true })
        {
            OnDiagnostic?.Invoke("RTCP выключен: поток защищён, а SRTCP не написан");
            return;
        }

        current?.Rtcp.Start();
    }

    private RtpSession? CurrentRtp
    {
        get
        {
            lock (_transportGate)
            {
                return _transport?.Rtp;
            }
        }
    }

    /// <summary>
    /// Собирает обработчики для общего тракта.
    ///
    /// Каждый проверяет, что звук всё ещё наш. Проверка не лишняя: между снятием
    /// владения и остановкой тракта помещается уже начатый вызов из потока
    /// захвата, и без неё фоновая линия успела бы отправить свой кадр в чужой
    /// разговор.
    /// </summary>
    private VoiceAudioHandlers MakeHandlers() => new()
    {
        Diagnostic = message => OnDiagnostic?.Invoke(message),
        Event = value => OnAudioEvent?.Invoke(value),
        DecodedSamples = samples => OnDecodedSamples?.Invoke(samples),

        EncodedFrame = payload =>
        {
            if (!_ownsAudio)
            {
                return;
            }

            RtpSession? rtp = CurrentRtp;
            if (rtp is null)
            {
                return;
            }

            // Пока идёт событие DTMF, звук в линию не уходит.
            //
            // Не из экономии: все пакеты одного нажатия обязаны нести одну и ту
            // же метку времени (RFC 4733 §2.5.1), а каждый отправленный кадр
            // звука её двигает. Кадр посреди тона превращает одно нажатие в
            // серию коротких, и голосовое меню на той стороне слышит мусор.
            // Метку за это время сдвинет сам тон — здесь её трогать нельзя.
            if (_isSendingTone)
            {
                return;
            }

            if (_isMuted)
            {
                rtp.SkipFrame();
                return;
            }

            // Проверочный сигнал подменяет микрофон здесь, а не выше по тракту:
            // так он проходит ровно тот же путь, что и голос, — кодек, RTP,
            // сеть, — и меряется именно этот путь, а не наша выдумка о нём.
            ReadOnlyMemory<byte> outgoing = OutgoingTestSignal?.NextFrame() ?? payload;

            rtp.Send(outgoing);
            OnSentFrame?.Invoke(outgoing);
        },

        // Такт воспроизведения задаёт звуковая карта, а не таймер: кадр просят
        // ровно тогда, когда карте нужны отсчёты. В оригинале здесь стоял сон на
        // 20 мс, и он плыл — за 11 секунд разговора уходило 465 кадров вместо
        // 550.
        NeedsFrame = () =>
        {
            if (!_ownsAudio)
            {
                return null;
            }

            JitterFrame? frame;
            lock (_bufferGate)
            {
                frame = _jitter.Pop();
            }

            if (frame is null)
            {
                Interlocked.Increment(ref _starvedFrames);
                return null;
            }

            Interlocked.Increment(ref _playedFrames);
            return new PlaybackFrame(frame.Payload, frame.IsConcealment);
        },
    };

    private void ClaimAudio()
    {
        _bus.Claim(_token, _audioConfiguration, MakeHandlers());
        _ownsAudio = true;
    }

    private void ReleaseAudio()
    {
        // Порядок обязателен, см. <see cref="_ownsAudio"/>: сначала перестать
        // считать звук своим, потом отпускать шину.
        _ownsAudio = false;
        _bus.Release(_token);
    }

    private DtmfTiming EffectiveTiming(DtmfTiming? requested)
    {
        // Такт отправки берётся из согласованного потока, а не у вызывающего:
        // длительность тона и паузы — дело настроек, а пакетное время — дело
        // договорённости с сервером, и знать его снаружи неоткуда.
        int packetTime;
        lock (_transportGate)
        {
            packetTime = _transport?.Configuration.PacketTimeMilliseconds
                ?? AudioCodecInfo.DefaultPacketTimeMilliseconds;
        }

        return (requested ?? new DtmfTiming()) with { PacketTimeMilliseconds = packetTime };
    }

    private void Enqueue(DtmfJob job)
    {
        lock (_dtmfGate)
        {
            _dtmfQueue.Add(job);

            // Проверка очереди и создание рабочего — под одним замком. Иначе
            // новый тон попадает между «очередь пуста» и обнулением задачи и
            // остаётся без того, кто его отправит.
            if (_dtmfWorker is not null)
            {
                return;
            }

            _dtmfCancellation?.Dispose();
            _dtmfCancellation = new CancellationTokenSource();
            CancellationToken token = _dtmfCancellation.Token;
            _dtmfWorker = Task.Run(() => DrainDtmfAsync(token), CancellationToken.None);
        }
    }

    private async Task DrainDtmfAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DtmfJob job;
            lock (_dtmfGate)
            {
                if (_dtmfQueue.Count == 0)
                {
                    // Обнуление рабочего атомарно с проверкой очереди: следующий
                    // набор либо увидит его, либо заведёт новый.
                    _dtmfWorker = null;
                    return;
                }

                job = _dtmfQueue[0];
                _dtmfQueue.RemoveAt(0);
            }

            bool sent = await SendJobAsync(job, cancellationToken).ConfigureAwait(false);
            job.Completion?.TrySetResult(sent);

            if (!sent)
            {
                break;
            }
        }

        lock (_dtmfGate)
        {
            _dtmfWorker = null;
        }

        FailPendingDtmf();
    }

    private async Task<bool> SendJobAsync(DtmfJob job, CancellationToken cancellationToken)
    {
        try
        {
            foreach (DtmfAction action in DtmfPlanner.Actions(job.Sequence, job.Timing))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                switch (action)
                {
                    case DtmfAction.Wait wait:
                        await Task.Delay(wait.Milliseconds, cancellationToken).ConfigureAwait(false);
                        break;

                    case DtmfAction.Send send:
                        if (CurrentRtp is not RtpSession rtp)
                        {
                            return false;
                        }

                        _isSendingTone = true;
                        rtp.SendEvent(send.Packet.Payload, send.Packet.IsFirst);
                        if (send.Packet.CompletesEvent)
                        {
                            // Метка времени досдвигается на всю длительность
                            // тона: внутри события она не росла, но время шло.
                            rtp.FinishEvent(send.Packet.TimestampAdvance);
                            _isSendingTone = false;
                        }

                        break;

                    default:
                        break;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _isSendingTone = false;
        }
    }

    private void CancelDtmf()
    {
        lock (_dtmfGate)
        {
            _dtmfCancellation?.Cancel();
            _dtmfWorker = null;
        }

        FailPendingDtmf();
        _isSendingTone = false;
    }

    private void FailPendingDtmf()
    {
        DtmfJob[] pending;
        lock (_dtmfGate)
        {
            pending = [.. _dtmfQueue];
            _dtmfQueue.Clear();
        }

        foreach (DtmfJob job in pending)
        {
            job.Completion?.TrySetResult(false);
        }
    }

    /// <summary>Всё, что меняется при пересогласовании, — одной записью.</summary>
    private sealed record Transport(
        RtpSession Rtp,
        RtcpSession Rtcp,
        RtpSessionConfiguration Configuration,
        NegotiatedMedia Negotiated);

    /// <summary>
    /// Набор в очереди. Тайминг едет вместе с ним, а не читается на отправке:
    /// набор, вставший в очередь позади чужого, иначе играется чужими
    /// длительностями, а настройки меняются прямо во время разговора.
    /// </summary>
    private sealed record DtmfJob(
        DtmfSequence Sequence,
        DtmfTiming Timing,
        TaskCompletionSource<bool>? Completion);
}
