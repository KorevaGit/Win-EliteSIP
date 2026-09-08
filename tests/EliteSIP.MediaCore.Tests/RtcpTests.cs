using System.Buffers.Binary;

namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Пакеты RTCP.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/RTCPTests.swift</c>.
/// </summary>
public sealed class RtcpTests
{
    private static RtcpReportBlock Block(
        int lost = 0,
        double fraction = 0,
        uint jitter = 0,
        uint highest = 1000) =>
        new(0xDEAD_BEEF, fraction, lost, highest, jitter, 0x1234_5678, 65536);

    [Fact]
    public void Отчёт_отправителя_переживает_сборку_и_разбор()
    {
        RtcpSenderReport original = new(
            0x1111_2222,
            0xAABB_CCDD_EEFF_0011,
            160_000,
            1000,
            160_000,
            [Block(lost: 7, fraction: 0.25, jitter: 480)]);

        RtcpSenderReport decoded = Assert.IsType<RtcpSenderReport>(Rtcp.Parse(Rtcp.Encode(original))[0]);

        Assert.Equal(original.Ssrc, decoded.Ssrc);
        Assert.Equal(original.NtpTimestamp, decoded.NtpTimestamp);
        Assert.Equal(original.RtpTimestamp, decoded.RtpTimestamp);
        Assert.Equal(original.PacketCount, decoded.PacketCount);
        Assert.Equal(original.OctetCount, decoded.OctetCount);
        Assert.Single(decoded.Reports);
        Assert.Equal(7, decoded.Reports[0].CumulativeLost);
        Assert.Equal(480u, decoded.Reports[0].Jitter);

        // Доля потерь — восьмибитная дробь, точность у неё 1/256.
        Assert.True(Math.Abs(decoded.Reports[0].FractionLost - 0.25) < 0.005);
    }

    [Fact]
    public void Отчёт_приёмника_переживает_сборку_и_разбор()
    {
        RtcpReceiverReport original = new(0x3333_4444, [Block(), Block()]);
        RtcpReceiverReport decoded = Assert.IsType<RtcpReceiverReport>(Rtcp.Parse(Rtcp.Encode(original))[0]);

        Assert.Equal(0x3333_4444u, decoded.Ssrc);
        Assert.Equal(2, decoded.Reports.Count);
    }

    [Fact]
    public void Отрицательные_потери_не_превращаются_в_шестнадцать_миллионов()
    {
        // По RFC 3550 §6.4.1 накопленные потери — 24-битное число СО ЗНАКОМ:
        // дубликаты уводят его в минус, и это законно. Наивный разбор без
        // растяжения знака даёт около 16,7 миллиона потерянных пакетов, и отчёт
        // выглядит как полная потеря связи на исправном канале.
        RtcpReceiverReport original = new(1, [Block(lost: -5)]);
        RtcpReceiverReport decoded = Assert.IsType<RtcpReceiverReport>(Rtcp.Parse(Rtcp.Encode(original))[0]);

        Assert.Equal(-5, decoded.Reports[0].CumulativeLost);
    }

    [Fact]
    public void Составной_пакет_разбирается_целиком_а_не_по_первому()
    {
        // RTCP почти никогда не приходит одним пакетом: по RFC 3550 §6.1 отчёт
        // обязан идти в связке с описанием источника. Разбирать только первый
        // значит терять всё, что приехало следом.
        byte[] compound = Rtcp.Compound(
            Rtcp.Encode(new RtcpReceiverReport(42, [Block()])),
            42,
            "elitesip@16384");

        IReadOnlyList<RtcpPacket> packets = Rtcp.Parse(compound);
        Assert.Equal(2, packets.Count);
        Assert.Equal(42u, Assert.IsType<RtcpReceiverReport>(packets[0]).Ssrc);

        RtcpSourceDescription description = Assert.IsType<RtcpSourceDescription>(packets[1]);
        Assert.Equal(42u, description.Ssrc);
        Assert.Equal("elitesip@16384", description.CanonicalName);
    }

