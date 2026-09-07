using System.Text;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Приём входящего звонка целиком: от INVITE до ACK и BYE.
///
/// Проверяется именно то, что на живой АТС стоит дороже всего: коды и порядок
/// ответов, ретрансмиссия 200 OK до подтверждения, отмена вызова до ответа и
/// вторая линия. Каждый из этих случаев на Asterisk выглядит как «звонок повис»,
/// и различить их без разбора трафика нельзя.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/IncomingCallTests.swift</c>.
/// </summary>
public sealed class IncomingCallTests
{
    private const string OfferSdp =
        "v=0\r\no=root 1 1 IN IP4 172.17.0.2\r\ns=Asterisk\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
        + "m=audio 14002 RTP/AVP 0 101\r\na=rtpmap:0 PCMU/8000\r\n"
        + "a=rtpmap:101 telephone-event/8000\r\na=sendrecv\r\n\r\n";

    private const string AnswerSdp =
        "v=0\r\no=elitesip 1 1 IN IP4 192.168.1.50\r\ns=-\r\nc=IN IP4 192.168.1.50\r\nt=0 0\r\n"
        + "m=audio 16000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n\r\n";

    /// <summary>INVITE в том виде, в каком его шлёт chan_sip с плеча очереди раздачи.</summary>
    private static SipRequest MakeInvite(
        string callId = "incoming-1",
        string from = "\"AutoDialer\" <sip:2929@172.17.0.2>;tag=as77aabb",
        string branch = "z9hG4bKinbound1",
        int sequence = 102,
        string? contact = "<sip:2929@172.17.0.2:5060>",
        string body = OfferSdp)
    {
        var request = new SipRequest(
            SipMethod.Invite,
            new SipUri("192.168.1.50", user: "100"),
            body: Encoding.UTF8.GetBytes(body));

        request.Headers.Append(SipHeaderName.Via, $"SIP/2.0/UDP 172.17.0.2:5060;branch={branch};rport");
        request.Headers.Append(SipHeaderName.MaxForwards, "70");
        request.Headers.Append(SipHeaderName.From, from);
        request.Headers.Append(SipHeaderName.To, "<sip:100@192.168.1.50>");
        request.Headers.Append(SipHeaderName.CallId, callId);
        request.Headers.Append(SipHeaderName.CSeq, $"{sequence} INVITE");
        if (contact is not null)
        {
            request.Headers.Append(SipHeaderName.Contact, contact);
        }
        request.Headers.Append(SipHeaderName.ContentType, "application/sdp");
        return request;
    }

    /// <summary>ACK на 2xx: свой branch, тот же Call-ID и номер CSeq.</summary>
    private static SipRequest MakeAck(SipRequest invite, string toTag)
    {
        var ack = new SipRequest(SipMethod.Ack, new SipUri("192.168.1.50", user: "100"));
        ack.Headers.Append(SipHeaderName.Via, "SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bKack-own");
        ack.Headers.Append(SipHeaderName.From, invite.Headers.First(SipHeaderName.From) ?? string.Empty);
        ack.Headers.Append(SipHeaderName.To, $"<sip:100@192.168.1.50>;tag={toTag}");
        ack.Headers.Append(SipHeaderName.CallId, invite.CallId ?? string.Empty);
        ack.Headers.Append(SipHeaderName.CSeq, $"{invite.CSeq?.Number ?? 1} ACK");
        return ack;
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

    /// <summary>Ждёт события входящего звонка от агента.</summary>
    private static Task<SipIncomingCall?> AwaitIncomingCallAsync(SipUserAgent agent) =>
        Task.Run(async () =>
        {
            await foreach (SipUserAgentEvent value in agent.Events)
            {
                if (value is SipUserAgentEvent.IncomingCall incoming)
                {
                    return incoming.Call;
                }
            }
            return null;
        });

    /// <summary>Ждёт причины завершения звонка.</summary>
    private static Task<string?> AwaitEndAsync(SipIncomingCall call) =>
        Task.Run(async () =>
        {
            await foreach (SipCallEvent value in call.Events)
            {
                if (value is SipCallEvent.Ended ended)
                {
                    return ended.Reason;
                }
            }
            return null;
        });

    private static IReadOnlyList<SipResponse> InviteResponses(ScriptedSipServer server) =>
        [.. server.SentResponses.Where(response => response.CSeq?.Method == SipMethod.Invite)];

    // Ответы на INVITE

    [Fact]
    public async Task На_INVITE_отвечаем_100_и_180_до_всякого_решения_оператора()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        SipRequest invite = MakeInvite();
        server.Inject(invite);

        SipIncomingCall? call = await pending;
        Assert.NotNull(call);
        Assert.Equal("2929", call.CallerNumber);
        Assert.Equal("AutoDialer", call.CallerName);
        Assert.Equal("100", call.CalledNumber);
        Assert.True(call.Offer.Span.SequenceEqual(invite.Body.Span));

        await agent.StopAsync();

        IReadOnlyList<SipResponse> responses = InviteResponses(server);
        Assert.Equal([100, 180], responses.Take(2).Select(response => response.StatusCode));

        // 180 обязан нести наш тег: по нему звонящий соберёт диалог, когда придёт
        // 200. Без тега Asterisk 200 OK не свяжет с этим вызовом.
        SipResponse ringing = responses.First(response => response.StatusCode == 180);
        Assert.NotNull(ringing.To?.Tag);
        Assert.Equal("incoming-1", ringing.CallId);
        Assert.NotEmpty(ringing.Contacts);
    }

