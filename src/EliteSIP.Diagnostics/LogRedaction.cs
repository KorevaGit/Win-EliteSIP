using System.Text;

namespace EliteSIP.Diagnostics;

/// <summary>
/// Маскирование секретов перед записью в файл.
///
/// Журнал на рабочем месте существует затем, чтобы его прислали в поддержку.
/// Значит всё, что в него попало, рано или поздно уедет через мессенджер, ляжет
/// в чужие «Загрузки» и попадёт в бэкап. Файл, который нельзя приложить к
/// обращению, бесполезен ровно так же, как отсутствующий, — поэтому секреты
/// вырезаются на записи, а не при отправке.
///
/// Маскируются поля, а не заголовки целиком. <c>Authorization</c> без
/// <c>response</c> остаётся полезным: по <c>realm</c>, <c>nonce</c> и
/// <c>algorithm</c> видно, чем именно сервер недоволен, а вот <c>response</c> —
/// это и есть ответ на вызов, то есть производная пароля. Вырезать заголовок
/// целиком значило бы сделать журнал бесполезным ради того, что и так закрыто
/// точечно.
///
/// Перенесено из macOS-версии (<c>Packages/Diagnostics/Sources/Diagnostics/LogRedaction.swift</c>)
/// вместе с тестами. Разбор посимвольный, а не регулярным выражением, по той же
/// причине, что и в оригинале: правила про кавычки, границы значения и
/// <c>inline:</c> в регулярном выражении читаются хуже, чем в коде, а править их
/// придётся тому, кто разбирает жалобу, а не тому, кто это писал.
/// </summary>
public static class LogRedaction
{
    /// <summary>
    /// Чем заменяется значение. Не пустая строка: по ней в журнале видно, что
    /// значение было и его убрали, а не что поле пришло пустым.
    /// </summary>
    public const string Placeholder = "скрыто";

    /// <summary>
    /// Ключевой материал SRTP из SDP:
    /// <c>a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:&lt;ключ&gt;|...</c>.
    ///
    /// Отдельным правилом, потому что отделяется двоеточием, а не знаком
    /// равенства. Это буквально ключ, которым шифруется разговор: в журнале ему
    /// не место ни в каком виде.
    /// </summary>
    internal const string InlineMarker = "inline:";

    /// <summary>
    /// Поля, значения которых не должны попадать в файл.
    ///
    /// Границы слова слева намеренно нет. <c>x-response=</c> или
    /// <c>authresponse=</c> тоже будут замаскированы, и это правильный перекос:
    /// лишняя маска стоит строки в журнале, пропущенная — пароля.
    /// </summary>
    internal static readonly string[] SecretFields =
    [
        "response",
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "apikey",
        "api_key",
    ];

    /// <summary>Убирает значения секретных полей из строки.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Быстрый выход: в подавляющем большинстве строк искать нечего, а
        // журнал пишется на каждое событие сигнализации.
        if (!ContainsCandidate(text))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);

        int index = 0;
        while (index < text.Length)
        {
            if (TryMatchSecretField(text, index, out int valueStart, out int valueEnd))
            {
                result.Append(text, index, valueStart - index);
                result.Append(Placeholder);
                index = valueEnd;
                continue;
            }

            if (TryMatchInline(text, index, out int inlineEnd))
            {
                result.Append(InlineMarker);
                result.Append(Placeholder);
                index = inlineEnd;
                continue;
            }

            result.Append(text[index]);
            index += 1;
        }

        return result.ToString();
    }

    private static bool ContainsCandidate(string text)
    {
        if (text.Contains(InlineMarker, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string field in SecretFields)
        {
            if (text.Contains(field, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Если с позиции начинается «поле = значение», отдаёт границы значения:
    /// откуда его вырезать и где продолжить разбор.
    /// </summary>
    private static bool TryMatchSecretField(string text, int index, out int valueStart, out int valueEnd)
    {
        valueStart = 0;
        valueEnd = 0;

        string? field = null;
        foreach (string candidate in SecretFields)
        {
            if (Matches(candidate, text, index))
            {
                field = candidate;
                break;
            }
        }

        if (field is null)
        {
            return false;
        }

        int cursor = SkipWhitespace(text, index + field.Length);
        if (cursor >= text.Length || text[cursor] != '=')
        {
            return false;
        }

        cursor = SkipWhitespace(text, cursor + 1);
        if (cursor >= text.Length)
        {
            return false;
        }

        // Значение в кавычках отдаётся вместе с ними: кавычки остаются в
        // журнале, чтобы строка не перестала быть похожей на заголовок.
        if (text[cursor] == '"')
        {
            int quoted = cursor + 1;
            while (quoted < text.Length && text[quoted] != '"')
            {
                quoted += 1;
            }

            valueStart = cursor + 1;
            valueEnd = Math.Min(quoted, text.Length);
            return true;
        }

        int end = cursor;
        while (end < text.Length && !IsValueTerminator(text[end]))
        {
            end += 1;
        }

        if (end <= cursor)
        {
            return false;
        }

        valueStart = cursor;
        valueEnd = end;
        return true;
    }

    /// <summary>
    /// Если с позиции начинается <c>inline:</c>, отдаёт конец ключевого
    /// материала. Маскируется весь токен, включая срок жизни и индекс: разбирать
    /// их в журнале незачем, а ошибиться границей — значит оставить часть ключа.
    /// </summary>
    private static bool TryMatchInline(string text, int index, out int end)
    {
        end = 0;
        if (!Matches(InlineMarker, text, index))
        {
            return false;
        }

        int cursor = index + InlineMarker.Length;
        while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]))
        {
            cursor += 1;
        }

        if (cursor <= index + InlineMarker.Length)
        {
            return false;
        }

        end = cursor;
        return true;
    }

    private static bool Matches(string needle, string text, int index)
    {
        if (index + needle.Length > text.Length)
        {
            return false;
        }

        return string.Compare(text, index, needle, 0, needle.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static int SkipWhitespace(string text, int index)
    {
        int cursor = index;
        while (cursor < text.Length && (text[cursor] == ' ' || text[cursor] == '\t'))
        {
            cursor += 1;
        }

        return cursor;
    }

    /// <summary>
    /// Где кончается значение без кавычек.
    ///
    /// Запятая и точка с запятой разделяют параметры в заголовках SIP, скобка
    /// закрывает вставку в нашем собственном тексте, пробел кончает токен.
    /// </summary>
    private static bool IsValueTerminator(char character)
    {
        return char.IsWhiteSpace(character)
            || character == ','
            || character == ';'
            || character == ')'
            || character == '"';
    }
}