    [Fact]
    public void Длина_в_заголовке_считается_в_словах_без_первого()
    {
        // Единица измерения здесь — 32-битные слова МИНУС одно. Ошибка на
        // единицу ломает разбор всего составного пакета, а не одного поля.
        byte[] data = Rtcp.Encode(new RtcpReceiverReport(1));
        Assert.Equal(8, data.Length);

        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)));
        Assert.Equal(2, data[0] >> 6);
        Assert.Equal(0, data[0] & 0x1F);
        Assert.Equal(201, data[1]);
    }

    [Fact]
    public void Мусор_не_разбирается_и_не_роняет_разбор()
    {
        // На открытый UDP-порт прилетает что угодно, включая сканеры.
        Assert.Equal(
            RtcpParseFailure.UnsupportedVersion,
            Assert.Throws<RtcpParseException>(
                () => Rtcp.Parse(new byte[] { 0x00, 0xC9, 0x00, 0x01, 0, 0, 0, 1 })).Failure);

        // Заголовок обещает больше, чем приехало.
        Assert.Equal(
            RtcpParseFailure.TooShort,
            Assert.Throws<RtcpParseException>(
                () => Rtcp.Parse(new byte[] { 0x80, 0xC9, 0x00, 0xFF, 0, 0, 0, 1 })).Failure);

        // Обрывок короче заголовка просто заканчивает разбор.
        Assert.Empty(Rtcp.Parse(new byte[] { 0x80, 0xC9 }));
    }

    [Fact]
    public void Прощание_опознаётся()
    {
        RtcpGoodbye goodbye = Assert.IsType<RtcpGoodbye>(Rtcp.Parse(Rtcp.EncodeGoodbye(0xCAFE_BABE))[0]);
        Assert.Equal(0xCAFE_BABEu, goodbye.Ssrc);
    }

    [Fact]
    public void Метка_NTP_считается_от_1900_года()
    {
        // Эпоха NTP на 70 лет раньше Unix. Ошибка здесь не ломает разговор, но
        // делает бессмысленной задержку кругового обхода.
        Assert.Equal(2_208_988_800ul, Rtcp.NtpTimestamp(DateTimeOffset.FromUnixTimeSeconds(0)) >> 32);
        Assert.Equal(2_208_988_801ul, Rtcp.NtpTimestamp(DateTimeOffset.FromUnixTimeSeconds(1)) >> 32);
    }

    [Fact]
    public void Задержка_кругового_обхода_считается_по_средним_битам_метки()
    {
        // Формат 16.16 секунды: 65536 — это ровно секунда.
        RtcpReportBlock block = new(1, 0, 0, 0, 0, 1000 * 65536, 65536 / 2);

        // Сейчас 1002 секунды, отчёт был на 1000-й, у собеседника пролежал
        // полсекунды — значит на дорогу ушло полторы.
        TimeSpan roundTrip = Assert.NotNull(block.RoundTripTime(1002 * 65536));
        Assert.True(Math.Abs(roundTrip.TotalSeconds - 1.5) < 0.01);

        // Пока собеседник ни одного нашего отчёта не видел, считать нечего.
        Assert.Null((block with { LastSenderReport = 0 }).RoundTripTime(1002 * 65536));
    }
}

/// <summary>
/// Сессия RTCP: сборка отчёта и разбор входящего.
///
/// В оригинале эта часть проверялась только через живой сокет и пятисекундный
/// таймер, то есть на практике не проверялась. Здесь сборка отчёта и разбор
/// вынесены в отдельные методы и проверяются напрямую — содержимое отчёта это
/// то, по чему собеседник судит о нашем потоке.
/// </summary>
public sealed class RtcpSessionTests
{
    private const uint Ssrc = 0x0BAD_F00D;

    /// <summary>Постоянное имя источника: по нему собеседник узнаёт нас после пересборки потока.</summary>
    private const string CanonicalName = "elitesip@test";

    private static RtcpSession MakeSession(ushort localPort) =>
        new(Ssrc, CanonicalName, 8000, localPort, "127.0.0.1", 40012);

    private static ushort FreePort()
    {
        RtpPortReservation reservation = RtpPortReservation.Reserve();
        ushort port = reservation.RtcpPort;
        reservation.Release();
        return port;
    }

    [Fact]
    public void Пока_мы_ничего_не_отправили_идёт_отчёт_приёмника()
    {
        using RtcpSession session = MakeSession(FreePort());
        session.StatisticsProvider = () => new LocalMediaStatistics { RemoteSsrc = 0x1234 };

        byte[] report = Assert.IsType<byte[]>(session.BuildReport(DateTimeOffset.UtcNow));
        IReadOnlyList<RtcpPacket> packets = Rtcp.Parse(report);

        RtcpReceiverReport receiver = Assert.IsType<RtcpReceiverReport>(packets[0]);
        Assert.Equal(Ssrc, receiver.Ssrc);
        Assert.Equal(0x1234u, Assert.Single(receiver.Reports).SourceSsrc);

        // Отчёт без CNAME Asterisk выбрасывает, и статистика у него остаётся
        // пустой при внешне исправном обмене.
        //
        // Проверяется именно содержимое, а не тип пакета: раньше описание
        // источника не разбиралось вовсе и приезжало сюда «пакетом типа 202»,
        // так что тест подтверждал только его наличие в потоке.
        RtcpSourceDescription description = Assert.IsType<RtcpSourceDescription>(packets[1]);
        Assert.Equal(Ssrc, description.Ssrc);
        Assert.Equal(CanonicalName, description.CanonicalName);
    }