    [Fact]
    public async Task Ответ_на_звонок_уходит_200_OK_с_нашим_SDP()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        server.Inject(MakeInvite());
        Assert.NotNull(await pending);

        Assert.True(await agent.AnswerIncomingCallAsync(answer: Encoding.UTF8.GetBytes(AnswerSdp)));

        SipResponse ok = server.SentResponses.Last(
            response => response.StatusCode == 200 && response.CSeq?.Method == SipMethod.Invite);

        Assert.Equal(AnswerSdp, Encoding.UTF8.GetString(ok.Body.Span));
        Assert.Equal("application/sdp", ok.ContentType);
        Assert.NotEmpty(ok.Contacts);

        // Тег в 200 обязан совпасть с тем, что ушёл в 180: иначе это два разных
        // диалога, и ACK не найдёт ни один из них.
        SipResponse ringing = server.SentResponses.First(response => response.StatusCode == 180);
        Assert.Equal(ringing.To?.Tag, ok.To?.Tag);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Отклонение_отвечает_486_вызов_вернётся_в_очередь()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        server.Inject(MakeInvite());
        SipIncomingCall? call = await pending;
        Assert.NotNull(call);

        Task<string?> ended = AwaitEndAsync(call);

        await agent.RejectIncomingCallAsync();
        Assert.Equal("отклонён", await ended);

        Assert.Equal(486, InviteResponses(server)[^1].StatusCode);

