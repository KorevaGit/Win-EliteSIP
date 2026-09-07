using System.Buffers.Binary;
using System.Text;

namespace EliteSIP.MediaCore;

/// <summary>Типы пакетов RTCP, которые нас касаются.</summary>
public enum RtcpPacketType : byte
{
    SenderReport = 200,
    ReceiverReport = 201,
    SourceDescription = 202,
    Goodbye = 203,
}

/// <summary>
/// Блок отчёта об одном источнике — сердце RTCP.
/// </summary>
/// <param name="FractionLost">Доля потерь с прошлого отчёта, 0…1.</param>
/// <param name="CumulativeLost">
/// Накопленное число потерянных пакетов. Знаковое: дубликаты могут увести его в
/// минус, и это по стандарту, а не ошибка.
/// </param>
/// <param name="Jitter">Джиттер в единицах часов RTP.</param>
/// <param name="LastSenderReport">Средняя часть метки NTP из последнего отчёта отправителя.</param>
/// <param name="DelaySinceLastSenderReport">
/// Задержка с момента получения того отчёта, в 1/65536 секунды.
/// </param>
public sealed record RtcpReportBlock(
    uint SourceSsrc,
    double FractionLost,
    int CumulativeLost,
    uint HighestSequenceNumber,
    uint Jitter,
    uint LastSenderReport,
    uint DelaySinceLastSenderReport)
{
    /// <summary>
    /// Задержка кругового обхода, если её можно посчитать.
    ///
    /// Считается по RFC 3550 §6.4.1: из текущего времени вычитается метка
    /// нашего отчёта, которую собеседник вернул, и время, которое отчёт у него
    /// пролежал. Возвращает <c>null</c>, пока собеседник ещё ни одного нашего
    /// отчёта не видел.
    /// </summary>
    public TimeSpan? RoundTripTime(uint now)
    {
        if (LastSenderReport == 0)
        {
            return null;
        }

        uint elapsed = unchecked(now - LastSenderReport - DelaySinceLastSenderReport);

        // Значения в формате 16.16 секунды.
        return TimeSpan.FromSeconds((double)elapsed / 65536);
    }
}

/// <summary>Отчёт отправителя: что мы (или собеседник) отправили.</summary>
/// <param name="NtpTimestamp">Метка NTP в формате 32.32.</param>
public sealed record RtcpSenderReport(
    uint Ssrc,
    ulong NtpTimestamp,
    uint RtpTimestamp,
    uint PacketCount,
    uint OctetCount,
    IReadOnlyList<RtcpReportBlock>? Reports = null) : RtcpPacket
{
    public IReadOnlyList<RtcpReportBlock> Reports { get; init; } = Reports ?? [];
}

/// <summary>Отчёт приёмника: что мы (или собеседник) приняли.</summary>
public sealed record RtcpReceiverReport(uint Ssrc, IReadOnlyList<RtcpReportBlock>? Reports = null) : RtcpPacket
{
    public IReadOnlyList<RtcpReportBlock> Reports { get; init; } = Reports ?? [];
}

/// <summary>Собеседник закрыл поток.</summary>
public sealed record RtcpGoodbye(uint Ssrc) : RtcpPacket;

/// <summary>
/// Разобрать не смогли или он нам неинтересен. Тип сохраняется: по нему видно,
/// что именно шлёт сервер.
/// </summary>
public sealed record RtcpOther(byte Type) : RtcpPacket;

/// <summary>
/// Разобранный пакет RTCP.
///
/// В оригинале это перечисление со связанными значениями. Здесь запечатанная
/// иерархия записей — ближайший аналог: набор вариантов закрыт, и разбор по
/// <c>switch</c> остаётся полным.
/// </summary>
public abstract record RtcpPacket
{
    private protected RtcpPacket()
    {
    }
}

/// <summary>Почему пакет RTCP не разобрался.</summary>
public enum RtcpParseFailure
{
    TooShort,
    UnsupportedVersion,
}

/// <summary>Пакет RTCP разобрать не удалось.</summary>
public sealed class RtcpParseException : Exception
{
    public RtcpParseException(RtcpParseFailure failure, int detail = 0)
        : base($"Пакет RTCP не разобран: {failure} ({detail})")
    {
        Failure = failure;
        Detail = detail;
    }

    public RtcpParseException()
        : this(RtcpParseFailure.TooShort)
    {
    }

    public RtcpParseException(string message)
        : base(message)
    {
    }

    public RtcpParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RtcpParseFailure Failure { get; }

    public int Detail { get; }
}