    [Fact]
    public void Как_только_мы_заговорили_идёт_отчёт_отправителя()
    {
        using RtcpSession session = MakeSession(FreePort());
        session.StatisticsProvider = () => new LocalMediaStatistics
        {
            PacketsSent = 250,
            OctetsSent = 40_000,
            RtpTimestamp = 160_000,
            RemoteSsrc = 0x1234,
            Jitter = 480,
        };

        RtcpSenderReport sender = Assert.IsType<RtcpSenderReport>(
            Rtcp.Parse(Assert.IsType<byte[]>(session.BuildReport(DateTimeOffset.UtcNow)))[0]);

        Assert.Equal(250u, sender.PacketCount);
        Assert.Equal(40_000u, sender.OctetCount);
        Assert.Equal(160_000u, sender.RtpTimestamp);
        Assert.Equal(480u, Assert.Single(sender.Reports).Jitter);
    }

    [Fact]
    public void Без_известного_SSRC_собеседника_блок_отчёта_не_выдумывается()
    {
        using RtcpSession session = MakeSession(FreePort());
        session.StatisticsProvider = () => new LocalMediaStatistics { PacketsSent = 1 };

        RtcpSenderReport sender = Assert.IsType<RtcpSenderReport>(
            Rtcp.Parse(Assert.IsType<byte[]>(session.BuildReport(DateTimeOffset.UtcNow)))[0]);

        Assert.Empty(sender.Reports);
    }

    [Fact]
    public void Метка_чужого_отчёта_возвращается_собеседнику_вместе_с_задержкой()
    {
        // По ней собеседник и считает время кругового обхода: без метки и
        // задержки его сторона не узнает круг вообще.
        using RtcpSession session = MakeSession(FreePort());
        session.StatisticsProvider = () => new LocalMediaStatistics { RemoteSsrc = 0x1234 };

        DateTimeOffset received = DateTimeOffset.UtcNow;
        ulong remoteNtp = Rtcp.NtpTimestamp(received);
        session.Handle([new RtcpSenderReport(0x1234, remoteNtp, 0, 10, 1600)], received);

        RtcpReceiverReport report = Assert.IsType<RtcpReceiverReport>(
            Rtcp.Parse(Assert.IsType<byte[]>(session.BuildReport(received.AddSeconds(2))))[0]);
        RtcpReportBlock block = Assert.Single(report.Reports);

        Assert.Equal(Rtcp.MiddleBits(remoteNtp), block.LastSenderReport);

        // Две секунды в формате 16.16 — это 2 × 65536.
        Assert.True(Math.Abs((int)block.DelaySinceLastSenderReport - (2 * 65536)) < 6553);
    }

    [Fact]
    public void Отчёт_про_чужой_источник_не_выдаётся_за_наш_поток()
    {
        // В конференции блоков приезжает несколько, а интересен только наш
        // собственный поток.
        using RtcpSession session = MakeSession(FreePort());
        List<RemoteMediaView> views = [];
        session.OnRemoteView = views.Add;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        session.Handle(
            [
                new RtcpReceiverReport(0x1234,
                [
                    new RtcpReportBlock(0x9999_9999, 0.5, 100, 0, 8000, 0, 0),
                    new RtcpReportBlock(Ssrc, 0.25, 7, 0, 480, 0, 0),
                ]),
            ],
            now);

        RemoteMediaView view = Assert.Single(views);
        Assert.Equal(7, view.CumulativeLost);
        Assert.Equal(0.25, view.FractionLost);

        // 480 единиц часов RTP на 8 кГц — это 60 мс.
        Assert.Equal(60, view.JitterMilliseconds, 3);
    }

    [Fact]
    public void Прощание_собеседника_попадает_в_журнал()
    {
        using RtcpSession session = MakeSession(FreePort());
        List<string> diagnostics = [];
        session.OnDiagnostic = diagnostics.Add;

        session.Handle([new RtcpGoodbye(0x1234)], DateTimeOffset.UtcNow);
        Assert.Single(diagnostics);
    }
}
