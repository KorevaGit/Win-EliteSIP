using System.Buffers.Binary;

namespace EliteSIP.MediaCore;

/// <summary>Причина, по которой пакет RTP не разобрался.</summary>
public enum RtpParseFailure
{
    /// <summary>Короче фиксированного заголовка.</summary>
    TooShort,
    /// <summary>Версия не 2.</summary>
    UnsupportedVersion,
    /// <summary>Обещаны CSRC, которых в пакете нет.</summary>
    TruncatedCsrc,
    /// <summary>Обещано расширение заголовка, которого в пакете нет.</summary>
    TruncatedExtension,
    /// <summary>Длина дополнения не помещается в пакет.</summary>
    InvalidPadding,
}

/// <summary>
/// Пакет RTP разобрать не удалось.
///
/// В оригинале это перечисление ошибок с присоединёнными значениями. Здесь
/// исключение с полем <see cref="Failure"/> и подробностью: тесты проверяют
/// именно причину, а не текст.
/// </summary>
public sealed class RtpParseException : Exception
{
    public RtpParseException(RtpParseFailure failure, int detail)
        : base($"Пакет RTP не разобран: {failure} ({detail})")
    {
        Failure = failure;
        Detail = detail;
    }

    public RtpParseException()
        : this(RtpParseFailure.TooShort, 0)
    {
    }

    public RtpParseException(string message)
        : base(message)
    {
        Failure = RtpParseFailure.TooShort;
    }

    public RtpParseException(string message, Exception innerException)
        : base(message, innerException)
    {
        Failure = RtpParseFailure.TooShort;
    }

    public RtpParseFailure Failure { get; }

    /// <summary>
    /// Число, уточняющее причину: длина пакета, версия или байт дополнения.
    /// Нужно в журнале — по одной причине без числа непонятно, что пришло.
    /// </summary>
    public int Detail { get; }
}

/// <summary>
/// Пакет RTP по RFC 3550.
///
/// Заголовок фиксированной части — 12 байт:
///
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |V=2|P|X|  CC   |M|     PT      |       sequence number         |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                           timestamp                           |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |            synchronization source (SSRC) identifier           |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
///
/// Класс, а не структура: полезная нагрузка и список CSRC — ссылки, и значимый
/// тип разделил бы их с копией. Сравнение при этом по значению, как в
/// оригинале, — на нём стоит проверка round-trip.
/// </summary>
public sealed class RtpPacket : IEquatable<RtpPacket>
{
    public const byte Version = 2;
    public const int HeaderByteCount = 12;

    public RtpPacket(
        byte payloadType,
        ushort sequenceNumber,
        uint timestamp,
        uint ssrc,
        ReadOnlyMemory<byte> payload,
        bool marker = false,
        IReadOnlyList<uint>? csrcs = null)
    {
        PayloadType = payloadType;
        SequenceNumber = sequenceNumber;
        Timestamp = timestamp;
        Ssrc = ssrc;
        Payload = payload;
        Marker = marker;
        Csrcs = csrcs ?? [];
    }

    public byte PayloadType { get; }

    public ushort SequenceNumber { get; }

    public uint Timestamp { get; }

    public uint Ssrc { get; }

    /// <summary>
    /// Маркер. Для аудио означает первый пакет после тишины, для DTMF — начало
    /// события.
    /// </summary>
    public bool Marker { get; }

    public IReadOnlyList<uint> Csrcs { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Разбор пришедшего из сокета пакета.</summary>
    public static RtpPacket Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderByteCount)
        {
            throw new RtpParseException(RtpParseFailure.TooShort, data.Length);
        }

        int version = (data[0] & 0b1100_0000) >> 6;
        if (version != Version)
        {
            throw new RtpParseException(RtpParseFailure.UnsupportedVersion, version);
        }

        bool hasPadding = (data[0] & 0b0010_0000) != 0;
        bool hasExtension = (data[0] & 0b0001_0000) != 0;
        int csrcCount = data[0] & 0b0000_1111;

