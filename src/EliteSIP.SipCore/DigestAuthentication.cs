using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EliteSIP.SipCore;

/// <summary>Разобранный вызов на аутентификацию из WWW-Authenticate или Proxy-Authenticate.</summary>
public sealed class DigestChallenge
{
    public DigestChallenge(
        string realm,
        string nonce,
        string? opaque = null,
        string? algorithm = null,
        IEnumerable<string>? qop = null,
        bool stale = false)
    {
        Realm = realm;
        Nonce = nonce;
        Opaque = opaque;
        Algorithm = algorithm;
        Qop = qop is null ? [] : [.. qop];
        Stale = stale;
    }

    public string Realm { get; }

    public string Nonce { get; }

    public string? Opaque { get; }

    /// <summary><see langword="null"/> означает MD5 — так по RFC 2617, и именно так шлёт chan_sip.</summary>
    public string? Algorithm { get; }

    public IReadOnlyList<string> Qop { get; }

    /// <summary>
    /// Сервер говорит, что nonce устарел: повторить можно тем же паролем, а не
    /// считать это ошибкой логина.
    /// </summary>
    public bool Stale { get; }

    /// <summary>Разбирает одно значение заголовка вида <c>Digest realm="…", nonce="…"</c>.</summary>
    public static DigestChallenge? Parse(ReadOnlySpan<char> headerValue)
    {
        ReadOnlySpan<char> text = headerValue.TrimSip();

        // Отделяем схему от параметров по первому пробелу.
        int space = text.IndexOf(' ');
        if (space < 0)
        {
            return null;
        }

        if (!text[..space].Equals("Digest", StringComparison.OrdinalIgnoreCase))
        {
            // Basic и прочее не поддерживаем сознательно: SIP-серверы им не
            // пользуются, а молчаливая поддержка спрятала бы реальную проблему.
            return null;
        }

        string? realm = null;
        string? nonce = null;
        string? opaque = null;
        string? algorithm = null;
        List<string> qop = [];
        bool stale = false;

        // Запятые здесь разделяют параметры одного вызова, а не разные значения,
        // и внутри кавычек их игнорировать обязательно.
        foreach (string piece in SipLexer.SplitTopLevel(text[(space + 1)..], ','))
        {
            string parameter = piece.TrimSip();
            int equals = parameter.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                continue;
            }

            string name = parameter[..equals].TrimSip().ToLowerInvariant();
            string value = SipLexer.Unquoted(parameter.AsSpan(equals + 1).TrimSip());

            switch (name)
            {
                case "realm": realm = value; break;
                case "nonce": nonce = value; break;
                case "opaque": opaque = value; break;
                case "algorithm": algorithm = value; break;
                case "stale": stale = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                case "qop":
                    qop = [.. value.Split(',').Select(part => part.TrimSip())];
                    break;
                default: break;
            }
        }

        return realm is null || nonce is null
            ? null
            : new DigestChallenge(realm, nonce, opaque, algorithm, qop, stale);
    }

    /// <summary>Нормализованный алгоритм.</summary>
    public string NormalizedAlgorithm => (Algorithm ?? "MD5").ToUpperInvariant();

    public bool IsSupported => NormalizedAlgorithm is "MD5" or "MD5-SESS";
}

public enum DigestAuthenticationErrorKind
{
    /// <summary>
    /// Сервер требует алгоритм, которого мы не умеем (например SHA-256 по
    /// RFC 8760). Лучше сказать это явно, чем послать заведомо неверный ответ и
    /// получить 403 без объяснений.
    /// </summary>
    UnsupportedAlgorithm,
    UnsupportedQualityOfProtection,
}

public sealed class DigestAuthenticationException : Exception
{
    public DigestAuthenticationException()
        : this(DigestAuthenticationErrorKind.UnsupportedAlgorithm, string.Empty)
    {
    }

    public DigestAuthenticationException(string message)
        : this(DigestAuthenticationErrorKind.UnsupportedAlgorithm, message)
    {
    }

    public DigestAuthenticationException(string message, Exception innerException)
        : base(message, innerException) => Detail = message;

    public DigestAuthenticationException(DigestAuthenticationErrorKind kind, string detail)
        : base($"{kind}: {detail}")
    {
        Kind = kind;
        Detail = detail;
    }

    public DigestAuthenticationErrorKind Kind { get; }

    public string Detail { get; } = string.Empty;
}

/// <summary>Вычисление ответа на digest-вызов (RFC 2617, в объёме, который использует SIP).</summary>
public static class DigestAuthentication
{
    public readonly record struct Credentials(string Username, string Password);

