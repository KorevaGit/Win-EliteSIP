using System.Text;

namespace EliteSIP.MediaCore;

/// <summary>Почему тело SDP не разобралось.</summary>
public enum SdpParseFailure
{
    Empty,
    MissingOrigin,
    MalformedOrigin,
    MalformedMedia,
    MalformedConnection,
    UnsupportedVersion,
    InvalidEncoding,
}

/// <summary>Тело SDP разобрать не удалось.</summary>
public sealed class SdpParseException : Exception
{
    public SdpParseException(SdpParseFailure failure, string detail)
        : base($"{failure}: {detail}")
    {
        Failure = failure;
        Detail = detail;
    }

    public SdpParseException()
        : this(SdpParseFailure.Empty, string.Empty)
    {
    }

    public SdpParseException(string message)
        : base(message) => Detail = message;

    public SdpParseException(string message, Exception innerException)
        : base(message, innerException) => Detail = message;

    public SdpParseFailure Failure { get; }

    public string Detail { get; } = string.Empty;
}

/// <summary>
/// Разбор SDP.
///
/// В оригинале это инициализатор в расширении <c>SessionDescription</c>. Здесь
/// отдельный класс: разбор и модель не обязаны лежать в одном файле, а разница
/// в том, что вызов читается как <c>SdpParser.Parse</c>, а не как конструктор,
/// который может бросить.
/// </summary>
public static class SdpParser
{
    public static SessionDescription Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Переводы строк нормализуем заранее: в теле встречаются и CRLF, и LF,
        // а в паре мест — конец без перевода строки вовсе.
        string[] lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)
        {
            throw new SdpParseException(SdpParseFailure.Empty, "нет строк");
        }

        SdpOrigin? origin = null;
        string sessionName = "-";
        SdpConnection? sessionConnection = null;
        ulong startTime = 0;
        ulong stopTime = 0;
        List<SdpAttributeLine> sessionAttributes = [];
        List<MediaDescription> mediaSections = [];
        List<string> unknown = [];

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length < 2 || line[1] != '=')
            {
                if (line.Length > 0)
                {
                    unknown.Add(line);
                }

                continue;
            }

            char type = line[0];
            string value = line[2..];

            switch (type)
            {
                case 'v':
                    if (value != "0")
                    {
                        throw new SdpParseException(SdpParseFailure.UnsupportedVersion, value);
                    }

                    break;

                case 'o':
                    origin = ParseOrigin(value);
                    break;

                case 's':
                    sessionName = value;
                    break;

                case 'c':
                {
                    SdpConnection connection = ParseConnection(value);
                    // Строка c= до первой m= относится к сессии, после — к секции.
                    if (mediaSections.Count == 0)
                    {
                        sessionConnection = connection;
                    }
                    else
                    {
                        mediaSections[^1].Connection = connection;
                    }

                    break;
                }

                case 't':
                {
                    string[] times = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    startTime = times.Length > 0 && ulong.TryParse(times[0], out ulong start) ? start : 0;
                    stopTime = times.Length > 1 && ulong.TryParse(times[1], out ulong stop) ? stop : 0;
                    break;
                }

                case 'm':
                    mediaSections.Add(ParseMedia(value));
                    break;

                case 'a':
                {
                    SdpAttributeLine attribute = ParseAttribute(value);
                    if (mediaSections.Count == 0)
                    {
                        sessionAttributes.Add(attribute);
                    }
                    else
                    {
                        mediaSections[^1].Attributes.Add(attribute);
                    }

                    break;
                }

                default:
                    // i=, u=, e=, p=, b=, k=, z=, r= — не нужны, но и не теряются.
                    unknown.Add(line);
                    break;
            }
        }

        if (origin is null)
        {
            throw new SdpParseException(SdpParseFailure.MissingOrigin, "нет строки o=");
        }

        return new SessionDescription(
            origin,
            sessionName,
            sessionConnection,
            startTime,
            stopTime,
            sessionAttributes,
            mediaSections,
            unknown);
    }

    public static SessionDescription Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            return Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data));
        }
        catch (DecoderFallbackException)
        {
            throw new SdpParseException(SdpParseFailure.InvalidEncoding, "тело не в UTF-8");
        }
    }

    private static SdpOrigin ParseOrigin(string value)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6
            || !ulong.TryParse(parts[1], out ulong sessionId)
            || !ulong.TryParse(parts[2], out ulong sessionVersion))
        {
            throw new SdpParseException(SdpParseFailure.MalformedOrigin, value);
        }

        return new SdpOrigin(sessionId, parts[5], parts[0], sessionVersion, parts[3], parts[4]);
    }

    private static SdpConnection ParseConnection(string value)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            throw new SdpParseException(SdpParseFailure.MalformedConnection, value);
        }

        // У адреса может быть суффикс TTL или числа адресов: 224.0.0.1/127/2.
        string address = parts[2].Split('/')[0];
        return new SdpConnection(address, parts[0], parts[1]);
    }

    private static MediaDescription ParseMedia(string value)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            throw new SdpParseException(SdpParseFailure.MalformedMedia, value);
        }

        // Порт может идти с числом портов: 5004/2.
        if (!ushort.TryParse(parts[1].Split('/')[0], out ushort port))
        {
            throw new SdpParseException(SdpParseFailure.MalformedMedia, value);
        }

        List<byte> formats = [];
        foreach (string format in parts.Skip(3))
        {
            if (byte.TryParse(format, out byte payloadType))
            {
                formats.Add(payloadType);
            }
        }

        return new MediaDescription(port, parts[0], parts[2], formats);
    }

    private static SdpAttributeLine ParseAttribute(string value)
    {
        int colon = value.IndexOf(':');
        return colon < 0
            ? new SdpAttributeLine(value)
            : new SdpAttributeLine(value[..colon], value[(colon + 1)..]);
    }
}
