namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Входящие запросы, не создающие звонок.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/InboundRequestTests.swift</c>.
/// Входящий INVITE со всеми его случаями разобран отдельно, в
/// <see cref="IncomingCallTests"/>.
/// </summary>
public sealed class InboundRequestTests
{
    /// <summary>OPTIONS в том виде, в каком его шлёт chan_sip при qualify=yes.</summary>
    private static SipRequest MakeOptions(string callId = "qualify-1", int sequence = 102)
    {
        var request = new SipRequest(SipMethod.Options, new SipUri("192.168.1.50", user: "100"));
        request.Headers.Append(SipHeaderName.Via, $"SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bK{callId};rport");
        request.Headers.Append(SipHeaderName.MaxForwards, "70");
        request.Headers.Append(SipHeaderName.From, "\"asterisk\" <sip:asterisk@172.17.0.2>;tag=as3f4d5e");
        request.Headers.Append(SipHeaderName.To, "<sip:100@192.168.1.50>");
        request.Headers.Append(SipHeaderName.CallId, callId);
        request.Headers.Append(SipHeaderName.CSeq, $"{sequence} OPTIONS");
        return request;
    }

    private static ScriptedSipServer AcceptingServer() =>
        new((request, index) => index == 0
            ? ScriptedSipServer.Unauthorized(request)
            : ScriptedSipServer.RegistrationAccepted(request));

    private static async Task<SipUserAgent> RegisteredAgentAsync(ScriptedSipServer server)
    {
        var agent = new SipUserAgent(
            TestSupport.TestAccount(server.Transport),
            TestSupport.TestCredentials,
            server,
            TestSupport.FastTimers());

        await agent.StartAsync();
        await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered);
        return agent;
    }

    [Fact]
    public async Task На_OPTIONS_отвечаем_200_иначе_пир_уходит_в_UNREACHABLE()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);

        SipRequest options = MakeOptions();
        server.Inject(options);

        Assert.True(await TestSupport.WaitUntilAsync(() => server.SentResponses.Count > 0));
        await agent.StopAsync();

        SipResponse response = server.SentResponses[0];
        Assert.Equal(200, response.StatusCode);

        // Ответ обязан вернуть Via, Call-ID и CSeq запроса: по ним Asterisk
        // сопоставляет его со своей транзакцией.
        Assert.Equal(options.TopVia?.Branch, response.TopVia?.Branch);
        Assert.Equal(options.CallId, response.CallId);
        Assert.Equal(options.CSeq?.Number, response.CSeq?.Number);
        Assert.Equal(SipMethod.Options, response.CSeq?.Method);
        Assert.Equal("as3f4d5e", response.From?.Tag);
        Assert.NotNull(response.To?.Tag);
        Assert.Contains("INVITE", response.Headers.First("Allow") ?? string.Empty, StringComparison.Ordinal);
        Assert.NotEmpty(response.Contacts);
    }

    [Fact]
    public async Task Ретрансмиссия_OPTIONS_получает_тот_же_ответ()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);

        SipRequest options = MakeOptions();
        server.Inject(options);
        Assert.True(await TestSupport.WaitUntilAsync(() => server.SentResponses.Count == 1));

        server.Inject(options);
        Assert.True(await TestSupport.WaitUntilAsync(() => server.SentResponses.Count == 2));
        await agent.StopAsync();

        IReadOnlyList<SipResponse> responses = server.SentResponses;
        Assert.Equal(2, responses.Count);
        Assert.Equal(responses[0].To?.Tag, responses[1].To?.Tag);
        Assert.Equal(responses[0].CSeq?.Number, responses[1].CSeq?.Number);
    }

    [Fact]
    public async Task Входящий_REFER_отклоняется_явно()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);

        // Исходящий REFER поддерживается, но принимать удалённую команду перевода —
        // отдельная политика. Отвечаем 501, а не молчим.
        var request = new SipRequest(SipMethod.Refer, new SipUri("192.168.1.50", user: "100"));
        request.Headers.Append(SipHeaderName.Via, "SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bKrefer1");
        request.Headers.Append(SipHeaderName.From, "<sip:asterisk@172.17.0.2>;tag=as1");
        request.Headers.Append(SipHeaderName.To, "<sip:100@192.168.1.50>");
        request.Headers.Append(SipHeaderName.CallId, "refer-1");
        request.Headers.Append(SipHeaderName.CSeq, "1 REFER");
        server.Inject(request);

        Assert.True(await TestSupport.WaitUntilAsync(() => server.SentResponses.Count > 0));
        await agent.StopAsync();

        SipResponse response = server.SentResponses[0];
        Assert.Equal(501, response.StatusCode);
        Assert.NotNull(response.Headers.First("Allow"));
    }

    [Fact]
    public async Task Непонятое_входящее_оставляет_след_а_не_исчезает()
    {
        // Раньше здесь стоял голый return: непонятое сообщение отбрасывалось
        // молча. Цена такого молчания — жалоба «разговор висит после того, как
        // собеседник положил трубку», у которой в журнале нет ни строки о том,
        // что вообще что-то приходило.
        var server = new ScriptedSipServer((_, _) => null);
        using var layer = new SipTransactionLayer(server, TestSupport.FastTimers());

        List<(SipLogLevel Level, string Text)> discarded = [];
        layer.OnDiscarded = (level, text) =>
        {
            lock (discarded)
            {
                discarded.Add((level, text));
            }
        };

        await layer.StartAsync();
        server.Inject("BROKEN sip:100@here SIP/9.9\r\n\r\n"u8.ToArray());

        Assert.True(await TestSupport.WaitUntilAsync(() =>
        {
            lock (discarded)
            {
                return discarded.Count > 0;
            }
        }));

        await layer.StopAsync();

        (SipLogLevel level, string text) = discarded[0];
        Assert.Equal(SipLogLevel.Warning, level);
        Assert.Contains("не разобрано", text, StringComparison.Ordinal);

        // В строку попадает только стартовая строка: дальше в сообщении идут
        // заголовки, среди которых бывает Authorization, а паролей в журнале не
        // бывает.
        Assert.Contains("BROKEN sip:100@here SIP/9.9", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Выжимка_из_мусора_не_тащит_в_журнал_ни_длину_ни_непечатное()
    {
        // На открытый UDP-порт прилетает что угодно, вплоть до мегабайта
        // двоичного мусора. Обрезка и замена непечатного — не косметика: журнал
        // читают глазами, и одна такая строка способна его испортить целиком.
        byte[] noise = new byte[4096];
        Array.Fill(noise, (byte)0x00);
        noise[0] = (byte)'X';

        string described = SipTransactionLayer.Describe(noise);

        Assert.Contains("4096 байт", described, StringComparison.Ordinal);
        Assert.DoesNotContain(described, character => character == '\0');
        Assert.True(described.Length < 200, $"строка длиной {described.Length} — это уже не выжимка");
    }
}
