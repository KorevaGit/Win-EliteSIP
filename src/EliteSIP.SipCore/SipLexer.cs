using System.Text;

namespace EliteSIP.SipCore;

/// <summary>
/// Мелкая лексика SIP, общая для URI, Via, name-addr и заголовков авторизации.
///
/// Ключевая мысль всего файла: наивный Split в SIP почти всегда неверен.
/// Запятая внутри <c>"display, name"</c> не разделяет значения, точка с запятой
/// внутри <c>&lt;sip:a@b;transport=tls&gt;</c> не начинает параметр заголовка, а
/// в <c>Digest realm="a", nonce="b"</c> запятая разделяет параметры одного
/// значения, а не два значения. Поэтому всё режется с учётом кавычек и угловых
/// скобок.
///
/// Перенесено из <c>Packages/SIPCore/Sources/SIPCore/SIPLexing.swift</c>.
/// </summary>
internal static class SipLexer
{
    /// <summary>
    /// Символы, из-за которых значение обязано уехать в кавычках: пробел,
    /// табуляция, кавычка, обратная косая и всё, что в грамматике RFC 3261
    /// служит разделителем.
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> NeedsQuoting =
        System.Buffers.SearchValues.Create(" \t\"\\,;:<>@()[]{}?=/");

    /// <summary>
    /// Режет строку по разделителю, игнорируя разделители внутри двойных
    /// кавычек и внутри угловых скобок.
    /// </summary>
    public static List<string> SplitTopLevel(ReadOnlySpan<char> text, char separator, bool omittingEmpty = true)
    {
        List<string> result = [];
        int start = 0;
        bool inQuotes = false;
        bool inAngles = false;
        bool escaped = false;

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];

            if (escaped)
            {
                escaped = false;
            }
            else if (inQuotes)
            {
                if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }
            }
            else if (character == '"')
            {
                inQuotes = true;
            }
            else if (character == '<')
            {
                inAngles = true;
            }
            else if (character == '>')
            {
                inAngles = false;
            }
            else if (character == separator && !inAngles)
            {
                ReadOnlySpan<char> piece = text[start..index];
                if (!omittingEmpty || !piece.TrimSip().IsEmpty)
                {
                    result.Add(piece.ToString());
                }
                start = index + 1;
            }
        }

        ReadOnlySpan<char> tail = text[start..];
        if (!omittingEmpty || !tail.TrimSip().IsEmpty)
        {
            result.Add(tail.ToString());
        }
        return result;
    }

    /// <summary>
    /// Разбирает список параметров вида <c>;a=1;b="x;y";flag</c>.
    /// Ведущая точка с запятой не обязательна.
    /// </summary>
    public static List<SipParameter> ParseParameters(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> body = text;
        if (body.Length > 0 && body[0] == ';')
        {
            body = body[1..];
        }

        List<SipParameter> parameters = [];
        foreach (string raw in SplitTopLevel(body, ';'))
        {
            string piece = raw.TrimSip();
            if (piece.Length == 0)
            {
                continue;
            }

            int equals = piece.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                parameters.Add(new SipParameter(piece));
                continue;
            }

            string name = piece[..equals].TrimSip();
            if (name.Length == 0)
            {
                continue;
            }
            parameters.Add(new SipParameter(name, Unquoted(piece.AsSpan(equals + 1).TrimSip())));
        }
        return parameters;
    }

    /// <summary>Снимает обрамляющие кавычки и раскрывает экранирование внутри них.</summary>
    public static string Unquoted(ReadOnlySpan<char> text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return text.ToString();
        }

        ReadOnlySpan<char> inner = text[1..^1];
        var result = new StringBuilder(inner.Length);
        bool escaped = false;
        foreach (char character in inner)
        {
            if (escaped)
            {
                result.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else
            {
                result.Append(character);
            }
        }
        return result.ToString();
    }

    /// <summary>Оборачивает значение в кавычки, если без них оно было бы разобрано неверно.</summary>
    public static string QuotedIfNeeded(string value)
    {
        bool needsQuotes = value.Length == 0 || value.AsSpan().IndexOfAny(NeedsQuoting) >= 0;
        if (!needsQuotes)
        {
            return value;
        }

        var escaped = new StringBuilder(value.Length + 2);
        escaped.Append('"');
        foreach (char character in value)
        {
            if (character is '"' or '\\')
            {
                escaped.Append('\\');
            }
            escaped.Append(character);
        }
        escaped.Append('"');
        return escaped.ToString();
    }

    /// <summary>
    /// Обрезка пробелов и табуляций. Именно их, а не всего, что
    /// <c>char.IsWhiteSpace</c> считает пробелом: в заголовке SIP перевод
    /// строки — это граница, а не пробел, и съедать его молча нельзя.
    /// </summary>
    public static ReadOnlySpan<char> TrimSip(this ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> result = text;
        while (result.Length > 0 && (result[0] == ' ' || result[0] == '\t'))
        {
            result = result[1..];
        }
        while (result.Length > 0 && (result[^1] == ' ' || result[^1] == '\t'))
        {
            result = result[..^1];
        }
        return result;
    }

    public static string TrimSip(this string text) => TrimSip(text.AsSpan()).ToString();
}
