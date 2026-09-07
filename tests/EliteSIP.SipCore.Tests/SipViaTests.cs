namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Заголовок Via.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/SIPViaTests.swift</c>.
/// </summary>
public sealed class SipViaTests
{
    [Fact]
    public void Разбирает_транспорт_хост_порт_и_branch()
    {
        SipVia via = Require("SIP/2.0/UDP 10.0.0.5:5060;branch=z9hG4bKabc123;rport");
        Assert.Equal(SipTransport.Udp, via.Transport);
        Assert.Equal("10.0.0.5", via.Host);
        Assert.Equal((ushort?)5060, via.Port);
        Assert.Equal("z9hG4bKabc123", via.Branch);
        Assert.True(via.HasParameter("rport"));
        Assert.Null(via.Rport);
    }

    [Fact]
    public void Received_и_rport_из_ответа_дают_внешний_адрес()
    {
        // Это единственный способ узнать, каким нас видно из-за NAT. Без этого
        // Contact в регистрации указывает на локальный адрес, и входящие не идут.
        SipVia via = Require("SIP/2.0/UDP 192.168.1.50:5060;branch=z9hG4bK1;received=203.0.113.7;rport=41234");
        Assert.Equal("203.0.113.7", via.Received);
        Assert.Equal((ushort?)41234, via.Rport);

        (string Host, ushort? Port)? observed = via.ObservedAddress;
        Assert.NotNull(observed);
        Assert.Equal("203.0.113.7", observed.Value.Host);
        Assert.Equal((ushort?)41234, observed.Value.Port);
    }

    [Fact]
    public void Без_received_внешний_адрес_неизвестен() =>
        Assert.Null(Require("SIP/2.0/TLS 10.0.0.5:5061;branch=z9hG4bK1").ObservedAddress);

    [Fact]
    public void RequestRport_добавляет_флаг_один_раз()
    {
        SipVia via = Require("SIP/2.0/UDP 10.0.0.5:5060;branch=z9hG4bK1");
        via.RequestRport();
        via.RequestRport();
        Assert.Single(via.Parameters, parameter => string.Equals(parameter.Name, "rport", StringComparison.Ordinal));
        Assert.EndsWith(";rport", via.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IPv6_в_скобках_не_путается_с_портом()
    {
        SipVia via = Require("SIP/2.0/TLS [2001:db8::1]:5061;branch=z9hG4bK1");
        Assert.Equal("2001:db8::1", via.Host);
        Assert.Equal((ushort?)5061, via.Port);
        Assert.Contains("[2001:db8::1]:5061", via.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Хост_без_порта_разбирается()
    {
        SipVia via = Require("SIP/2.0/TLS pbx.example.com;branch=z9hG4bK1");
        Assert.Equal("pbx.example.com", via.Host);
        Assert.Null(via.Port);
    }

    [Theory]
    [InlineData("SIP/2.0/UDP 10.0.0.5:5060;branch=z9hG4bK1")]
    [InlineData("SIP/2.0/TLS pbx.example.com;branch=z9hG4bK2;received=1.2.3.4;rport=5060")]
    [InlineData("SIP/2.0/TCP [2001:db8::1]:5060;branch=z9hG4bK3")]
    public void Round_trip(string text) => Assert.Equal(text, Require(text).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("SIP/2.0/UDP")]           // нет sent-by
    [InlineData("HTTP/1.1/UDP host")]     // не SIP
    [InlineData("SIP/1.0/UDP host")]      // не та версия
    [InlineData("SIP/2.0/SCTP host")]     // неподдерживаемый транспорт
    [InlineData("SIP/2.0/UDP host:99999")] // порт вне диапазона
    public void Мусор_отвергается(string text) => Assert.Null(SipVia.Parse(text));

    private static SipVia Require(string text)
    {
        SipVia? via = SipVia.Parse(text);
        Assert.NotNull(via);
        return via;
    }
}
