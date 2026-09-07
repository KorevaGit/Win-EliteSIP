using System.Globalization;

namespace EliteSIP.MediaCore;

/// <summary>
/// <c>a=sendrecv</c> и родственники. Нужны не для красоты: удержание вызова —
/// это re-INVITE со сменой направления, а не отдельная команда SIP.
/// </summary>
public enum MediaDirection
{
    SendRecv,
    SendOnly,
    RecvOnly,
    Inactive,
}

public static class MediaDirectionInfo
{
    /// <summary>Порядок важен: он же задаёт порядок поиска атрибута в секции.</summary>
    public static IReadOnlyList<MediaDirection> All { get; } =
        [MediaDirection.SendRecv, MediaDirection.SendOnly, MediaDirection.RecvOnly, MediaDirection.Inactive];

    public static string AttributeName(this MediaDirection direction) => direction switch
    {
        MediaDirection.SendRecv => "sendrecv",
        MediaDirection.SendOnly => "sendonly",
        MediaDirection.RecvOnly => "recvonly",
        MediaDirection.Inactive => "inactive",
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    /// <summary>Что нам делать с направлением собеседника при ответе.</summary>
    public static MediaDirection Reversed(this MediaDirection direction) => direction switch
    {
        MediaDirection.SendRecv => MediaDirection.SendRecv,
        MediaDirection.SendOnly => MediaDirection.RecvOnly,
        MediaDirection.RecvOnly => MediaDirection.SendOnly,
        MediaDirection.Inactive => MediaDirection.Inactive,
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    public static bool ReceivesAudio(this MediaDirection direction) =>
        direction is MediaDirection.SendRecv or MediaDirection.RecvOnly;

    public static bool SendsAudio(this MediaDirection direction) =>
        direction is MediaDirection.SendRecv or MediaDirection.SendOnly;
}

/// <summary><c>a=rtpmap:101 telephone-event/8000</c>.</summary>
public sealed record RtpMap(byte PayloadType, string EncodingName, uint ClockRate, int? Channels = null)
{
    public static RtpMap? Parse(string value)
    {
        string[] parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !byte.TryParse(parts[0], out byte payloadType))
        {
            return null;
        }

        string[] encoding = parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (encoding.Length < 2 || !uint.TryParse(encoding[1], out uint clockRate))
        {
            return null;
        }

        int? channels = encoding.Length > 2 && int.TryParse(encoding[2], out int parsed) ? parsed : null;
        return new RtpMap(payloadType, encoding[0], clockRate, channels);
    }

    public string Value => Channels is int channels
        ? $"{PayloadType} {EncodingName}/{ClockRate}/{channels}"
        : $"{PayloadType} {EncodingName}/{ClockRate}";

    /// <summary>Соответствует ли этот rtpmap нашему кодеку.</summary>
    public bool Matches(AudioCodec codec) =>
        string.Equals(EncodingName, codec.SdpName(), StringComparison.OrdinalIgnoreCase)
        && ClockRate == codec.RtpClockRate();

    public bool IsTelephoneEvent =>
        string.Equals(EncodingName, TelephoneEvent.SdpName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Строка <c>o=</c>.</summary>
public sealed record SdpOrigin(
    ulong SessionId,
    string Address,
    string Username = "-",
    ulong SessionVersion = 1,
    string NetworkType = "IN",
    string AddressType = "IP4");

/// <summary>Строка <c>c=</c>.</summary>
public sealed record SdpConnection(string Address, string NetworkType = "IN", string AddressType = "IP4");

/// <summary>Строка <c>a=</c>: имя и, возможно, значение после двоеточия.</summary>
public sealed record SdpAttributeLine(string Name, string? Value = null);

/// <summary>
/// Секция <c>m=</c> с относящимися к ней строками.
///
/// Изменяемая: разбор дописывает в текущую секцию атрибуты и строку <c>c=</c>
/// по мере чтения, а согласование собирает ответ из предложения собеседника.
/// В оригинале это структура, и мутация шла по индексу в массиве.
/// </summary>
public sealed class MediaDescription
{
    public MediaDescription(
        ushort port,
        string type = "audio",
        string protocolName = "RTP/AVP",
        IEnumerable<byte>? formats = null,
        SdpConnection? connection = null,
        IEnumerable<SdpAttributeLine>? attributes = null)
    {
        Port = port;
        Type = type;
        ProtocolName = protocolName;
        Formats = formats is null ? [] : [.. formats];
        Connection = connection;
        Attributes = attributes is null ? [] : [.. attributes];
    }

    public string Type { get; set; }

    public ushort Port { get; set; }

    public string ProtocolName { get; set; }

    /// <summary>Payload type в порядке предпочтения отправителя. Порядок значим.</summary>
    public List<byte> Formats { get; }

    public SdpConnection? Connection { get; set; }

    public List<SdpAttributeLine> Attributes { get; }

    /// <summary>
    /// Копия секции со своими списками.
    ///
    /// В оригинале это структура, и копия получалась присваиванием. Здесь
    /// класс, поэтому копия делается явно — и делается там, где она
    /// действительно нужна: повторное предложение правит своё описание, а не
    /// то, которое уже уехало собеседнику.
    /// </summary>
    public MediaDescription Clone() => new(Port, Type, ProtocolName, Formats, Connection, Attributes);

    public string? Attribute(string name) => Attributes
        .FirstOrDefault(attribute => string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))
        ?.Value;

    public bool HasAttribute(string name) => Attributes
        .Any(attribute => string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>a=rtpmap:0 PCMU/8000</c> → [0: RtpMap(...)].</summary>
    public IReadOnlyDictionary<byte, RtpMap> RtpMaps
    {
        get
        {
            Dictionary<byte, RtpMap> result = [];
            foreach (SdpAttributeLine attribute in Attributes)
            {
                if (!string.Equals(attribute.Name, "rtpmap", StringComparison.OrdinalIgnoreCase)
                    || attribute.Value is null
                    || RtpMap.Parse(attribute.Value) is not RtpMap map)
                {
                    continue;
                }

                result[map.PayloadType] = map;
            }

            return result;
        }
    }

    /// <summary>
    /// Направление потока. Отсутствие атрибута означает sendrecv (RFC 4566 §6).
    /// </summary>
    public MediaDirection Direction
    {
        get
        {
            foreach (MediaDirection candidate in MediaDirectionInfo.All)
            {
                if (HasAttribute(candidate.AttributeName()))
                {
                    return candidate;
                }
            }

            return MediaDirection.SendRecv;
        }
    }

    public int? PacketTimeMilliseconds =>
        Attribute("ptime") is string value && int.TryParse(value, out int milliseconds) ? milliseconds : null;

    /// <summary>Все корректные предложения SDES в порядке отправителя.</summary>
    public IReadOnlyList<SdesCryptoLine> SdesCryptoLines =>
    [
        .. Attributes
            .Where(attribute => string.Equals(attribute.Name, "crypto", StringComparison.OrdinalIgnoreCase))
            .Select(attribute => attribute.Value is null ? null : SdesCryptoLine.Parse(attribute.Value))
            .OfType<SdesCryptoLine>(),
    ];
}

/// <summary>
/// Описание сессии по RFC 4566 — тело INVITE и 200 OK.
///
/// Модель осознанно неполная: из всех типов строк реализованы те, что реально
/// присылает Asterisk и что нужно нам. Незнакомые строки не теряются, а
/// складываются в <see cref="UnknownLines"/> — так их видно при отладке, и они
/// не мешают разбору остального.
/// </summary>
public sealed class SessionDescription
{
    public const string ContentType = "application/sdp";

    public SessionDescription(
        SdpOrigin origin,
        string sessionName = "EliteSIP",
        SdpConnection? connection = null,
        ulong startTime = 0,
        ulong stopTime = 0,
        IEnumerable<SdpAttributeLine>? attributes = null,
        IEnumerable<MediaDescription>? media = null,
        IEnumerable<string>? unknownLines = null)
    {
        Origin = origin;
        SessionName = sessionName;
        Connection = connection;
        StartTime = startTime;
        StopTime = stopTime;
        Attributes = attributes is null ? [] : [.. attributes];
        Media = media is null ? [] : [.. media];
        UnknownLines = unknownLines is null ? [] : [.. unknownLines];
    }

    public SdpOrigin Origin { get; set; }

    public string SessionName { get; set; }

    public SdpConnection? Connection { get; set; }

    public ulong StartTime { get; set; }

    public ulong StopTime { get; set; }

    public List<SdpAttributeLine> Attributes { get; }

    public List<MediaDescription> Media { get; }

    public List<string> UnknownLines { get; }

    /// <summary>Первая аудио-секция. Видео у нас нет, так что это и есть «медиа».</summary>
    public MediaDescription? Audio => Media.FirstOrDefault(section => section.Type == "audio");

    /// <summary>Копия описания со своими секциями. См. <see cref="MediaDescription.Clone"/>.</summary>
    public SessionDescription Clone() => new(
        Origin,
        SessionName,
        Connection,
        StartTime,
        StopTime,
        Attributes,
        Media.Select(section => section.Clone()),
        UnknownLines);

    /// <summary>
    /// Строки выводятся в порядке, который требует RFC 4566 §5. Порядок здесь
    /// не вопрос вкуса: часть реализаций разбирает SDP последовательно и на
    /// перестановке ломается.
    /// </summary>
    public string Encoded()
    {
        List<string> lines =
        [
            "v=0",
            string.Create(
                CultureInfo.InvariantCulture,
                $"o={Origin.Username} {Origin.SessionId} {Origin.SessionVersion} {Origin.NetworkType} {Origin.AddressType} {Origin.Address}"),
            $"s={SessionName}",
        ];

        if (Connection is SdpConnection connection)
        {
            lines.Add($"c={connection.NetworkType} {connection.AddressType} {connection.Address}");
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture, $"t={StartTime} {StopTime}"));

        foreach (SdpAttributeLine attribute in Attributes)
        {
            lines.Add(Encode(attribute));
        }

        foreach (MediaDescription section in Media)
        {
            string mediaLine = string.Create(
                CultureInfo.InvariantCulture,
                $"m={section.Type} {section.Port} {section.ProtocolName}");
            foreach (byte format in section.Formats)
            {
                mediaLine += string.Create(CultureInfo.InvariantCulture, $" {format}");
            }

            lines.Add(mediaLine);

            if (section.Connection is SdpConnection sectionConnection)
            {
                lines.Add(
                    $"c={sectionConnection.NetworkType} {sectionConnection.AddressType} {sectionConnection.Address}");
            }

            foreach (SdpAttributeLine attribute in section.Attributes)
            {
                lines.Add(Encode(attribute));
            }
        }

        return string.Concat(lines.Select(line => line + "\r\n"));
    }

    public byte[] EncodedData() => System.Text.Encoding.UTF8.GetBytes(Encoded());

    private static string Encode(SdpAttributeLine attribute) =>
        attribute.Value is string value ? $"a={attribute.Name}:{value}" : $"a={attribute.Name}";
}