/// <summary>
/// Пакеты RTCP по RFC 3550: отчёты о качестве в обе стороны.
///
/// Зачем они нужны здесь. Собственная статистика показывает только то, что мы
/// приняли; что происходит с нашим потоком у собеседника — не видно вообще.
/// RTCP отвечает ровно на этот вопрос: в отчёте приёмника приезжают доля
/// потерь, джиттер и задержка кругового обхода, посчитанные той стороной.
/// Практически это разница между «у нас всё хорошо, а клиент жалуется» и
/// «видим, что до клиента не доходит четверть пакетов».
///
/// Разбор и сборка — чистые функции над байтами: сеть тут ни при чём, и всё
/// проверяется тестами.
/// </summary>
public static class Rtcp
{
    /// <summary>
    /// Собирает составной пакет: отчёт плюс описание источника.
    ///
    /// Отдельный SDES обязателен по RFC 3550 §6.1 — приёмник вправе выбросить
    /// отчёт без CNAME. Asterisk именно так и делает, и без SDES статистика на
    /// его стороне остаётся пустой при внешне исправном обмене.
    /// </summary>
    public static byte[] Compound(ReadOnlySpan<byte> report, uint ssrc, string canonicalName) =>
        [.. report, .. SourceDescription(ssrc, canonicalName)];

    public static byte[] Encode(RtcpSenderReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<byte> body = [];
        AppendBigEndian(body, report.Ssrc);
        AppendBigEndian(body, report.NtpTimestamp);
        AppendBigEndian(body, report.RtpTimestamp);
        AppendBigEndian(body, report.PacketCount);
        AppendBigEndian(body, report.OctetCount);
        foreach (RtcpReportBlock block in report.Reports)
        {
            body.AddRange(Encode(block));
        }

        return [.. Header(RtcpPacketType.SenderReport, report.Reports.Count, body.Count), .. body];
    }

    public static byte[] Encode(RtcpReceiverReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<byte> body = [];
        AppendBigEndian(body, report.Ssrc);
        foreach (RtcpReportBlock block in report.Reports)
        {
            body.AddRange(Encode(block));
        }

        return [.. Header(RtcpPacketType.ReceiverReport, report.Reports.Count, body.Count), .. body];
    }

    /// <summary>
    /// Прощание по RFC 3550 §6.6: собеседник сразу освобождает состояние
    /// источника, а не ждёт истечения таймаута.
    /// </summary>
    public static byte[] EncodeGoodbye(uint ssrc)
    {
        List<byte> data = [0x81, (byte)RtcpPacketType.Goodbye];
        AppendBigEndian(data, (ushort)1);
        AppendBigEndian(data, ssrc);
        return [.. data];
    }

    /// <summary>
    /// Разбирает составной пакет.
    ///
    /// Именно составной: RTCP почти никогда не приходит по одному пакету, и
    /// разбирать только первый значит терять отчёт, который приехал вторым.
    /// </summary>
    public static IReadOnlyList<RtcpPacket> Parse(ReadOnlySpan<byte> data)
    {
        List<RtcpPacket> packets = [];
        int offset = 0;

        while (data.Length - offset >= 4)
        {
            byte first = data[offset];
            if (first >> 6 != 2)
            {
                throw new RtcpParseException(RtcpParseFailure.UnsupportedVersion, first >> 6);
            }

            int reportCount = first & 0x1F;
            byte type = data[offset + 1];
            int words = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            int length = (words + 1) * 4;

            if (data.Length - offset < length)
            {
                throw new RtcpParseException(RtcpParseFailure.TooShort, data.Length - offset);
            }

            ReadOnlySpan<byte> body = data[(offset + 4)..(offset + length)];

            switch ((RtcpPacketType)type)
            {
                case RtcpPacketType.SenderReport:
                    packets.Add(ParseSenderReport(body, reportCount));
                    break;

                case RtcpPacketType.ReceiverReport:
                    packets.Add(ParseReceiverReport(body, reportCount));
                    break;

                case RtcpPacketType.Goodbye:
                    if (body.Length < 4)
                    {
                        throw new RtcpParseException(RtcpParseFailure.TooShort, body.Length);
                    }

                    packets.Add(new RtcpGoodbye(BinaryPrimitives.ReadUInt32BigEndian(body)));
                    break;

                default:
                    packets.Add(new RtcpOther(type));
                    break;
            }

            offset += length;
        }

        return packets;
    }

