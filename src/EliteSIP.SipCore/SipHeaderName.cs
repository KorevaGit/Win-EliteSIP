using System.Text;

namespace EliteSIP.SipCore;

/// <summary>
/// Канонизация имён заголовков.
///
/// В SIP имя заголовка регистронезависимо и у части заголовков есть
/// однобуквенная компактная форма (RFC 3261 §20). Хранить их как пришло —
/// значит ловить баги вида «<c>Call-ID</c> есть, а <c>call-id</c> нет».
/// Поэтому при разборе имя сразу приводится к каноническому виду, а компактная
/// форма разворачивается.
/// </summary>
public static class SipHeaderName
{
    // Часто используемые имена

    public const string Via = "Via";
    public const string From = "From";
    public const string To = "To";
    public const string CallId = "Call-ID";
    public const string CSeq = "CSeq";
    public const string Contact = "Contact";
    public const string MaxForwards = "Max-Forwards";
    public const string Expires = "Expires";
    public const string ContentLength = "Content-Length";
    public const string ContentType = "Content-Type";
    public const string UserAgent = "User-Agent";
    public const string Allow = "Allow";
    public const string Supported = "Supported";
    public const string Authorization = "Authorization";
    public const string ProxyAuthorization = "Proxy-Authorization";
    public const string WwwAuthenticate = "WWW-Authenticate";
    public const string ProxyAuthenticate = "Proxy-Authenticate";
    public const string Route = "Route";
    public const string RecordRoute = "Record-Route";
    public const string ReferTo = "Refer-To";
    public const string ReferredBy = "Referred-By";
    public const string Event = "Event";
    public const string SubscriptionState = "Subscription-State";
    public const string MinExpires = "Min-Expires";
    public const string RetryAfter = "Retry-After";
    public const string Reason = "Reason";
    public const string Require = "Require";

    /// <summary>Компактные формы (RFC 3261 §20 и RFC 3515/6665 для refer/event).</summary>
    private static readonly Dictionary<string, string> CompactForms = new(StringComparer.Ordinal)
    {
        ["i"] = CallId,
        ["m"] = Contact,
        ["e"] = "Content-Encoding",
        ["l"] = ContentLength,
        ["c"] = ContentType,
        ["f"] = From,
        ["s"] = "Subject",
        ["k"] = Supported,
        ["t"] = To,
        ["v"] = Via,
        ["r"] = ReferTo,
        ["b"] = ReferredBy,
        ["o"] = Event,
        ["u"] = "Allow-Events",
    };

    /// <summary>Каноническое написание для заголовков, где Title-Case недостаточно.</summary>
    private static readonly Dictionary<string, string> CanonicalSpellings = BuildCanonicalSpellings();

    /// <summary>
    /// Заголовки, у которых несколько значений законно перечисляются через
    /// запятую в одной строке.
    ///
    /// Список именно белый, а не чёрный, и это принципиально: в
    /// <c>WWW-Authenticate: Digest realm="a", nonce="b"</c> запятая разделяет
    /// параметры одного значения. Разрезав такой заголовок, мы получим мусор.
    /// </summary>
    private static readonly HashSet<string> CommaSeparatedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        Via, Route, RecordRoute, Contact, Allow, Supported, Require,
        "Proxy-Require", "Unsupported", "Allow-Events", "Accept",
        "Content-Encoding", "Accept-Encoding", "Accept-Language", "In-Reply-To",
    };

    private static Dictionary<string, string> BuildCanonicalSpellings()
    {
        string[] names =
        [
            Via, From, To, CallId, CSeq, Contact, MaxForwards, Expires,
            ContentLength, ContentType, UserAgent, Allow, Supported,
            Authorization, ProxyAuthorization, WwwAuthenticate, ProxyAuthenticate,
            Route, RecordRoute, ReferTo, ReferredBy, Event, SubscriptionState,
            MinExpires, RetryAfter, Reason, Require,
            "Content-Encoding", "Subject", "Allow-Events", "Accept",
            "Session-Expires", "Min-SE", "Unsupported", "Proxy-Require",
            "Warning", "Server", "Date", "Timestamp", "Organization",
            "Priority", "In-Reply-To", "Replaces",
        ];

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            result.TryAdd(name.ToLowerInvariant(), name);
        }
        return result;
    }

    /// <summary>Приводит имя к каноническому виду, разворачивая компактную форму.</summary>
    public static string Canonical(ReadOnlySpan<char> name)
    {
        string lowered = name.TrimSip().ToString().ToLowerInvariant();

        if (lowered.Length == 1 && CompactForms.TryGetValue(lowered, out string? expanded))
        {
            return expanded;
        }

        if (CanonicalSpellings.TryGetValue(lowered, out string? known))
        {
            return known;
        }

        // Незнакомый заголовок: Title-Case по дефисам, чтобы хотя бы выглядел
        // однородно с остальными.
        var result = new StringBuilder(lowered.Length);
        bool atPartStart = true;
        foreach (char character in lowered)
        {
            if (character == '-')
            {
                result.Append('-');
                atPartStart = true;
                continue;
            }
            result.Append(atPartStart ? char.ToUpperInvariant(character) : character);
            atPartStart = false;
        }
        return result.ToString();
    }

    public static bool AllowsCommaSeparatedValues(string canonicalName) =>
        CommaSeparatedHeaders.Contains(canonicalName);
}
