namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Канонизация имён и набор заголовков.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/SIPHeadersTests.swift</c>.
/// </summary>
public sealed class SipHeadersTests
{
    [Fact]
    public void Компактные_формы_разворачиваются_в_канонические_имена()
    {
        Assert.Equal("Via", SipHeaderName.Canonical("v"));
        Assert.Equal("From", SipHeaderName.Canonical("f"));
        Assert.Equal("To", SipHeaderName.Canonical("t"));
        Assert.Equal("Call-ID", SipHeaderName.Canonical("i"));
        Assert.Equal("Contact", SipHeaderName.Canonical("m"));
        Assert.Equal("Content-Length", SipHeaderName.Canonical("l"));
        Assert.Equal("Content-Type", SipHeaderName.Canonical("c"));
    }

    [Fact]
    public void Регистр_в_имени_не_важен_написание_нормализуется()
    {
        Assert.Equal("Call-ID", SipHeaderName.Canonical("call-id"));
        Assert.Equal("CSeq", SipHeaderName.Canonical("CSEQ"));
        Assert.Equal("WWW-Authenticate", SipHeaderName.Canonical("www-authenticate"));
        Assert.Equal("Max-Forwards", SipHeaderName.Canonical("  Max-Forwards  "));

        // Незнакомый заголовок хотя бы приводится к однородному виду.
        Assert.Equal("X-Custom-Thing", SipHeaderName.Canonical("x-custom-thing"));
    }

    [Fact]
    public void Поиск_работает_по_любому_написанию_имени()
    {
        var headers = new SipHeaders();
        headers.Append("Call-ID", "abc@host");
        Assert.Equal("abc@host", headers.First("call-id"));
        Assert.Equal("abc@host", headers.First("i"));
        Assert.True(headers.Contains("CALL-ID"));
    }

    [Fact]
    public void Via_через_запятую_разворачивается_в_отдельные_значения()
    {
        var headers = new SipHeaders();
        headers.Append(
            "Via",
            "SIP/2.0/UDP first.example.com;branch=z9hG4bK1, SIP/2.0/UDP second.example.com;branch=z9hG4bK2");

        IReadOnlyList<string> values = headers.Values("Via");
        Assert.Equal(2, values.Count);
        Assert.Contains("first.example.com", values[0], StringComparison.Ordinal);
        Assert.Contains("second.example.com", values[1], StringComparison.Ordinal);
    }

    [Fact]
    public void WwwAuthenticate_по_запятым_не_разрезается()
    {
        // Это главная причина, по которой список разрешённых заголовков белый,
        // а не чёрный: здесь запятая разделяет параметры одного значения.
        var headers = new SipHeaders();
        headers.Append("WWW-Authenticate", "Digest realm=\"asterisk\", nonce=\"1234abcd\", algorithm=MD5");

        IReadOnlyList<string> values = headers.Values("WWW-Authenticate");
        string only = Assert.Single(values);
        Assert.Contains("realm=", only, StringComparison.Ordinal);
        Assert.Contains("nonce=", only, StringComparison.Ordinal);
    }

    [Fact]
    public void Запятая_внутри_кавычек_и_угловых_скобок_не_разделяет_значения()
    {
        var headers = new SipHeaders();
        headers.Append("Contact", "\"Петров, Иван\" <sip:100@host>;expires=300");
        Assert.Single(headers.Values("Contact"));

        var routes = new SipHeaders();
        routes.Append("Route", "<sip:a@proxy1;lr>, <sip:b@proxy2;lr>");
        Assert.Equal(2, routes.Values("Route").Count);
    }

    [Fact]
    public void Set_заменяет_все_одноимённые_и_идемпотентен()
    {
        var headers = new SipHeaders();
        headers.Append("Via", "SIP/2.0/UDP a");
        headers.Append("Via", "SIP/2.0/UDP b");
        headers.Append("From", "<sip:100@host>");

        headers.Set("Via", "SIP/2.0/TLS c");
        Assert.Equal(["SIP/2.0/TLS c"], headers.Values("Via"));
        Assert.Equal(2, headers.Fields.Count);

        headers.Set("Via", "SIP/2.0/TLS c");
        Assert.Equal(2, headers.Fields.Count);
    }

    [Fact]
    public void Порядок_одноимённых_сохраняется_а_Prepend_кладёт_наверх()
    {
        var headers = new SipHeaders();
        headers.Append("Via", "SIP/2.0/UDP inner");
        headers.Prepend("Via", "SIP/2.0/UDP outer");

        // Свой Via всегда сверху стека — иначе ответ уйдёт не туда.
        Assert.Equal(["SIP/2.0/UDP outer", "SIP/2.0/UDP inner"], headers.Values("Via"));
    }

    [Fact]
    public void Remove_убирает_все_вхождения()
    {
        var headers = new SipHeaders();
        headers.Append("Via", "a");
        headers.Append("v", "b");
        headers.Remove("VIA");
        Assert.Empty(headers.Values("Via"));
    }

    [Fact]
    public void Сериализация_даёт_канонические_имена_и_CRLF()
    {
        var headers = new SipHeaders();
        headers.Append("i", "abc");
        headers.Append("l", "0");
        Assert.Equal("Call-ID: abc\r\nContent-Length: 0\r\n", headers.Encoded);
    }
}
