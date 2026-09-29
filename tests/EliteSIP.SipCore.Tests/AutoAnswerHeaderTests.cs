namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Просьба об автоответе в заголовках INVITE — для режима автоподъёма
/// «по заголовку» (0.1.61).
/// </summary>
public sealed class AutoAnswerHeaderTests
{
    private static SipRequest Invite(params (string Name, string Value)[] headers)
    {
        var request = new SipRequest(SipMethod.Invite, new SipUri("192.168.1.50", user: "100"));
        foreach (var (name, value) in headers)
        {
            request.Headers.Append(name, value);
        }

        return request;
    }

    [Theory]
    [InlineData("X-Autoanswer", "TRUE")]
    [InlineData("X-Autoanswer", "yes")]
    [InlineData("Call-Info", "<sip:172.17.0.2>;answer-after=0")]
    [InlineData("Call-Info", "<uri>; Answer-After=3")]
    [InlineData("Alert-Info", "info=alert-autoanswer")]
    [InlineData("Alert-Info", "Ring Answer")]
    [InlineData("Alert-Info", "<http://127.0.0.1>;info=Auto Answer")]
    [InlineData("Answer-Mode", "Auto")]
    [InlineData("Priv-Answer-Mode", "Auto;require")]
    public void Просьба_опознаётся(string name, string value)
        => Assert.True(SipUserAgent.AsksForAutoAnswer(Invite((name, value))));

    [Theory]
    [InlineData("X-Autoanswer", "FALSE")]
    [InlineData("Call-Info", "<http://example.com/photo.png>;purpose=icon")]
    [InlineData("Alert-Info", "<http://127.0.0.1/ring.wav>")]
    [InlineData("Answer-Mode", "Manual")]
    public void Прочее_не_просьба(string name, string value)
        => Assert.False(SipUserAgent.AsksForAutoAnswer(Invite((name, value))));

    [Fact]
    public void Без_заголовков_не_просьба()
        => Assert.False(SipUserAgent.AsksForAutoAnswer(Invite()));
}