        bool marker = (data[1] & 0b1000_0000) != 0;
        byte payloadType = (byte)(data[1] & 0b0111_1111);
        ushort sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);

        int offset = HeaderByteCount;

        if (data.Length < offset + (csrcCount * 4))
        {
            throw new RtpParseException(RtpParseFailure.TruncatedCsrc, csrcCount);
        }

        uint[] csrcs = new uint[csrcCount];
        for (int index = 0; index < csrcCount; index++)
        {
            csrcs[index] = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            offset += 4;
        }

        if (hasExtension)
        {
            // Расширение мы не используем, но обязаны его перешагнуть: иначе
            // первые байты полезной нагрузки окажутся мусором в звуке.
            if (data.Length < offset + 4)
            {
                throw new RtpParseException(RtpParseFailure.TruncatedExtension, data.Length - offset);
            }

            int words = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            offset += 4 + (words * 4);
            if (data.Length < offset)
            {
                throw new RtpParseException(RtpParseFailure.TruncatedExtension, words);
            }
        }

        int end = data.Length;
        if (hasPadding)
        {
            // Последний байт — количество байт дополнения, включая сам себя.
            byte padding = data[^1];
            if (padding == 0 || offset + padding > end)
            {
                throw new RtpParseException(RtpParseFailure.InvalidPadding, padding);
            }

            end -= padding;
        }

        return new RtpPacket(
            payloadType,
            sequenceNumber,
            timestamp,
            ssrc,
            data[offset..end].ToArray(),
            marker,
            csrcs);
    }

    /// <summary>Сборка пакета для отправки.</summary>
    public byte[] Encoded()
    {
        byte[] bytes = new byte[HeaderByteCount + (Csrcs.Count * 4) + Payload.Length];
        Span<byte> span = bytes;

        // Дополнение не используем: пакеты у нас всегда кратны нужному размеру,
        // а флаг P потребовал бы согласованной длины у принимающей стороны.
        span[0] = (byte)((Version << 6) | (Csrcs.Count & 0x0F));
        span[1] = (byte)((Marker ? 0b1000_0000 : 0) | (PayloadType & 0b0111_1111));
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], SequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], Ssrc);

        int offset = HeaderByteCount;
        foreach (uint csrc in Csrcs)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[offset..], csrc);
            offset += 4;
        }

        Payload.Span.CopyTo(span[offset..]);
        return bytes;
    }

    public bool Equals(RtpPacket? other) =>
        other is not null
        && PayloadType == other.PayloadType
        && SequenceNumber == other.SequenceNumber
        && Timestamp == other.Timestamp
        && Ssrc == other.Ssrc
        && Marker == other.Marker
        && Csrcs.SequenceEqual(other.Csrcs)
        && Payload.Span.SequenceEqual(other.Payload.Span);

    public override bool Equals(object? obj) => Equals(obj as RtpPacket);

    public override int GetHashCode() =>
        HashCode.Combine(PayloadType, SequenceNumber, Timestamp, Ssrc, Marker, Payload.Length);
}

/// <summary>
/// Полезная нагрузка события telephone-event по RFC 4733 — четыре байта.
///
/// <code>
///  0                   1                   2                   3
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |     event     |E|R| volume    |          duration             |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
/// </summary>
public readonly record struct TelephoneEventPayload
{
    public const int ByteCount = 4;

    public TelephoneEventPayload(byte @event, ushort duration, bool isEnd = false, byte volume = 10)
    {
        Event = @event;
        IsEnd = isEnd;
        Volume = Math.Min(volume, (byte)63);
        Duration = duration;
    }

    /// <summary>Код события: 0–9 это цифры, 10 — «*», 11 — «#», 12–15 — A–D.</summary>
    public byte Event { get; }

    /// <summary>Конец события. Последние три пакета события обязаны иметь этот флаг.</summary>
    public bool IsEnd { get; }

    /// <summary>Громкость в -dBm0, от 0 до 63. Меньше — громче.</summary>
    public byte Volume { get; }

    /// <summary>Длительность в тактах часов кодека (8000 Гц), нарастающая.</summary>
    public ushort Duration { get; }

    public static TelephoneEventPayload? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < ByteCount)
        {
            return null;
        }

        return new TelephoneEventPayload(
            data[0],
            BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
            (data[1] & 0b1000_0000) != 0,
            (byte)(data[1] & 0b0011_1111));
    }

    public byte[] Encoded()
    {
        byte[] bytes = new byte[ByteCount];
        bytes[0] = Event;
        bytes[1] = (byte)((IsEnd ? 0b1000_0000 : 0) | (Volume & 0b0011_1111));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), Duration);
        return bytes;
    }

    /// <summary>Код события для символа на клавиатуре.</summary>
    public static byte? EventCode(char character) => character switch
    {
        >= '0' and <= '9' => (byte)(character - '0'),
        '*' => 10,
        '#' => 11,
        'A' or 'a' => 12,
        'B' or 'b' => 13,
        'C' or 'c' => 14,
        'D' or 'd' => 15,
        _ => null,
    };

    public static char? Character(byte eventCode) => eventCode switch
    {
        <= 9 => (char)('0' + eventCode),
        10 => '*',
        11 => '#',
        12 => 'A',
        13 => 'B',
        14 => 'C',
        15 => 'D',
        _ => null,
    };
}