        // Линия должна освободиться: следующий INVITE обязан снова зазвонить, а
        // не получить «занято» от призрака прошлого вызова.
        Assert.Null(agent.CallState);
        await agent.StopAsync();
    }

    [Fact]
    public async Task Вторая_линия_получает_486_а_не_молчание()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        server.Inject(MakeInvite());
        Assert.NotNull(await pending);

        server.Inject(MakeInvite("incoming-2", branch: "z9hG4bKinbound2"));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.SentResponses.Any(response => response.CallId == "incoming-2")));
        await agent.StopAsync();

        SipResponse second = server.SentResponses.Last(response => response.CallId == "incoming-2");
        Assert.Equal(486, second.StatusCode);
    }

    [Fact]
    public async Task INVITE_без_Contact_отклоняется()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);

        server.Inject(MakeInvite(contact: null));
        Assert.True(await TestSupport.WaitUntilAsync(() => InviteResponses(server).Count > 0));
        await agent.StopAsync();

        Assert.Equal(400, InviteResponses(server)[0].StatusCode);
    }

    // Подтверждение

    [Fact]
    public async Task Ответ_200_повторяется_пока_не_придёт_ACK()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        SipRequest invite = MakeInvite();
        server.Inject(invite);
        Assert.NotNull(await pending);
        await agent.AnswerIncomingCallAsync(answer: Encoding.UTF8.GetBytes(AnswerSdp));

        int OkCount() => server.SentResponses.Count(
            response => response.StatusCode == 200 && response.CSeq?.Method == SipMethod.Invite);

        // На UDP потерянный 200 OK означает разговор, о котором знаем только мы:
        // INVITE после 2xx больше не повторяется, и второго шанса не будет.
        Assert.True(await TestSupport.WaitUntilAsync(() => OkCount() >= 2));

        string? toTag = server.SentResponses.Last(response => response.StatusCode == 200).To?.Tag;
        Assert.NotNull(toTag);
        server.Inject(MakeAck(invite, toTag));

        // После ACK повторы обязаны прекратиться.
        await Task.Delay(400);
        int settled = OkCount();
        await Task.Delay(400);
        Assert.Equal(settled, OkCount());

        await agent.StopAsync();
    }

    [Fact]
    public async Task ACK_не_повторяет_ответ_обратно()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        SipRequest invite = MakeInvite();
        server.Inject(invite);
        Assert.NotNull(await pending);
        await agent.AnswerIncomingCallAsync(answer: Encoding.UTF8.GetBytes(AnswerSdp));

        string? toTag = server.SentResponses.Last(response => response.StatusCode == 200).To?.Tag;
        Assert.NotNull(toTag);

        server.Inject(MakeAck(invite, toTag));
        await Task.Delay(200);
        int afterAck = server.SentResponses.Count;

        server.Inject(MakeAck(invite, toTag));
        await Task.Delay(200);
        Assert.Equal(afterAck, server.SentResponses.Count);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Ретрансмиссия_INVITE_получает_тот_же_180()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        SipRequest invite = MakeInvite();
        server.Inject(invite);
        Assert.NotNull(await pending);

        int before = server.SentResponses.Count;
        server.Inject(invite);
        Assert.True(await TestSupport.WaitUntilAsync(() => server.SentResponses.Count > before));
        await agent.StopAsync();

        List<SipResponse> ringing = [.. server.SentResponses.Where(response => response.StatusCode == 180)];
        Assert.True(ringing.Count >= 2);
        Assert.Single(ringing.Select(response => response.To?.Tag).Distinct(StringComparer.Ordinal));
    }

    // Отмена

    [Fact]
    public async Task CANCEL_до_ответа_даёт_200_на_CANCEL_и_487_на_INVITE()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        SipRequest invite = MakeInvite();
        server.Inject(invite);
        SipIncomingCall? call = await pending;
        Assert.NotNull(call);

        Task<string?> ended = AwaitEndAsync(call);

        var cancel = new SipRequest(SipMethod.Cancel, new SipUri("192.168.1.50", user: "100"));
        cancel.Headers.Append(SipHeaderName.Via, "SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bKinbound1");
        cancel.Headers.Append(SipHeaderName.From, invite.Headers.First(SipHeaderName.From) ?? string.Empty);
        cancel.Headers.Append(SipHeaderName.To, "<sip:100@192.168.1.50>");
        cancel.Headers.Append(SipHeaderName.CallId, "incoming-1");
        cancel.Headers.Append(SipHeaderName.CSeq, "102 CANCEL");
        server.Inject(cancel);

        Assert.Equal("отменён вызывающим", await ended);
        await agent.StopAsync();

        SipResponse cancelResponse = server.SentResponses.First(
            response => response.CSeq?.Method == SipMethod.Cancel);
        Assert.Equal(200, cancelResponse.StatusCode);

        // 487 обязателен: без него Asterisk считает вызов живым и держит канал.
        Assert.Equal(487, InviteResponses(server)[^1].StatusCode);
    }

    // Завершение

    [Fact]
    public async Task BYE_от_собеседника_завершает_принятый_звонок()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        SipRequest invite = MakeInvite();
        server.Inject(invite);
        SipIncomingCall? call = await pending;
        Assert.NotNull(call);
        await agent.AnswerIncomingCallAsync(answer: Encoding.UTF8.GetBytes(AnswerSdp));

        string? toTag = server.SentResponses.Last(response => response.StatusCode == 200).To?.Tag;
        Assert.NotNull(toTag);
        server.Inject(MakeAck(invite, toTag));

        Task<string?> ended = AwaitEndAsync(call);

        var bye = new SipRequest(SipMethod.Bye, new SipUri("192.168.1.50", user: "100"));
        bye.Headers.Append(SipHeaderName.Via, "SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bKbye1");
        bye.Headers.Append(SipHeaderName.From, invite.Headers.First(SipHeaderName.From) ?? string.Empty);
        bye.Headers.Append(SipHeaderName.To, $"<sip:100@192.168.1.50>;tag={toTag}");
        bye.Headers.Append(SipHeaderName.CallId, "incoming-1");
        bye.Headers.Append(SipHeaderName.CSeq, "103 BYE");
        server.Inject(bye);

        Assert.Equal("собеседник завершил звонок", await ended);
        await agent.StopAsync();

        SipResponse response = server.SentResponses.Last(value => value.CSeq?.Method == SipMethod.Bye);
        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Свой_отбой_на_принятом_звонке_уходит_как_BYE_на_Contact_звонящего()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        SipRequest invite = MakeInvite();
        server.Inject(invite);
        Assert.NotNull(await pending);
        await agent.AnswerIncomingCallAsync(answer: Encoding.UTF8.GetBytes(AnswerSdp));

        string? toTag = server.SentResponses.Last(response => response.StatusCode == 200).To?.Tag;
        Assert.NotNull(toTag);
        server.Inject(MakeAck(invite, toTag));
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Answered));

        await agent.HangUpAsync();
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Bye)));
        await agent.StopAsync();

        SipRequest sentBye = server.ReceivedRequests.Last(request => request.Method == SipMethod.Bye);

        // BYE идёт на Contact собеседника, а не на адрес из From: за NAT это
        // разные адреса, и второй никуда не ведёт.
        Assert.Contains("2929@172.17.0.2:5060", sentBye.Uri.ToString(), StringComparison.Ordinal);
        Assert.Equal("incoming-1", sentBye.CallId);

        // Свой тег в From, чужой в To — диалог со стороны отвечавшего зеркален.
        Assert.Equal(toTag, sentBye.From?.Tag);
        Assert.Equal("as77aabb", sentBye.To?.Tag);
    }

    [Fact]
    public async Task Отбой_до_ответа_отвечает_486_а_не_шлёт_CANCEL_чужому_запросу()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        Task<SipIncomingCall?> pending = AwaitIncomingCallAsync(agent);

        server.Inject(MakeInvite());
        Assert.NotNull(await pending);

        await agent.HangUpAsync();
        await agent.StopAsync();

        Assert.DoesNotContain(server.ReceivedRequests, request => request.Method == SipMethod.Cancel);
        Assert.Equal(486, InviteResponses(server)[^1].StatusCode);
    }
}
