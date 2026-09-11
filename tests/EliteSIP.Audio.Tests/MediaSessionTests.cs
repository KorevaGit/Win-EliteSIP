using System.Net;
using System.Net.Sockets;
using EliteSIP.MediaCore;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Медиа-половина разговора: приём, фильтр источника, удержание,
/// пересогласование, немой микрофон и DTMF.
///
/// Звуковой карты здесь нет ни одной. Всё, что касается устройства, закрыто
/// заглушкой тракта (см. <see cref="FakeVoiceAudioEngine"/>), а всё, что
/// касается сети, — настоящими сокетами на петле: подделывать UDP значило бы
/// проверять подделку, а петля стоит микросекунды.
///
/// Перенесено по смыслу из
/// <c>Packages/MediaCore/Tests/MediaCoreTests/MediaSessionTests.swift</c>.
/// </summary>
public sealed class MediaSessionTests
{
    private const byte EventPayloadType = TelephoneEvent.DefaultPayloadType;

    /// <summary>Диапазон портов только для этих тестов — см. <c>Fixture.Create</c>.</summary>
    private const ushort TestPortRangeLower = 16600;

    private const ushort TestPortRangeUpper = 16700;

    [Fact]
    public void Предложение_несёт_занятый_порт()
    {
        (SessionDescription offer, RtpPortReservation reservation) = MediaSession.MakeOffer("10.0.0.5");
        using (reservation)
        {
            Assert.Equal(reservation.RtpPort, offer.Audio?.Port);
            Assert.Equal("10.0.0.5", offer.Connection?.Address);

            // Порт обязан уйти в предложение до INVITE: узнать его потом негде.
            Assert.Equal(0, reservation.RtpPort % 2);
        }
    }

    [Fact]
    public void Открытое_предложение_на_защищённом_профиле_отклоняется_а_не_принимается_молча()
    {
        SessionDescription plainOffer = SdpNegotiator.MakeOffer("10.0.0.9", 40000);

        // Молчаливый откат на открытый RTP — ровно то, от чего защита и нужна:
        // разговор бы шёл, а защиты бы не было, и заметить это без снятия
        // трафика невозможно.
        SdpNegotiationException failure = Assert.Throws<SdpNegotiationException>(
            () => MediaSession.MakeAnswer(plainOffer, "10.0.0.5", security: MediaSecurityPolicy.SdesRequired));

        Assert.Equal(SdpNegotiationFailure.SecureMediaRequired, failure.Failure);
    }

    [Fact]
    public async Task Принятый_пакет_доезжает_до_буфера()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.StartWithoutAudio();

        fixture.SendToSession(Packet(sequenceNumber: 1, timestamp: 160, ssrc: 0x1111));