    /// <summary>Текущее время в формате NTP: секунды с 1900 года, 32.32.</summary>
    public static ulong NtpTimestamp(DateTimeOffset? moment = null)
    {
        // Разница между эпохами Unix и NTP — семьдесят лет с семнадцатью
        // високосными днями.
        const double SecondsFrom1900To1970 = 2_208_988_800.0;
        double seconds = ((moment ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() / 1000.0)
            + SecondsFrom1900To1970;
        ulong whole = (ulong)seconds;
        ulong fraction = (ulong)((seconds - whole) * 4_294_967_296);
        return (whole << 32) | fraction;
    }

    /// <summary>
    /// Средние 32 бита метки NTP — то, что кладётся в поле «последний отчёт
    /// отправителя» и возвращается собеседником для расчёта задержки.
    /// </summary>
    public static uint MiddleBits(ulong ntp) => (uint)((ntp >> 16) & 0xFFFF_FFFF);

    private static byte[] Encode(RtcpReportBlock block)
    {
        List<byte> data = [];
        AppendBigEndian(data, block.SourceSsrc);

        // Доля потерь — восьмибитная дробь, старший байт слова; в остальных
        // трёх байтах накопленные потери со знаком.
        byte fraction = (byte)Math.Clamp(block.FractionLost * 256, 0, 255);
        uint cumulative = unchecked((uint)block.CumulativeLost) & 0x00FF_FFFF;
        AppendBigEndian(data, ((uint)fraction << 24) | cumulative);
        AppendBigEndian(data, block.HighestSequenceNumber);
        AppendBigEndian(data, block.Jitter);
        AppendBigEndian(data, block.LastSenderReport);
        AppendBigEndian(data, block.DelaySinceLastSenderReport);
        return [.. data];
    }

    /// <summary>Минимальный SDES с одним CNAME.</summary>
    private static byte[] SourceDescription(uint ssrc, string canonicalName)
    {
        List<byte> chunk = [];
        AppendBigEndian(chunk, ssrc);

        byte[] name = Encoding.UTF8.GetBytes(canonicalName);
        if (name.Length > 255)
        {
            name = name[..255];
        }

        chunk.Add(1); // CNAME
        chunk.Add((byte)name.Length);
        chunk.AddRange(name);
        chunk.Add(0); // конец списка элементов

        // Кусок дополняется нулями до границы в четыре байта.
        while (chunk.Count % 4 != 0)
        {
            chunk.Add(0);
        }

        return [.. Header(RtcpPacketType.SourceDescription, 1, chunk.Count), .. chunk];
    }

    private static byte[] Header(RtcpPacketType type, int count, int bodyLength)
    {
        List<byte> data =
        [
            // Версия 2, без дополнения, счётчик отчётов в младших пяти битах.
            (byte)(0x80 | Math.Min(count, 31)),
            (byte)type,
        ];

        // Длина в 32-битных словах, не считая первого. Заголовок — 4 байта.
        AppendBigEndian(data, (ushort)(((bodyLength + 4) / 4) - 1));
        return [.. data];
    }

    private static RtcpSenderReport ParseSenderReport(ReadOnlySpan<byte> body, int reportCount)
    {
        if (body.Length < 20)
        {
            throw new RtcpParseException(RtcpParseFailure.TooShort, body.Length);
        }

        return new RtcpSenderReport(
            BinaryPrimitives.ReadUInt32BigEndian(body),
            BinaryPrimitives.ReadUInt64BigEndian(body[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(body[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(body[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(body[20..]),
            ParseBlocks(body[24..], reportCount));
    }

    private static RtcpReceiverReport ParseReceiverReport(ReadOnlySpan<byte> body, int reportCount)
    {
        if (body.Length < 4)
        {
            throw new RtcpParseException(RtcpParseFailure.TooShort, body.Length);
        }

        return new RtcpReceiverReport(
            BinaryPrimitives.ReadUInt32BigEndian(body),
            ParseBlocks(body[4..], reportCount));
    }

    private static List<RtcpReportBlock> ParseBlocks(ReadOnlySpan<byte> body, int count)
    {
        List<RtcpReportBlock> blocks = [];
        int cursor = 0;

        for (int index = 0; index < count; index++)
        {
            if (body.Length - cursor < 24)
            {
                break;
            }

            ReadOnlySpan<byte> block = body[cursor..];
            uint lossWord = BinaryPrimitives.ReadUInt32BigEndian(block[4..]);

            // Накопленные потери — 24-битное число со знаком; знак надо
            // растянуть вручную, иначе потеря одного пакета выглядит как
            // шестнадцать миллионов.
            int cumulative = (int)(lossWord & 0x00FF_FFFF);
            if ((cumulative & 0x0080_0000) != 0)
            {
                cumulative -= 0x0100_0000;
            }

            blocks.Add(new RtcpReportBlock(
                BinaryPrimitives.ReadUInt32BigEndian(block),
                (double)(lossWord >> 24) / 256,
                cumulative,
                BinaryPrimitives.ReadUInt32BigEndian(block[8..]),
                BinaryPrimitives.ReadUInt32BigEndian(block[12..]),
                BinaryPrimitives.ReadUInt32BigEndian(block[16..]),
                BinaryPrimitives.ReadUInt32BigEndian(block[20..])));

            cursor += 24;
        }

        return blocks;
    }

    private static void AppendBigEndian(List<byte> data, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        data.AddRange(buffer);
    }

    private static void AppendBigEndian(List<byte> data, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        data.AddRange(buffer);
    }

    private static void AppendBigEndian(List<byte> data, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        data.AddRange(buffer);
    }
}
