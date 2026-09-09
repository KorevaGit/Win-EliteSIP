using System.Net;
using System.Net.Sockets;

namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Медиа-сессия и резервирование портов.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/RTPSessionTests.swift</c>.
/// </summary>
public sealed class RtpSessionTests
{
    [Fact]
    public void Занимает_чётный_порт_из_диапазона()
    {
        using RtpPortReservation reservation = RtpPortReservation.Reserve();

        // Чётность не эстетика: по RFC 3550 §11 за RTP-портом идёт RTCP на
        // порт+1, и нечётный порт ломает это соглашение.
        Assert.Equal(0, reservation.RtpPort % 2);
        Assert.InRange(
            reservation.RtpPort,
            RtpPortReservation.DefaultPortRangeLower,
            RtpPortReservation.DefaultPortRangeUpper);
        Assert.Equal(reservation.RtpPort + 1, reservation.RtcpPort);
    }

    [Fact]
    public void Две_подготовленные_линии_удерживают_разные_пары_портов()
    {
        using RtpPortReservation first = RtpPortReservation.Reserve();
        using RtpPortReservation second = RtpPortReservation.Reserve();

        Assert.NotEqual(first.RtpPort, second.RtpPort);
        Assert.NotEqual(first.RtcpPort, second.RtcpPort);
    }

    [Fact]
    public void Диапазон_по_умолчанию_согласован_с_лабораторией()
    {
        // Диапазон должен помещаться в тот, что опубликован в docker-compose и
        // прописан в rtp.conf. Разъезд здесь означает звук в одну сторону.
        Assert.True(RtpPortReservation.DefaultPortRangeLower >= 16384);
        Assert.True(
            RtpPortReservation.DefaultPortRangeUpper - RtpPortReservation.DefaultPortRangeLower >= 50,
            "слишком узко для нескольких линий");
    }

    [Fact]
    public void Пустой_диапазон_даёт_понятную_ошибку_а_не_молчание()
    {
        // Диапазон из одного нечётного порта: пара в него не помещается.
        Assert.Throws<NoFreeRtpPortException>(() => RtpPortReservation.Reserve(1, 1));
    }

    [Fact]
    public void Настройки_строятся_из_результата_согласования_SDP()
    {
        NegotiatedMedia negotiated = new(
            AudioCodec.Pcma,
            8,
            "172.17.0.2",
            14028,
            TelephoneEventPayloadType: 101,
            PacketTimeMilliseconds: 20);

        RtpSessionConfiguration configuration = RtpSessionConfiguration.FromNegotiated(negotiated);

        Assert.Equal(AudioCodec.Pcma, configuration.Codec);
        Assert.Equal(8, configuration.PayloadType);
        Assert.Equal<byte?>(101, configuration.TelephoneEventPayloadType);

        // 20 мс при 8 кГц — это 160 отсчётов, и на столько же растёт метка
        // времени в каждом пакете.
        Assert.Equal(160u, configuration.TimestampIncrement);

        // У G.722 отсчётов вдвое больше, а метка растёт на те же 160: частота
        // часов RTP у него объявлена 8000 при выборке 16 000 (RFC 3551 §4.5.2).
        RtpSessionConfiguration wideband = new(AudioCodec.G722, 9);
        Assert.Equal(160u, wideband.TimestampIncrement);
        Assert.Equal(320, AudioCodec.G722.SampleCount(20));
    }

    [Fact]
    public void Счёт_отправителя_включает_пакеты_DTMF_а_не_только_звук()
    {
        // Отчёт RTCP объявляет, сколько всего пакетов данных мы отправили
        // (RFC 3550 §6.4.1). События telephone-event идут тем же потоком и тем
        // же SSRC, поэтому пропуск их в счёте — занижённый отчёт ровно на
        // набранные цифры, и заметно это только у собеседника.
        using RtpPortReservation reservation = RtpPortReservation.Reserve();
        reservation.Activate();

        using RtpSession session = new(
            new RtpSessionConfiguration(TelephoneEventPayloadType: 101),
            reservation.RtpPort,
            "127.0.0.1",
            40106);

        byte[] silence = new byte[160];
        Array.Fill(silence, G711.MuLawSilence);
        session.Send(silence);

        (uint Packets, uint Octets, uint Timestamp, uint Ssrc) before = session.SendStatistics;
        Assert.Equal(1u, before.Packets);
        Assert.Equal(160u, before.Octets);

        TelephoneEventPayload payload = new(4, duration: 160, isEnd: false, volume: 10);
        session.SendEvent(payload, isFirst: true);

        (uint Packets, uint Octets, uint Timestamp, uint Ssrc) after = session.SendStatistics;
        Assert.Equal(2u, after.Packets);
        Assert.Equal(160u + (uint)payload.Encoded().Length, after.Octets);

        // А вот метка времени внутри события расти не должна — это отдельное
        // правило RFC 4733 §2.5.1, и счётчики его не отменяют.
        Assert.Equal(before.Timestamp, after.Timestamp);
    }

