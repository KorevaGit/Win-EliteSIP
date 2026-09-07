namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Digest-аутентификация.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/DigestAuthenticationTests.swift</c>
/// вместе с эталонами: они посчитаны независимо, системной утилитой md5, а не
/// этим же кодом.
///
///   HA1 = md5("100:asterisk:elite100")
///   HA2 = md5("REGISTER:sip:127.0.0.1")
///   без qop:  md5("HA1:1234abcd:HA2")
///   с qop:    md5("HA1:1234abcd:00000001:0a4f113b:auth:HA2")
/// </summary>
public sealed class DigestAuthenticationTests
{
    private const string Ha1 = "88bf37ae053364b41dc76a3ba43f376e";
    private const string Ha2 = "7f83831edc2db7fc4a41972f6cbb2683";
    private const string ResponseWithoutQopValue = "612c4f24ca9a94443ae2f97d0bb86902";
    private const string ResponseWithQopValue = "12eedc275eeb4af2a1a9e8d118e3e6f4";
    private const string ResponseSessionAlgorithmValue = "5e81eb2c2e94a4600744b757753fb568";

    private static readonly DigestAuthentication.Credentials Credentials = new("100", "elite100");

    [Fact]
    public void MD5_считается_верно()
    {
        Assert.Equal(Ha1, DigestAuthentication.Md5Hex("100:asterisk:elite100"));
        Assert.Equal(Ha2, DigestAuthentication.Md5Hex("REGISTER:sip:127.0.0.1"));
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", DigestAuthentication.Md5Hex(""));
    }

    [Fact]
    public void Разбирает_вызов_в_том_виде_в_каком_его_шлёт_chan_sip()
    {
        DigestChallenge challenge = Require("Digest algorithm=MD5, realm=\"asterisk\", nonce=\"1234abcd\"");
        Assert.Equal("asterisk", challenge.Realm);
        Assert.Equal("1234abcd", challenge.Nonce);
        Assert.Equal("MD5", challenge.NormalizedAlgorithm);
        Assert.Empty(challenge.Qop);
        Assert.False(challenge.Stale);
        Assert.True(challenge.IsSupported);
    }

    [Fact]
    public void Разбирает_вызов_с_qop_opaque_и_stale()
    {
        DigestChallenge challenge = Require(
            "Digest realm=\"asterisk\", nonce=\"n1\", opaque=\"op1\", qop=\"auth,auth-int\", stale=TRUE");
        Assert.Equal("op1", challenge.Opaque);
        Assert.Equal(["auth", "auth-int"], challenge.Qop);

        // stale означает «повтори с тем же паролем», а не «пароль неверный».
        Assert.True(challenge.Stale);
    }

    [Fact]
    public void Чужие_схемы_не_притворяются_понятыми()
    {
        Assert.Null(DigestChallenge.Parse("Basic realm=\"asterisk\""));
        Assert.Null(DigestChallenge.Parse("Digest"));
        Assert.Null(DigestChallenge.Parse("Digest nonce=\"n1\""));
        Assert.Null(DigestChallenge.Parse("Digest realm=\"r\""));
    }

