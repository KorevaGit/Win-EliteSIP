using System.Globalization;
using System.Text;

namespace EliteSIP.SipCore;

/// <summary>
/// Идентификатор диалога для <c>Replaces</c> (RFC 3891).
///
/// При консультационном переводе REFER уходит по исходному разговору, а в
/// Refer-To описывает второй, консультационный. Сервер создаёт новый INVITE к
/// адресату и этим заголовком просит заменить уже существующий диалог.
/// </summary>
public readonly record struct SipDialogIdentifier(string CallId, string LocalTag, string RemoteTag)
{
    public SipDialogIdentifier(SipDialog dialog)
        : this(
            (dialog ?? throw new ArgumentNullException(nameof(dialog))).CallId,
            dialog.LocalTag,
            dialog.RemoteTag)
    {
    }

    /// <summary>
    /// Значение Replaces с точки зрения получателя нового INVITE.
    ///
    /// Для локального диалога его собственный тег станет <c>from-tag</c>, а тег
    /// консультационного собеседника — <c>to-tag</c>: новый INVITE получит
    /// именно тот собеседник.
    /// </summary>
    public string HeaderValue => $"{CallId};to-tag={RemoteTag};from-tag={LocalTag}";

    /// <summary>
    /// Кодирует значение Replaces как значение URI-header в Refer-To.
    ///
    /// Точка с запятой и знак равенства внутри query обязаны быть
    /// percent-encoded: иначе часть прокси воспринимает их как параметры самого
    /// Refer-To URI. Список разрешённых — unreserved по RFC 3986 и ничего
    /// сверх: готовые наборы оставляют <c>%</c>, <c>@</c> и <c>&amp;</c>, а
    /// вложенное значение заголовка сохраняет смысл только с белым списком.
    /// </summary>
    public string PercentEncodedHeaderValue => SipPercentEncoding.Encode(HeaderValue, SipPercentEncoding.Unreserved);
}

/// <summary>Ход перевода после отправки REFER.</summary>
public abstract record SipTransferEvent
{
    private SipTransferEvent()
    {
    }

    /// <summary>Сервер принял REFER и начал перевод (обычно 202 Accepted).</summary>
    public sealed record Accepted : SipTransferEvent;

    /// <summary>NOTIFY сообщил успешный финальный ответ на созданный INVITE.</summary>
    public sealed record Succeeded : SipTransferEvent;

    /// <summary>REFER или созданный им INVITE завершился отказом.</summary>
    public sealed record Failed(int Status, string Reason) : SipTransferEvent;
}

/// <summary>
/// Почему перевод не состоялся.
///
/// Как и у звонка, наружу эти причины уходят только строкой — в событии
/// <see cref="SipTransferEvent.Failed"/>.
/// </summary>
public static class SipTransferErrors
{
    public const string NoActiveCall = "разговора нет";

    public const string EmptyTarget = "не задан номер перевода";

    public const string InvalidTarget = "номер перевода содержит недопустимые символы";

    public const string AlreadyTransferring = "перевод уже выполняется";

    public const string Timeout = "сервер не ответил";

    public const string NoResult = "сервер не сообщил результат перевода";

    public const string CallEnded = "разговор завершился во время перевода";

    public static string Rejected(int status, string reason) =>
        $"отказ {status.ToString(CultureInfo.InvariantCulture)} {reason}";

    public static string TransportFailed(string reason) => $"сеть: {reason}";
}

/// <summary>Процентное кодирование с белым списком — своё, потому что готовых с нужным набором нет.</summary>
internal static class SipPercentEncoding
{
    /// <summary>Unreserved по RFC 3986.</summary>
    public const string Unreserved = "-._~";

    /// <summary>
    /// То, что можно оставить в user-части SIP URI.
    ///
    /// Набор из оригинала: буквы, цифры и <c>-_.!~*'()+</c>. Плюс здесь не
    /// случайно — международный номер отличается от местного только им.
    /// </summary>
    public const string UserPart = "-_.!~*'()+";

    public static string Encode(string value, string extraAllowed)
    {
        var result = new StringBuilder(value.Length);
        foreach (byte octet in Encoding.UTF8.GetBytes(value))
        {
            char character = (char)octet;
            if (char.IsAsciiLetterOrDigit(character) || extraAllowed.Contains(character, StringComparison.Ordinal))
            {
                result.Append(character);
            }
            else
            {
                result.Append('%').Append(octet.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return result.ToString();
    }
}

internal static class SipFragment
{
    /// <summary>
    /// Разбирает первую строку <c>message/sipfrag</c>, присылаемого в NOTIFY:
    /// <c>SIP/2.0 200 OK</c>.
    /// </summary>
    public static (int Status, string Reason)? ParseStatus(ReadOnlyMemory<byte> body)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(body.Span);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        string firstLine = text.Split('\n', '\r')[0];
        string[] parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2
            || !string.Equals(parts[0], "SIP/2.0", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int status))
        {
            return null;
        }

        return (status, parts.Length == 3 ? parts[2] : string.Empty);
    }
}