    /// <summary>
    /// Собирает значение для заголовка Authorization или Proxy-Authorization.
    /// </summary>
    /// <param name="digestUri">
    /// Значение параметра <c>uri</c>. Это Request-URI запроса как строка, и она
    /// должна совпадать байт в байт с тем, что уйдёт в стартовой строке —
    /// сервер считает хеш от неё же.
    /// </param>
    /// <param name="nonceCount">Счётчик использования nonce, нужен только при qop.</param>
    public static string AuthorizationValue(
        Credentials credentials,
        DigestChallenge challenge,
        SipMethod method,
        string digestUri,
        int nonceCount = 1,
        string? cnonce = null,
        ReadOnlySpan<byte> body = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        if (!challenge.IsSupported)
        {
            throw new DigestAuthenticationException(
                DigestAuthenticationErrorKind.UnsupportedAlgorithm,
                challenge.NormalizedAlgorithm);
        }

        string? selectedQop = SelectQualityOfProtection(challenge.Qop);
        if (challenge.Qop.Count > 0 && selectedQop is null)
        {
            throw new DigestAuthenticationException(
                DigestAuthenticationErrorKind.UnsupportedQualityOfProtection,
                string.Join(", ", challenge.Qop));
        }

        string clientNonce = cnonce ?? SipToken.Random(16);

        string ha1 = Md5Hex($"{credentials.Username}:{challenge.Realm}:{credentials.Password}");
        if (string.Equals(challenge.NormalizedAlgorithm, "MD5-SESS", StringComparison.Ordinal))
        {
            ha1 = Md5Hex($"{ha1}:{challenge.Nonce}:{clientNonce}");
        }

        string ha2 = string.Equals(selectedQop, "auth-int", StringComparison.Ordinal)
            ? Md5Hex($"{method.Name()}:{digestUri}:{Md5Hex(body)}")
            : Md5Hex($"{method.Name()}:{digestUri}");

        string nonceCountText = nonceCount.ToString("x8", CultureInfo.InvariantCulture);

        string response = selectedQop is null
            ? Md5Hex($"{ha1}:{challenge.Nonce}:{ha2}")
            : Md5Hex($"{ha1}:{challenge.Nonce}:{nonceCountText}:{clientNonce}:{selectedQop}:{ha2}");

        List<string> parameters =
        [
            $"username={Quoted(credentials.Username)}",
            $"realm={Quoted(challenge.Realm)}",
            $"nonce={Quoted(challenge.Nonce)}",
            $"uri={Quoted(digestUri)}",
            $"response={Quoted(response)}",
        ];

        // algorithm передаём без кавычек — это token, и некоторые реализации
        // спотыкаются на закавыченном значении.
        parameters.Add(string.Equals(challenge.NormalizedAlgorithm, "MD5-SESS", StringComparison.Ordinal)
            ? "algorithm=MD5-sess"
            : "algorithm=MD5");

        if (selectedQop is not null)
        {
            parameters.Add($"qop={selectedQop}");
            parameters.Add($"nc={nonceCountText}");
            parameters.Add($"cnonce={Quoted(clientNonce)}");
        }

        if (challenge.Opaque is string opaque)
        {
            parameters.Add($"opaque={Quoted(opaque)}");
        }

        return "Digest " + string.Join(", ", parameters);
    }

    /// <summary>
    /// Из предложенных сервером вариантов выбираем <c>auth</c>: <c>auth-int</c>
    /// требует хеша тела и ничего не даёт против MITM, а поддержка обоих
    /// удваивает поверхность для ошибок.
    /// </summary>
    internal static string? SelectQualityOfProtection(IReadOnlyList<string> offered)
    {
        if (offered.Count == 0)
        {
            return null;
        }

        foreach (string value in offered)
        {
            if (string.Equals(value, "auth", StringComparison.OrdinalIgnoreCase))
            {
                return "auth";
            }
        }

        foreach (string value in offered)
        {
            if (string.Equals(value, "auth-int", StringComparison.OrdinalIgnoreCase))
            {
                return "auth-int";
            }
        }

        return null;
    }

    // Хеши

    internal static string Md5Hex(string text) => Md5Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// MD5 здесь не выбор, а требование протокола: digest в SIP считается
    /// именно им, и подменить его нечем — сервер посчитает свой хеш по RFC 2617
    /// и сравнит. Анализатор об этом не знает, поэтому предупреждение о
    /// сломанной криптографии снимается здесь и только здесь.
    /// </summary>
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms
    internal static string Md5Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(MD5.HashData(data));
#pragma warning restore CA5351

    private static string Quoted(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

public static class SipResponseAuthenticationExtensions
{
    /// <summary>
    /// Вызовы на аутентификацию из ответа.
    ///
    /// 401 приходит от registrar и требует Authorization, 407 — от прокси и
    /// требует Proxy-Authorization. Путать их нельзя, поэтому возвращаем вместе
    /// с именем заголовка, которым надо отвечать.
    /// </summary>
    public static IReadOnlyList<(DigestChallenge Challenge, string ResponseHeader)> AuthenticationChallenges(
        this SipResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        List<(DigestChallenge, string)> result = [];

        foreach (string value in response.Headers.Values(SipHeaderName.WwwAuthenticate))
        {
            if (DigestChallenge.Parse(value) is DigestChallenge challenge)
            {
                result.Add((challenge, SipHeaderName.Authorization));
            }
        }

        foreach (string value in response.Headers.Values(SipHeaderName.ProxyAuthenticate))
        {
            if (DigestChallenge.Parse(value) is DigestChallenge challenge)
            {
                result.Add((challenge, SipHeaderName.ProxyAuthorization));
            }
        }

        return result;
    }
}