    [Fact]
    public void Ответ_без_qop_совпадает_с_эталоном()
    {
        string value = DigestAuthentication.AuthorizationValue(
            Credentials,
            new DigestChallenge("asterisk", "1234abcd"),
            SipMethod.Register,
            "sip:127.0.0.1");

        Assert.StartsWith("Digest ", value, StringComparison.Ordinal);
        Assert.Contains($"response=\"{ResponseWithoutQopValue}\"", value, StringComparison.Ordinal);
        Assert.Contains("username=\"100\"", value, StringComparison.Ordinal);
        Assert.Contains("realm=\"asterisk\"", value, StringComparison.Ordinal);
        Assert.Contains("nonce=\"1234abcd\"", value, StringComparison.Ordinal);
        Assert.Contains("uri=\"sip:127.0.0.1\"", value, StringComparison.Ordinal);
        Assert.Contains("algorithm=MD5", value, StringComparison.Ordinal);

        // Сервер qop не предлагал — не навязываем его сами.
        Assert.DoesNotContain("qop=", value, StringComparison.Ordinal);
        Assert.DoesNotContain("nc=", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Ответ_с_qop_auth_совпадает_с_эталоном()
    {
        string value = DigestAuthentication.AuthorizationValue(
            Credentials,
            new DigestChallenge("asterisk", "1234abcd", qop: ["auth"]),
            SipMethod.Register,
            "sip:127.0.0.1",
            nonceCount: 1,
            cnonce: "0a4f113b");

        Assert.Contains($"response=\"{ResponseWithQopValue}\"", value, StringComparison.Ordinal);
        Assert.Contains("qop=auth", value, StringComparison.Ordinal);
        Assert.Contains("nc=00000001", value, StringComparison.Ordinal);
        Assert.Contains("cnonce=\"0a4f113b\"", value, StringComparison.Ordinal);
    }

    [Fact]
    public void MD5_sess_считает_HA1_через_nonce_и_cnonce()
    {
        string value = DigestAuthentication.AuthorizationValue(
            Credentials,
            new DigestChallenge("asterisk", "1234abcd", algorithm: "MD5-sess"),
            SipMethod.Register,
            "sip:127.0.0.1",
            cnonce: "0a4f113b");

        Assert.Contains($"response=\"{ResponseSessionAlgorithmValue}\"", value, StringComparison.Ordinal);
        Assert.Contains("algorithm=MD5-sess", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Счётчик_nonce_попадает_в_ответ_восьмизначным()
    {
        string value = DigestAuthentication.AuthorizationValue(
            Credentials,
            new DigestChallenge("asterisk", "n", qop: ["auth"]),
            SipMethod.Register,
            "sip:host",
            nonceCount: 42,
            cnonce: "c");

        Assert.Contains("nc=0000002a", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Неподдерживаемый_алгоритм_ошибка_а_не_заведомо_неверный_ответ()
    {
        // RFC 8760 добавил SHA-256. chan_sip его не умеет, но если сервер
        // однажды потребует, лучше сказать это прямо, чем получить 403 без
        // объяснений.
        var challenge = new DigestChallenge("asterisk", "n", algorithm: "SHA-256");
        Assert.False(challenge.IsSupported);

        DigestAuthenticationException error = Assert.Throws<DigestAuthenticationException>(
            () => DigestAuthentication.AuthorizationValue(Credentials, challenge, SipMethod.Register, "sip:host"));

        Assert.Equal(DigestAuthenticationErrorKind.UnsupportedAlgorithm, error.Kind);
        Assert.Equal("SHA-256", error.Detail);
    }

    [Fact]
    public void Неизвестный_qop_тоже_ошибка()
    {
        var challenge = new DigestChallenge("asterisk", "n", qop: ["exotic"]);

        DigestAuthenticationException error = Assert.Throws<DigestAuthenticationException>(
            () => DigestAuthentication.AuthorizationValue(Credentials, challenge, SipMethod.Register, "sip:host"));

        Assert.Equal(DigestAuthenticationErrorKind.UnsupportedQualityOfProtection, error.Kind);
    }

    [Fact]
    public void Из_предложенных_qop_выбирается_auth()
    {
        Assert.Null(DigestAuthentication.SelectQualityOfProtection([]));
        Assert.Equal("auth", DigestAuthentication.SelectQualityOfProtection(["auth"]));
        Assert.Equal("auth", DigestAuthentication.SelectQualityOfProtection(["auth-int", "auth"]));
        Assert.Equal("auth", DigestAuthentication.SelectQualityOfProtection(["AUTH"]));
        Assert.Equal("auth-int", DigestAuthentication.SelectQualityOfProtection(["auth-int"]));
        Assert.Null(DigestAuthentication.SelectQualityOfProtection(["exotic"]));
    }

    [Fact]
    public void Ответ_407_требует_ProxyAuthorization_а_не_Authorization()
    {
        var headers = new SipHeaders();
        headers.Append("Proxy-Authenticate", "Digest realm=\"proxy\", nonce=\"n2\"");
        var response = new SipResponse(407, headers: headers);

        (DigestChallenge Challenge, string ResponseHeader) only = Assert.Single(response.AuthenticationChallenges());
        Assert.Equal("Proxy-Authorization", only.ResponseHeader);
        Assert.Equal("proxy", only.Challenge.Realm);
        Assert.True(response.IsAuthenticationRequired);
    }

    /// <summary>
    /// Проверка из набора разбора сообщений: 401 от chan_sip должен дать ровно
    /// один вызов, и отвечать на него надо заголовком Authorization.
    /// </summary>
    [Fact]
    public void Ответ_401_даёт_вызов_с_заголовком_Authorization()
    {
        var headers = new SipHeaders();
        headers.Append("WWW-Authenticate", "Digest algorithm=MD5, realm=\"asterisk\", nonce=\"1234abcd\"");
        var response = new SipResponse(401, headers: headers);

        (DigestChallenge Challenge, string ResponseHeader) only = Assert.Single(response.AuthenticationChallenges());
        Assert.Equal("asterisk", only.Challenge.Realm);
        Assert.Equal("1234abcd", only.Challenge.Nonce);
        Assert.Equal("Authorization", only.ResponseHeader);
    }

    private static DigestChallenge Require(string headerValue)
    {
        DigestChallenge? challenge = DigestChallenge.Parse(headerValue);
        Assert.NotNull(challenge);
        return challenge;
    }
}