        Assert.Equal(1, await fixture.WaitForReceivedAsync(1));
    }

    [Fact]
    public async Task Чужой_поток_на_нашем_порту_в_буфер_не_попадает()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.StartWithoutAudio();

        fixture.SendToSession(Packet(1, 160, ssrc: 0x1111));
        Assert.Equal(1, await fixture.WaitForReceivedAsync(1));

        // Первый пакет разговора задал источник. Всё, что приходит с чужим SSRC,
        // отбрасывается до тех пор, пока не наберёт свою серию: одиночная
        // подмена столько не живёт.
        for (ushort sequence = 2; sequence <= 4; sequence++)
        {
            fixture.SendToSession(Packet(sequence, 160u * sequence, ssrc: 0x2222));
        }

        await Task.Delay(200);
        Assert.Equal(1, fixture.Session.Statistics.Received);
    }

    [Fact]
    public async Task События_DTMF_в_звук_не_отдаются()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.StartWithoutAudio();

        // Нагрузка события — не аудио. Декодированная как G.711, она
        // превратится в громкий треск в ухе оператора.
        fixture.SendToSession(Packet(1, 160, 0x1111, payloadType: EventPayloadType));
        await Task.Delay(200);

        Assert.Equal(0, fixture.Session.Statistics.Received);
    }

    [Fact]
    public async Task На_удержании_принятое_выбрасывается()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.StartWithoutAudio();
        fixture.Session.IsReceivingAudio = false;

        fixture.SendToSession(Packet(1, 160, 0x1111));
        await Task.Delay(200);

        // Копить это в буфере нельзя: к возврату в разговор там будет минута
        // протухшей музыки ожидания вместо голоса собеседника.
        Assert.Equal(0, fixture.Session.Statistics.Received);
    }

    [Fact]
    public void Смена_кодека_при_пересогласовании_отклоняется()
    {
        using Fixture fixture = Fixture.Create();

        // От кодека зависит вся цепочка звука, включая частоты пересчёта.
        // Пересобрать её на ходу нельзя, и молчать об этом нельзя тем более.
        Assert.Throws<MediaCodecChangedException>(
            () => fixture.Session.Renegotiate(fixture.Negotiated with { Codec = AudioCodec.G722 }));
    }

    [Fact]
    public void Смена_одного_направления_не_трогает_поток()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.StartWithoutAudio();
        ushort port = fixture.Session.LocalPort;

        MediaRenegotiation outcome = fixture.Session.Renegotiate(
            fixture.Negotiated with { Direction = MediaDirection.RecvOnly });

        // Удержание и возврат из него в подавляющем большинстве случаев именно
        // такие. Пересобирать ради них тракт — значит платить провалом звука за
        // строчку в SDP.
        Assert.Equal(MediaRenegotiation.DirectionOnly, outcome);
        Assert.Equal(port, fixture.Session.LocalPort);
        Assert.True(fixture.Session.IsMicrophoneMuted);
        Assert.True(fixture.Session.IsReceivingAudio);
    }

    [Fact]
    public void Смена_адреса_собеседника_пересобирает_поток_на_том_же_порту()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.StartWithoutAudio();
        ushort port = fixture.Session.LocalPort;

        MediaRenegotiation outcome = fixture.Session.Renegotiate(
            fixture.Negotiated with { RemotePort = (ushort)(fixture.Negotiated.RemotePort + 2) });

        Assert.Equal(MediaRenegotiation.StreamRebuilt, outcome);

        // Порт мы объявили в SDP, и менять его посреди диалога нельзя.
        Assert.Equal(port, fixture.Session.LocalPort);
    }

    [Fact]
    public void Старая_запись_удержания_поток_не_пересобирает()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.StartWithoutAudio();

        // Нулевой порт — запись удержания из RFC 2543, которой chan_sip
        // пользуется до сих пор. Она означает «мне сейчас ничего не шли», а не
        // «шли вот сюда»: пересобирать сокет на такой адрес не на что.
        MediaRenegotiation outcome = fixture.Session.Renegotiate(
            fixture.Negotiated with { RemotePort = 0, Direction = MediaDirection.SendOnly });

        Assert.Equal(MediaRenegotiation.DirectionOnly, outcome);
    }

    [Fact]
    public async Task Немой_микрофон_не_отправляет_кадр_но_двигает_метку_времени()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.Start();

        Action<ReadOnlyMemory<byte>> encoded = fixture.Engine.Handlers.EncodedFrame!;
        byte[] frame = new byte[160];

        encoded(frame);
        RtpPacket first = await fixture.WaitForSentAsync();

        fixture.Session.IsMicrophoneMuted = true;
        encoded(frame);
        encoded(frame);

        fixture.Session.IsMicrophoneMuted = false;
        encoded(frame);
        RtpPacket afterSilence = await fixture.WaitForSentAsync();

        // Два пропущенных кадра — это два такта времени, а не остановка часов.
        // Пакет со старой меткой собеседник разберёт не как паузу, а как
        // рассинхронизацию.
        Assert.Equal(first.Timestamp + (3 * 160u), afterSilence.Timestamp);

        // Номер последовательности растёт только на отправленных: пропущенный
        // кадр — это не потерянный пакет, и считать его потерей собеседник не
        // должен.
        Assert.Equal((ushort)(first.SequenceNumber + 1), afterSilence.SequenceNumber);

        // Маркер: после молчания начинается новый участок речи.
        Assert.True(afterSilence.Marker);
    }

    [Fact]
    public void Тракт_берётся_на_запуске_и_отдаётся_на_остановке()
    {
        using Fixture fixture = Fixture.Create();

        fixture.Session.Start();
        Assert.True(fixture.Session.OwnsAudio);
        Assert.True(fixture.Session.IsAudioActive);
        Assert.Equal(1, fixture.Engine.StartCount);

        fixture.Session.Stop();
        Assert.False(fixture.Session.OwnsAudio);
        Assert.False(fixture.Session.IsAudioActive);
    }

    [Fact]
    public void Фоновая_линия_отдаёт_карту_но_остаётся_в_разговоре()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.Start();

        fixture.Session.SuspendAudio();

        // Устройство отдано, сигнализация цела: линия держит свою пару портов и
        // вернётся в разговор без пересогласования.
        Assert.False(fixture.Session.OwnsAudio);
        Assert.True(fixture.Session.IsHeld);
        Assert.NotNull(fixture.Session.Negotiated);

        fixture.Session.ResumeAudio();
        Assert.True(fixture.Session.OwnsAudio);
        Assert.False(fixture.Session.IsHeld);
    }

    [Fact]
    public void Кадр_чужой_линии_в_наш_поток_не_уходит()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.Start();
        Action<ReadOnlyMemory<byte>> encoded = fixture.Engine.Handlers.EncodedFrame!;

        // Между снятием владения и остановкой тракта помещается уже начатый
        // вызов из потока захвата. Без проверки владения фоновая линия успела бы
        // отправить свой кадр в чужой разговор.
        fixture.Session.SuspendAudio();
        encoded(new byte[160]);

        Assert.Empty(fixture.ReceivedFromSession());
    }

    [Fact]
    public void Настройки_на_ходу_доходят_до_тракта_а_кодек_остаётся_согласованным()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.Start();

        // Настройка приходит с кодеком по умолчанию из пустой конфигурации;
        // разговор при этом идёт на том, о чём договорились с сервером.
        fixture.Session.ApplyAudio(new VoiceAudioConfiguration
        {
            Codec = AudioCodec.G722,
            PlaybackVolume = 0.3f,
            AutomaticGainControl = true,
        });

        VoiceAudioConfiguration? applied = fixture.Engine.LastApplied;
        Assert.NotNull(applied);
        Assert.Equal(0.3f, applied.PlaybackVolume);
        Assert.True(applied.AutomaticGainControl);
        Assert.Equal(AudioCodec.Pcmu, applied.Codec);
    }

    [Fact]
    public void Фоновая_линия_запоминает_настройки_и_возвращается_с_ними()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.Start();
        fixture.Session.SuspendAudio();

        fixture.Session.ApplyAudio(new VoiceAudioConfiguration { MicrophoneGain = 1.5f });
        fixture.Session.ResumeAudio();

        // Тракта у фоновой линии нет — применять было некуда, но при возврате
        // она обязана собрать его уже по новым настройкам.
        Assert.Equal(1.5f, fixture.Engine.LastConfiguration?.MicrophoneGain);
    }

    [Fact]
    public void Без_согласованного_события_тон_отправить_нечем()
    {
        using Fixture fixture = Fixture.Create(telephoneEventPayloadType: null);
        fixture.Session.StartWithoutAudio();

        // Молча проглотить нажатие нельзя: оператор будет думать, что попал в
        // меню, а на той стороне не произошло ничего.
        Assert.False(fixture.Session.SupportsTelephoneEvents);
        Assert.False(fixture.Session.SendDtmf('1'));
    }

    [Fact]
    public async Task Тон_идёт_одним_событием_с_общей_меткой_времени()
    {
        using Fixture fixture = Fixture.Create();
        fixture.Session.StartWithoutAudio();

        Assert.True(await fixture.Session.SendDtmfAndWaitAsync(new DtmfSequence("5")));

        RtpPacket[] tone = [.. fixture.ReceivedFromSession().Where(packet => packet.PayloadType == EventPayloadType)];
        Assert.NotEmpty(tone);

        // Все пакеты одного нажатия несут время его начала (RFC 4733 §2.5.1).
        // Растущая метка превращает одно нажатие в серию коротких, и голосовое
        // меню на той стороне слышит мусор.
        Assert.Single(tone.Select(packet => packet.Timestamp).Distinct());

        // Маркер стоит на первом пакете события: по нему принимающая сторона
        // понимает, что начался новый тон, а не продолжается прежний.
        Assert.True(tone[0].Marker);
        Assert.DoesNotContain(tone[1..], packet => packet.Marker);
    }

    [Fact]
    public async Task Запас_буфера_покрывает_то_на_сколько_тракт_просит_вперёд()
    {
        // Дефект, поймавшийся только на живом звонке: тракт наполняет своё
        // кольцо на несколько кадров вперёд, а буфер набирал два. Спрос
        // опережал приход, буфер отдавал сокрытие, ожидаемый номер уходил
        // вперёд — и настоящий пакет, пришедший через двадцать миллисекунд,
        // объявлялся опоздавшим и выбрасывался. Обратного хода у этого нет:
        // 425 выброшенных пакетов из 738 на пятнадцати секундах речи.
        using Fixture fixture = Fixture.Create();
        fixture.Session.Start();

        int lead = new VoiceAudioConfiguration().PlaybackLeadFrames;
        int prepared = lead + 2;

        for (ushort sequence = 1; sequence <= prepared; sequence++)
        {
            fixture.SendToSession(Packet(sequence, 160u * sequence, ssrc: 0x1111));
        }

        Assert.Equal(prepared, await fixture.WaitForReceivedAsync(prepared));

        // Тракт забирает свой запас разом — так он и делает при наполнении
        // кольца. Каждый отданный кадр обязан быть настоящим: сокрытие здесь
        // означает, что буфер ушёл вперёд прихода и разговор поехал.
        Func<PlaybackFrame?> needsFrame = fixture.Engine.Handlers.NeedsFrame!;
        for (int pull = 0; pull < lead; pull++)
        {
            PlaybackFrame? frame = needsFrame();
            Assert.NotNull(frame);
            Assert.False(frame.Value.IsConcealment, $"кадр {pull + 1} из {lead} оказался сокрытием");
        }

        Assert.Equal(0, fixture.Session.Statistics.Concealed);
        Assert.Equal(0, fixture.Session.Statistics.Late);
    }

    [Fact]
    public void Сводка_называет_кодек_и_счётчики()
    {
        using Fixture fixture = Fixture.Create();

        string summary = fixture.Session.Summary();

        Assert.Contains("принято 0", summary, StringComparison.Ordinal);
        Assert.Contains("PCMU", summary, StringComparison.Ordinal);
    }

    private static byte[] Packet(
        ushort sequenceNumber,
        uint timestamp,
        uint ssrc,
        byte payloadType = 0)
        => new RtpPacket(payloadType, sequenceNumber, timestamp, ssrc, new byte[160]).Encoded();

    /// <summary>
    /// Сессия, заглушка тракта и сокет «собеседника» на петле.
    ///
    /// Собеседник настоящий ровно настолько, насколько нужно: он принимает то,
    /// что мы отправили, и отправляет то, что мы должны принять. Всё остальное —
    /// Asterisk, и его очередь в приёмке этапа.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly Socket _peer;
        private readonly RtpPortReservation _reservation;

        private Fixture(
            MediaSession session,
            FakeVoiceAudioEngine engine,
            VoiceAudioBus bus,
            NegotiatedMedia negotiated,
            RtpPortReservation reservation,
            Socket peer)
        {
            Session = session;
            Engine = engine;
            Bus = bus;
            Negotiated = negotiated;
            _reservation = reservation;
            _peer = peer;
        }

        public MediaSession Session { get; }

        public FakeVoiceAudioEngine Engine { get; }

        public VoiceAudioBus Bus { get; }

        public NegotiatedMedia Negotiated { get; }

        public static Fixture Create(byte? telephoneEventPayloadType = EventPayloadType)
        {
            Socket peer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            // Диапазон свой, а не продуктовый по умолчанию. Учёт занятых портов
            // в RtpPortReservation ведётся внутри процесса, а `dotnet test` по
            // решению запускает сборки тестов параллельно — да ещё и рядом
            // может идти живой звонок со стенда. Все трое брали бы порты из
            // одного диапазона, и общий прогон мигал бы отказом привязки в
            // случайной сборке.
            RtpPortReservation reservation = RtpPortReservation.Reserve(TestPortRangeLower, TestPortRangeUpper);
            NegotiatedMedia negotiated = new(
                AudioCodec.Pcmu,
                PayloadType: 0,
                RemoteAddress: "127.0.0.1",
                RemotePort: (ushort)((IPEndPoint)peer.LocalEndPoint!).Port,
                TelephoneEventPayloadType: telephoneEventPayloadType);

            FakeVoiceAudioEngine engine = new();
            VoiceAudioBus bus = new(engine, _ => engine);
            MediaSession session = new(negotiated, reservation, bus);

            return new Fixture(session, engine, bus, negotiated, reservation, peer);
        }

        /// <summary>Отправляет пакет так, как его прислал бы собеседник.</summary>
        public void SendToSession(byte[] data) =>
            _peer.SendTo(data, new IPEndPoint(IPAddress.Loopback, Session.LocalPort));

        /// <summary>Всё, что сессия успела отправить собеседнику.</summary>
        public List<RtpPacket> ReceivedFromSession()
        {
            List<RtpPacket> packets = [];
            byte[] buffer = new byte[2048];

            while (_peer.Available > 0)
            {
                int received = _peer.Receive(buffer);
                packets.Add(RtpPacket.Parse(buffer.AsSpan(0, received)));
            }

            return packets;
        }

        /// <summary>
        /// Ждёт очередной отправленный пакет.
        ///
        /// Ожидание, а не мгновенная проверка: отправка идёт через настоящий
        /// сокет, и требовать от петли синхронности значит получить тест,
        /// который мигает на загруженной машине.
        /// </summary>
        public async Task<RtpPacket> WaitForSentAsync()
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (_peer.Available > 0)
                {
                    byte[] buffer = new byte[2048];
                    int received = _peer.Receive(buffer);
                    return RtpPacket.Parse(buffer.AsSpan(0, received));
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("пакет так и не пришёл");
        }

        /// <summary>Ждёт, пока буфер примет заданное число пакетов.</summary>
        public async Task<int> WaitForReceivedAsync(int expected)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (Session.Statistics.Received >= expected)
                {
                    break;
                }

                await Task.Delay(20);
            }

            return Session.Statistics.Received;
        }

        public void Dispose()
        {
            Session.Dispose();
            Bus.Dispose();
            _reservation.Dispose();
            _peer.Dispose();
        }
    }
}
