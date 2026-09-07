using System.Globalization;
using System.Text;

namespace EliteSIP.SipCore;

/// <summary>Чем именно сообщение не устроило разбор.</summary>
public enum SipParseErrorKind
{
    Empty,
    MalformedStartLine,
    UnsupportedVersion,
    MalformedHeader,
    UnknownMethod,
    InvalidRequestUri,
    InvalidStatusCode,
    MissingHeaderTerminator,
    TruncatedBody,
    InvalidEncoding,
}

/// <summary>
/// Сообщение не разобралось.
///
/// В оригинале это была ошибка-значение (<c>throws</c> в Swift — часть
/// сигнатуры); в C# ближайшее — исключение с полем <see cref="Kind"/>.
/// Текст детали не переводится: это трасса протокола, и читать её будут рядом с
/// дампом Wireshark.
/// </summary>
public sealed class SipParseException : Exception
{
    public SipParseException()
        : this(SipParseErrorKind.MalformedStartLine, string.Empty)
    {
    }

    public SipParseException(string message)
        : this(SipParseErrorKind.MalformedStartLine, message)
    {
    }

    public SipParseException(string message, Exception innerException)
        : base(message, innerException) => Detail = message;

    public SipParseException(SipParseErrorKind kind, string detail)
        : base($"{kind}: {detail}")
    {
        Kind = kind;
        Detail = detail;
    }

    public SipParseException(SipParseErrorKind kind, string detail, Exception innerException)
        : base($"{kind}: {detail}", innerException)
    {
        Kind = kind;
        Detail = detail;
    }

    public SipParseErrorKind Kind { get; }

    public string Detail { get; } = string.Empty;
}

public static class SipParser
{
    /// <summary>
    /// Разбирает одно полное сообщение.
    ///
    /// Тело возвращается сырыми байтами: заголовки в SIP текстовые, а тело —
    /// нет (в нашем случае SDP тоже текст, но декодировать его должен тот, кто
    /// знает Content-Type).
    /// </summary>
    public static SipMessage Parse(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new SipParseException(SipParseErrorKind.Empty, "нет данных");
        }

        (int Start, int End)? boundary = FindHeaderTerminator(data.Span);
        if (boundary is null)
        {
            throw new SipParseException(
                SipParseErrorKind.MissingHeaderTerminator,
                "не найден пустой перевод строки после заголовков");
        }

        string headerText;
        try
        {
            headerText = new UTF8Encoding(false, true).GetString(data.Span[..boundary.Value.Start]);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SipParseException(SipParseErrorKind.InvalidEncoding, "заголовки не в UTF-8", exception);
        }

        List<string> lines = Unfold(headerText);
        if (lines.Count == 0 || lines[0].Length == 0)
        {
            throw new SipParseException(SipParseErrorKind.MalformedStartLine, "пустая стартовая строка");
        }

        string startLine = lines[0];
        var headers = new SipHeaders();
        for (int index = 1; index < lines.Count; index++)
        {
            string line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                throw new SipParseException(SipParseErrorKind.MalformedHeader, $"нет двоеточия: {line}");
            }

            string name = line[..colon].TrimSip();
            if (name.Length == 0)
            {
                throw new SipParseException(SipParseErrorKind.MalformedHeader, $"пустое имя заголовка: {line}");
            }

            headers.Append(name, line[(colon + 1)..].TrimSip());
        }

        ReadOnlyMemory<byte> bodyData = data[boundary.Value.End..];
        ReadOnlyMemory<byte> body;
        if (headers.Number(SipHeaderName.ContentLength) is int declared)
        {
            if (declared < 0)
            {
                throw new SipParseException(SipParseErrorKind.MalformedHeader, "отрицательный Content-Length");
            }
            if (bodyData.Length < declared)
            {
                throw new SipParseException(
                    SipParseErrorKind.TruncatedBody,
                    $"объявлено {declared} байт, получено {bodyData.Length}");
            }
            body = bodyData[..declared];
        }
        else
        {
            body = bodyData;
        }

        return startLine.StartsWith("SIP/", StringComparison.Ordinal)
            ? ParseResponse(startLine, headers, body)
            : ParseRequest(startLine, headers, body);
    }

    // Стартовые строки

    private static SipRequest ParseRequest(string startLine, SipHeaders headers, ReadOnlyMemory<byte> body)
    {
        string[] parts = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            throw new SipParseException(SipParseErrorKind.MalformedStartLine, startLine);
        }

        if (!string.Equals(parts[^1], "SIP/2.0", StringComparison.OrdinalIgnoreCase))
        {
            throw new SipParseException(SipParseErrorKind.UnsupportedVersion, parts[^1]);
        }

        SipMethod? method = SipMethodExtensions.Parse(parts[0]);
        if (method is null)
        {
            throw new SipParseException(SipParseErrorKind.UnknownMethod, parts[0]);
        }

        // Request-URI не может содержать пробелов, так что склеивать середину
        // не нужно — но если их больше одного, строка битая.
        SipUri? uri = parts.Length == 3 ? SipUri.Parse(parts[1]) : null;
        if (uri is null)
        {
            throw new SipParseException(SipParseErrorKind.InvalidRequestUri, parts[1]);
        }

        return new SipRequest(method.Value, uri, headers, body);
    }

    private static SipResponse ParseResponse(string startLine, SipHeaders headers, ReadOnlyMemory<byte> body)
    {
        // Reason-phrase может содержать пробелы, поэтому режем максимум на 3 части.
        string[] parts = startLine.Split(' ', 3);
        if (parts.Length < 2)
        {
            throw new SipParseException(SipParseErrorKind.MalformedStartLine, startLine);
        }

        if (!string.Equals(parts[0], "SIP/2.0", StringComparison.OrdinalIgnoreCase))
        {
            throw new SipParseException(SipParseErrorKind.UnsupportedVersion, parts[0]);
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int statusCode)
            || statusCode is < 100 or >= 700)
        {
            throw new SipParseException(SipParseErrorKind.InvalidStatusCode, parts[1]);
        }

        string reason = parts.Length > 2 ? parts[2].TrimSip() : string.Empty;
        return new SipResponse(statusCode, reason.Length == 0 ? null : reason, headers, body);
    }

    // Вспомогательное

    /// <summary>
    /// Ищет границу «заголовки / тело»: CRLFCRLF или, снисходительно, LFLF.
    ///
    /// Снисходительность здесь оправдана: своё мы всегда пишем с CRLF, но
    /// принимать от чужой реализации сообщение с LF дешевле, чем разбираться,
    /// почему звонок не идёт.
    /// </summary>
    internal static (int Start, int End)? FindHeaderTerminator(ReadOnlySpan<byte> data)
    {
        for (int index = 0; index < data.Length; index++)
        {
            if (data[index] == 0x0D
                && index + 3 < data.Length
                && data[index + 1] == 0x0A && data[index + 2] == 0x0D && data[index + 3] == 0x0A)
            {
                return (index, index + 4);
            }

            if (data[index] == 0x0A && index + 1 < data.Length && data[index + 1] == 0x0A)
            {
                return (index, index + 2);
            }
        }
        return null;
    }

    /// <summary>
    /// Разворачивает свёрнутые заголовки: продолжение строки начинается с
    /// пробела или табуляции и по RFC 3261 §7.3.1 склеивается с предыдущей.
    /// </summary>
    internal static List<string> Unfold(string text)
    {
        List<string> joined = [];
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && joined.Count > 0)
            {
                joined[^1] += " " + line.TrimSip();
            }
            else
            {
                joined.Add(line);
            }
        }
        return joined;
    }
}
