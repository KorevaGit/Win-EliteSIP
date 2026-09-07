namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Разбор и сериализация SIP-URI.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/SIPURITests.swift</c>.
/// </summary>
public sealed class SipUriTests
{
    [Fact]
    public void Разбирает_минимальный_URI_без_пользователя_и_порта()
    {
        SipUri uri = Require("sip:pbx.example.com");
        Assert.Equal(SipScheme.Sip, uri.Scheme);
        Assert.Null(uri.User);
        Assert.Equal("pbx.example.com", uri.Host);
        Assert.Null(uri.Port);
        Assert.Empty(uri.Parameters);
    }

    [Fact]
    public void Разбирает_пользователя_порт_и_параметры()
    {
        SipUri uri = Require("sip:100@192.168.1.1:5060;transport=tls;lr");
        Assert.Equal("100", uri.User);
        Assert.Equal("192.168.1.1", uri.Host);
        Assert.Equal((ushort?)5060, uri.Port);
        Assert.Equal("tls", uri.GetParameter("transport"));
        Assert.True(uri.HasParameter("lr"));
        Assert.Null(uri.GetParameter("lr"));
        Assert.Equal(SipTransport.Tls, uri.Transport);
    }

    [Fact]
    public void Понимает_схему_sips_и_регистр_схемы()
    {
        SipUri uri = Require("SIPS:200@pbx.example.com");
        Assert.Equal(SipScheme.Sips, uri.Scheme);
        Assert.Equal(5061, uri.ResolvedPort(SipTransport.Udp));
    }

    [Fact]
    public void Снимает_IPv6_литерал_в_скобках_не_путая_его_с_портом()
    {
        SipUri uri = Require("sip:100@[2001:db8::1]:5061");
        Assert.Equal("2001:db8::1", uri.Host);
        Assert.Equal((ushort?)5061, uri.Port);

        SipUri noPort = Require("sip:[2001:db8::1]");
        Assert.Equal("2001:db8::1", noPort.Host);
        Assert.Null(noPort.Port);
    }

    [Fact]
    public void Игнорирует_пароль_в_userinfo()
    {
        SipUri uri = Require("sip:100:secret@pbx.example.com");
        Assert.Equal("100", uri.User);
        Assert.DoesNotContain("secret", uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Отбрасывает_header_часть_но_не_ломается_на_ней()
    {
        Assert.Equal("pbx.example.com", Require("sip:100@pbx.example.com?X-Foo=bar").Host);
    }

    [Theory]
    [InlineData("sip:pbx.example.com")]
    [InlineData("sip:100@192.168.1.1:5060")]
    [InlineData("sip:100@192.168.1.1:5060;transport=tls;lr")]
    [InlineData("sips:200@pbx.example.com:5061")]
    [InlineData("sip:100@[2001:db8::1]:5061")]
    public void Round_trip_сохраняет_строку_и_порядок_параметров(string text)
    {
        SipUri uri = Require(text);
        Assert.Equal(text, uri.ToString());
        Assert.Equal(uri, Require(uri.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("100@pbx.example.com")]      // нет схемы
    [InlineData("http:pbx.example.com")]     // чужая схема
    [InlineData("sip:")]                     // нет хоста
    [InlineData("sip:@pbx.example.com")]     // пустой userinfo
    [InlineData("sip:100@")]                 // нет хоста после собаки
    [InlineData("sip:pbx.example.com:99999")] // порт вне ushort
    [InlineData("sip:pbx.example.com:abc")]  // порт не число
    [InlineData("sip:[2001:db8::1")]         // незакрытая скобка
    public void Отклоняет_мусор(string text) => Assert.Null(SipUri.Parse(text));

    [Fact]
    public void Параметры_доступны_без_учёта_регистра_имени()
    {
        SipUri uri = Require("sip:pbx.example.com;Transport=TLS");
        Assert.Equal("TLS", uri.GetParameter("transport"));
        Assert.Equal(SipTransport.Tls, uri.Transport);
    }

    [Fact]
    public void Изменение_параметра_добавляет_и_удаляет()
    {
        SipUri uri = Require("sip:pbx.example.com");
        uri.SetParameter("transport", "tls");
        Assert.Equal("sip:pbx.example.com;transport=tls", uri.ToString());
        uri.SetParameter("TRANSPORT", null);
        Assert.Equal("sip:pbx.example.com", uri.ToString());
    }

    [Fact]
    public void Порт_по_умолчанию_зависит_от_транспорта()
    {
        SipUri plain = Require("sip:pbx.example.com");
        Assert.Equal(5060, plain.ResolvedPort(SipTransport.Udp));
        Assert.Equal(5061, plain.ResolvedPort(SipTransport.Tls));

        Assert.Equal(5061, Require("sip:pbx.example.com;transport=tls").ResolvedPort(SipTransport.Udp));

        // Явный порт важнее транспорта.
        Assert.Equal(5080, Require("sip:pbx.example.com:5080;transport=tls").ResolvedPort(SipTransport.Udp));
    }

    private static SipUri Require(string text)
    {
        SipUri? uri = SipUri.Parse(text);
        Assert.NotNull(uri);
        return uri;
    }
}