    /// <summary>
    /// Согласованный SDES поднимает защищённый поток, а не открытый.
    /// </summary>
    ///
    /// <remarks>
    /// До W11 на этом месте стояла обратная проверка: поток с согласованным SRTP
    /// обязан был не подниматься вовсе, потому что шифрования ещё не было, а
    /// отправить открытый RTP там, где договорились о защите, — это downgrade,
    /// который без снятия трафика не заметен. Теперь шифрование есть, и
    /// проверяется то же самое с другой стороны: по проводу идёт не тот RTP,
    /// который отправляли, а на приёме он снова становится собой.
    /// </remarks>
    [Fact]
    public void Согласованный_SDES_поднимает_защищённый_поток()
    {
        using RtpPortReservation sender = RtpPortReservation.Reserve();
        using RtpPortReservation receiver = RtpPortReservation.Reserve();
        sender.Activate();
        receiver.Activate();

        SrtpMasterKey ours = SrtpMasterKey.Random();
        SrtpMasterKey theirs = SrtpMasterKey.Random();

        // Ключи у направлений перекрёстные: наш исходящий — их входящий.
        RtpSessionConfiguration talkingSide = new(Security: MediaSecurity.Sdes(ours, theirs));
        RtpSessionConfiguration listeningSide = new(Security: MediaSecurity.Sdes(theirs, ours));

        using ManualResetEventSlim arrived = new();
        RtpPacket? received = null;

        using RtpSession listening = new(listeningSide, receiver.RtpPort, "127.0.0.1", sender.RtpPort);
        listening.OnReceivedPacket = packet =>
        {
            received = packet;
            arrived.Set();
        };
        listening.Start();

        using RtpSession talking = new(talkingSide, sender.RtpPort, "127.0.0.1", receiver.RtpPort);
        talking.Start();

        Assert.True(talking.IsSecured);

        byte[] tone = new byte[160];
        Array.Fill(tone, (byte)0x2A);
        talking.Send(tone);

        Assert.True(arrived.Wait(TimeSpan.FromSeconds(2)), "защищённый пакет не доехал по петле");

        RtpPacket packet = Assert.IsType<RtpPacket>(received);
        Assert.Equal(tone, packet.Payload.ToArray());
        Assert.Equal(talking.SynchronizationSource, packet.Ssrc);
    }

    /// <summary>
    /// Чужой ключ на входящем направлении не даёт ни звука, ни разбора.
    /// </summary>
    ///
    /// <remarks>
    /// Проверка сквозная нарочно: отдельно контекст уже проверен, а здесь важно,
    /// что отказ подлинности гасится в цикле приёма и не роняет разговор — на
    /// открытый порт прилетает что угодно, и рвать звонок из-за этого нельзя.
    /// </remarks>
    [Fact]
    public void Пакет_с_чужим_ключом_до_тракта_не_доходит()
    {
        using RtpPortReservation sender = RtpPortReservation.Reserve();
        using RtpPortReservation receiver = RtpPortReservation.Reserve();
        sender.Activate();
        receiver.Activate();

        SrtpMasterKey ours = SrtpMasterKey.Random();
        SrtpMasterKey stranger = SrtpMasterKey.Random();

        using ManualResetEventSlim arrived = new();

        using RtpSession listening = new(
            new RtpSessionConfiguration(Security: MediaSecurity.Sdes(SrtpMasterKey.Random(), stranger)),
            receiver.RtpPort,
            "127.0.0.1",
            sender.RtpPort);
        listening.OnReceivedPacket = _ => arrived.Set();
        listening.Start();

        using RtpSession talking = new(
            new RtpSessionConfiguration(Security: MediaSecurity.Sdes(ours, SrtpMasterKey.Random())),
            sender.RtpPort,
            "127.0.0.1",
            receiver.RtpPort);
        talking.Start();

        talking.Send(new byte[160]);

        Assert.False(arrived.Wait(TimeSpan.FromMilliseconds(400)), "пакет с чужим ключом дошёл до тракта");
    }

    [Fact]
    public void Пакет_уходит_в_сеть_и_приходит_на_том_конце()
    {
        // Проверка сквозная и намеренно короткая: она отвечает на единственный
        // вопрос, который не закрывают модульные тесты, — что сокет привязан,
        // отправка идёт с того же порта, и приём вообще работает.
        using RtpPortReservation sender = RtpPortReservation.Reserve();
        using RtpPortReservation receiver = RtpPortReservation.Reserve();
        sender.Activate();
        receiver.Activate();

        using ManualResetEventSlim arrived = new();
        RtpPacket? received = null;

        using RtpSession listening = new(
            new RtpSessionConfiguration(),
            receiver.RtpPort,
            "127.0.0.1",
            sender.RtpPort);
        listening.OnReceivedPacket = packet =>
        {
            received = packet;
            arrived.Set();
        };
        listening.Start();

        using RtpSession talking = new(
            new RtpSessionConfiguration(),
            sender.RtpPort,
            "127.0.0.1",
            receiver.RtpPort);
        talking.Start();

        byte[] silence = new byte[160];
        Array.Fill(silence, G711.MuLawSilence);
        talking.Send(silence);

        Assert.True(arrived.Wait(TimeSpan.FromSeconds(2)), "пакет не доехал по петле");
        RtpPacket packet = Assert.IsType<RtpPacket>(received);
        Assert.Equal(160, packet.Payload.Length);
        Assert.Equal(talking.SynchronizationSource, packet.Ssrc);

        // Первый пакет разговора помечается маркером: по нему принимающая
        // сторона понимает начало речи.
        Assert.True(packet.Marker);
    }

