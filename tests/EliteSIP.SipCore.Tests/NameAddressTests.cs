namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Значение From, To, Contact и Refer-To.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/NameAddressTests.swift</c>.
/// </summary>
public sealed class NameAddressTests
{
    [Fact]
    public void Разбирает_имя_в_кавычках_и_URI_в_скобках()
    {
        NameAddress value = Require("\"Agent 100\" <sip:100@pbx.example.com>;tag=abc123");
        Assert.Equal("Agent 100", value.DisplayName);
        Assert.Equal("100", value.Uri.User);
        Assert.Equal("pbx.example.com", value.Uri.Host);
        Assert.Equal("abc123", value.Tag);
    }

    [Fact]
    public void Разбирает_имя_без_кавычек()
    {
        NameAddress value = Require("Agent <sip:100@host>");
        Assert.Equal("Agent", value.DisplayName);
        Assert.Null(value.Tag);
    }

    [Fact]
    public void Без_угловых_скобок_параметры_принадлежат_заголовку_а_не_URI()
    {
        // RFC 3261 §20.10: это ключевая неоднозначность SIP. Здесь tag — это
        // параметр заголовка To, а не параметр URI.
        NameAddress value = Require("sip:100@host;tag=xyz");
        Assert.Equal("host", value.Uri.Host);
        Assert.Empty(value.Uri.Parameters);
        Assert.Equal("xyz", value.Tag);
    }

    [Fact]
    public void В_скобках_параметры_URI_и_заголовка_различаются()
    {
        NameAddress value = Require("<sip:100@host;transport=tls>;tag=xyz");
        Assert.Equal("tls", value.Uri.GetParameter("transport"));
        Assert.Equal("xyz", value.Tag);
        Assert.Equal(SipTransport.Tls, value.Uri.Transport);
    }

    [Fact]
    public void Запятая_и_точка_с_запятой_внутри_кавычек_не_ломают_разбор()
    {
        NameAddress value = Require("\"Петров, Иван; отдел 5\" <sip:100@host>;tag=q");
        Assert.Equal("Петров, Иван; отдел 5", value.DisplayName);
        Assert.Equal("q", value.Tag);
    }

    [Fact]
    public void Expires_у_Contact_читается()
    {
        // Asterisk может вернуть срок регистрации именно здесь, а не в Expires.
        Assert.Equal(120, Require("<sip:100@10.0.0.5:5060>;expires=120").Expires);
    }

    [Fact]
    public void Сериализация_всегда_ставит_URI_в_угловые_скобки()
    {
        var value = new NameAddress(new SipUri("host", user: "100"));
        Assert.Equal("<sip:100@host>", value.ToString());

        value.Tag = "t1";
        Assert.Equal("<sip:100@host>;tag=t1", value.ToString());

        value.DisplayName = "Agent 100";
        Assert.Equal("\"Agent 100\" <sip:100@host>;tag=t1", value.ToString());
    }

    [Theory]
    [InlineData("<sip:100@host>")]
    [InlineData("<sip:100@host>;tag=abc")]
    [InlineData("\"Agent\" <sip:100@host:5061;transport=tls>;tag=abc")]
    public void Round_trip_сохраняет_смысл(string text)
    {
        NameAddress parsed = Require(text);
        Assert.Equal(parsed, Require(parsed.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>")]
    [InlineData("<not-a-uri>")]
    [InlineData("Agent <sip:>")]
    public void Мусор_отвергается(string text) => Assert.Null(NameAddress.Parse(text));

    private static NameAddress Require(string text)
    {
        NameAddress? value = NameAddress.Parse(text);
        Assert.NotNull(value);
        return value;
    }
}