    [Fact]
    public void Взятый_у_резервации_сокет_не_освобождает_порт_ни_на_миг()
    {
        // Раньше между «отпустить проверочный сокет» и «привязать рабочий» был
        // зазор, в котором порт свободен для всей машины. Внутрипроцессный учёт
        // его не закрывает: соседний процесс про наш список занятых портов не
        // знает и займёт порт совершенно законно — а отказ вылезет в
        // конструкторе потока, посреди уже принятого звонка.
        using RtpPortReservation reservation = RtpPortReservation.Reserve();
        ushort port = reservation.RtpPort;

        using RtpSession session = new(
            new RtpSessionConfiguration(),
            port,
            "127.0.0.1",
            40110,
            reservation.TakeRtpSocket());

        // Порт всё это время наш: попытка занять его со стороны обязана
        // провалиться, а не «успеть в зазор».
        using Socket intruder = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
        };

        Assert.Throws<SocketException>(() => intruder.Bind(new IPEndPoint(IPAddress.Any, port)));
    }

    [Fact]
    public void Отданный_дважды_сокет_даёт_понятный_отказ()
    {
        using RtpPortReservation reservation = RtpPortReservation.Reserve();
        _ = reservation.TakeRtpSocket();

        // Второй желающий на тот же сокет — это ошибка вызывающего, и молчать о
        // ней нельзя: разговор пошёл бы по сокету, который уже закрывает
        // кто-то другой.
        Assert.Throws<InvalidOperationException>(reservation.TakeRtpSocket);
    }

    [Fact]
    public void Смена_плеча_собеседника_не_рвёт_нумерацию_потока()
    {
        // Так выглядит пересогласование: сервер вернулся с другого адреса. Для
        // собеседника это должен быть тот же поток — иначе его джиттер-буфер
        // разберёт смену плеча как обрыв связи.
        using RtpPortReservation reservation = RtpPortReservation.Reserve();
        using Socket first = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using Socket second = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        second.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        using RtpSession session = new(
            new RtpSessionConfiguration(),
            reservation.RtpPort,
            "127.0.0.1",
            (ushort)((IPEndPoint)first.LocalEndPoint!).Port,
            reservation.TakeRtpSocket());

        byte[] silence = new byte[160];
        Array.Fill(silence, G711.MuLawSilence);
        session.Send(silence);

        RtpPacket before = Receive(first);

        session.Retarget("127.0.0.1", (ushort)((IPEndPoint)second.LocalEndPoint!).Port);
        session.Send(silence);

        RtpPacket after = Receive(second);

        Assert.Equal(before.Ssrc, after.Ssrc);
        Assert.Equal((ushort)(before.SequenceNumber + 1), after.SequenceNumber);
        Assert.Equal(before.Timestamp + 160u, after.Timestamp);

        // Маркер: для собеседника это начало речи с нового плеча.
        Assert.True(after.Marker);
    }

    private static RtpPacket Receive(Socket socket)
    {
        byte[] buffer = new byte[2048];
        socket.ReceiveTimeout = 2000;
        int read = socket.Receive(buffer);
        return RtpPacket.Parse(buffer.AsSpan(0, read));
    }

    [Fact]
    public void Пропущенный_кадр_двигает_метку_времени_и_ставит_маркер()
    {
        // Так выглядит немой микрофон. Просто не отправлять кадры нельзя: метка
        // времени растёт только на отправке, и пакет с меткой минутной давности
        // собеседник разберёт не как паузу, а как приход безнадёжно старого
        // звука.
        using RtpPortReservation reservation = RtpPortReservation.Reserve();
        reservation.Activate();

        using RtpSession session = new(
            new RtpSessionConfiguration(),
            reservation.RtpPort,
            "127.0.0.1",
            40108);

        byte[] silence = new byte[160];
        Array.Fill(silence, G711.MuLawSilence);
        session.Send(silence);

        (uint Packets, uint Octets, uint Timestamp, uint Ssrc) before = session.SendStatistics;

        session.SkipFrame();
        session.SkipFrame();

        (uint Packets, uint Octets, uint Timestamp, uint Ssrc) after = session.SendStatistics;

        // Время идёт на два такта, а пакетов не прибавилось: пропущенный кадр —
        // это молчание, а не потерянный пакет, и в счёт отправителя он не
        // входит.
        Assert.Equal(before.Timestamp + 320u, after.Timestamp);
        Assert.Equal(before.Packets, after.Packets);
        Assert.Equal(before.Octets, after.Octets);
    }
}
