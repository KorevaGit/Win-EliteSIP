using System.Text;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Исходящий звонок: ответ, отбой, отказы, пределы и линии.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/CallTests.swift</c>.
/// </summary>
public sealed class CallTests
{
    /// <summary>
    /// Сервер, который ведёт себя как настоящий: требует авторизацию у любого
    /// запроса без заголовка Authorization и принимает его с ним. Так проверка не
    /// зависит от порядка запросов и не ломается от лишней регистрации.
    /// </summary>
    private static ScriptedSipServer MakeServer() =>
        new((request, _) =>
        {
            if (request.Headers.First("Authorization") is null)
            {
                return ScriptedSipServer.Unauthorized(request);
            }

            return request.Method switch
            {
                SipMethod.Register => ScriptedSipServer.RegistrationAccepted(request),

                // Ответ на INVITE присылаем вручную: их несколько.
                SipMethod.Invite => null,

                _ => ScriptedSipServer.Response(request, 200),
            };
        });

    private static async Task<SipUserAgent> MakeAgentAsync(ScriptedSipServer server, TimeSpan? ringingLimit = null)
    {
        var agent = new SipUserAgent(
            TestSupport.TestAccount(),
            TestSupport.TestCredentials,
            server,
            TestSupport.FastTimers(),
            ringingLimit: ringingLimit);

        await agent.StartAsync();
        await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered);
        return agent;
    }

    private static ReadOnlyMemory<byte> SdpOffer() => Encoding.UTF8.GetBytes(
        "v=0\r\no=- 1 1 IN IP4 10.0.0.5\r\ns=EliteSIP\r\nc=IN IP4 10.0.0.5\r\nt=0 0\r\nm=audio 16384 RTP/AVP 0\r\n");

    private static SipResponse Answer(SipRequest invite, int status = 200)
    {
        SipResponse response = ScriptedSipServer.Response(
            invite,
            status,
            extraHeaders:
            [
                (SipHeaderName.Contact, "<sip:600@172.17.0.2:5060>"),
                (SipHeaderName.ContentType, "application/sdp"),
            ]);

        response.Body = Encoding.UTF8.GetBytes(
            "v=0\r\no=root 1 1 IN IP4 172.17.0.2\r\ns=Asterisk\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
                + "m=audio 14028 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n");

        return response;
    }

    private static SipRequest LastInvite(ScriptedSipServer server) =>
        server.ReceivedRequests.Last(request => request.Method == SipMethod.Invite);

    private static Task<bool> WaitForSignedInviteAsync(ScriptedSipServer server) =>
        TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Count(request => request.Method == SipMethod.Invite) >= 2);

    [Fact]
    public async Task Звонок_доходит_до_ответа_и_подтверждается_ACK()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        SipOutgoingCall call = agent.PlaceCall("600", SdpOffer());
        Task<ReadOnlyMemory<byte>?> collector = Task.Run(async () =>
        {
            ReadOnlyMemory<byte>? body = null;
            await foreach (SipCallEvent value in call.Events)
            {
                if (value is SipCallEvent.Answered answered)
                {
                    body = answered.Body;
                }
            }
            return body;
        });

        // chan_sip требует авторизацию и на INVITE, поэтому первый уходит без
        // неё, а второй — уже подписанный.
        Assert.True(await WaitForSignedInviteAsync(server));

        SipRequest invite = LastInvite(server);
        Assert.NotNull(invite.Headers.First("Authorization"));
        Assert.Equal("application/sdp", invite.ContentType);
        Assert.False(invite.Body.IsEmpty);

        server.Inject(ScriptedSipServer.Response(invite, 100));
        server.Inject(ScriptedSipServer.Response(invite, 180));
        server.Inject(Answer(invite));

        Assert.True(await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Answered));

        // ACK на 2xx уходит на Contact из ответа и вне транзакции.
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Ack)));

        SipRequest ack = server.ReceivedRequests.Last(request => request.Method == SipMethod.Ack);
        Assert.Equal("172.17.0.2", ack.Uri.Host);
        Assert.Equal(invite.CSeq?.Number, ack.CSeq?.Number);
        Assert.NotNull(ack.To?.Tag);

        await agent.HangUpAsync();
        ReadOnlyMemory<byte>? answerBody = await collector;
        Assert.True(answerBody is { IsEmpty: false }, "тело ответа должно дойти до вызывающего");

        await agent.StopAsync();
    }

    [Fact]
    public async Task Занято_объясняется_человеческим_языком()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        SipOutgoingCall call = agent.PlaceCall("600", SdpOffer());
        Task<string?> collector = Task.Run(async () =>
        {
            await foreach (SipCallEvent value in call.Events)
            {
                if (value is SipCallEvent.Failed failed)
                {
                    return failed.Reason;
                }
            }
            return null;
        });

        Assert.True(await WaitForSignedInviteAsync(server));
        server.Inject(ScriptedSipServer.Response(LastInvite(server), 486));

        // «Сервер ответил 486» оператору не говорит ничего, «занято» — говорит всё.
        Assert.Equal(SipCallErrors.DescribeCallFailure(486, "Busy Here"), await collector);
        Assert.Null(agent.CallState);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Гудки_без_финального_ответа_снимаются_по_пределу()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server, TimeSpan.FromMilliseconds(400));

        SipOutgoingCall call = agent.PlaceCall("600", SdpOffer());
        Task<(int Status, string Reason)?> collector = Task.Run(async () =>
        {
            await foreach (SipCallEvent value in call.Events)
            {
                if (value is SipCallEvent.Failed failed)
                {
                    return ((int, string)?)(failed.Status, failed.Reason);
                }
            }
            return null;
        });

        Assert.True(await WaitForSignedInviteAsync(server));
        server.Inject(ScriptedSipServer.Response(LastInvite(server), 180));
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Ringing));

        // Дальше сервер молчит — так выглядит потерянный финальный ответ. До этой
        // правки линия оставалась в «Гудках» навсегда: на первом 1xx таймер B
        // снят, а своего предела у звонка не было.
        (int Status, string Reason)? result = await collector;
        Assert.NotNull(result);
        Assert.Equal(408, result.Value.Status);
        Assert.Null(agent.CallState);

        // Молча забыть про INVITE нельзя: он продолжит звонить у вызываемого.
        Assert.Contains(server.ReceivedRequests, request => request.Method == SipMethod.Cancel);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Ответ_на_уже_снятую_линию_подтверждается_и_закрывается_BYE()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        agent.PlaceCall("600", SdpOffer());
        Assert.True(await WaitForSignedInviteAsync(server));

        SipRequest invite = LastInvite(server);
        server.Inject(ScriptedSipServer.Response(invite, 180));
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Ringing));

        await agent.HangUpAsync();
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Cancel)));

        // Классическая ничья: 200 OK разошёлся с нашим CANCEL в сети. Для той
        // стороны это состоявшийся разговор — у вызываемого снята трубка.
        server.Inject(Answer(invite));

        Assert.True(
            await TestSupport.WaitUntilAsync(
                () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Bye)),
            "разговор, о котором мы не знаем, надо закрыть, а не бросить");

        // Порядок обязателен: BYE без ACK сервер вправе не принять — диалог для
        // него ещё не подтверждён.
        IReadOnlyList<SipRequest> requests = server.ReceivedRequests;
        int ackIndex = IndexOf(requests, SipMethod.Ack);
        int byeIndex = IndexOf(requests, SipMethod.Bye);
        Assert.True(ackIndex >= 0 && byeIndex >= 0);
        Assert.True(ackIndex < byeIndex);
        Assert.Null(agent.CallState);

        await agent.StopAsync();

        static int IndexOf(IReadOnlyList<SipRequest> requests, SipMethod method)
        {
            for (int index = 0; index < requests.Count; index++)
            {
                if (requests[index].Method == method)
                {
                    return index;
                }
            }
            return -1;
        }
    }

    [Fact]
    public async Task Отбой_после_ответа_отправляет_BYE_внутри_диалога()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        agent.PlaceCall("600", SdpOffer());
        Assert.True(await WaitForSignedInviteAsync(server));

        SipRequest invite = LastInvite(server);
        server.Inject(Answer(invite));
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Answered));

        await agent.HangUpAsync();

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Bye)));

        SipRequest bye = server.ReceivedRequests.Last(request => request.Method == SipMethod.Bye);
        Assert.Equal("172.17.0.2", bye.Uri.Host);
        Assert.Equal(invite.CallId, bye.CallId);
        Assert.NotNull(bye.To?.Tag);

        // CSeq обязан вырасти: повтор номера сервер счёл бы ретрансмиссией.
        Assert.True(bye.CSeq?.Number > invite.CSeq?.Number);
        Assert.Null(agent.CallState);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Отбой_до_ответа_отправляет_CANCEL_а_не_BYE()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        agent.PlaceCall("600", SdpOffer());
        Assert.True(await WaitForSignedInviteAsync(server));

        server.Inject(ScriptedSipServer.Response(LastInvite(server), 180));
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Ringing));

        await agent.HangUpAsync();

        // Диалога ещё нет — BYE отправлять некуда и незачем.
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Cancel)));
        Assert.DoesNotContain(server.ReceivedRequests, request => request.Method == SipMethod.Bye);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Входящий_BYE_завершает_звонок_и_получает_200()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        SipOutgoingCall call = agent.PlaceCall("600", SdpOffer());
        Task<string?> collector = Task.Run(async () =>
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

        Assert.True(await WaitForSignedInviteAsync(server));
        SipRequest invite = LastInvite(server);
        server.Inject(Answer(invite));
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Answered));

        server.Inject(MakeBye(invite, "as1a2b3c", "z9hG4bKbye1", 1));

        // Ответить обязаны: иначе Asterisk повторяет BYE и держит диалог.
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.SentResponses.Any(response => response.CSeq?.Method == SipMethod.Bye && response.StatusCode == 200)));

        Assert.Equal("собеседник завершил звонок", await collector);
        Assert.Null(agent.CallState);

        await agent.StopAsync();
    }

    [Fact]
    public async Task BYE_с_чужим_тегом_получает_481_и_не_завершает_звонок()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        agent.PlaceCall("600", SdpOffer());
        Assert.True(await WaitForSignedInviteAsync(server));

        SipRequest invite = LastInvite(server);
        server.Inject(Answer(invite));
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Answered));

        server.Inject(MakeBye(invite, "wrong-remote", "z9hG4bKforeign-bye", 9));

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.SentResponses.Any(response => response.CSeq?.Method == SipMethod.Bye && response.StatusCode == 481)));
        Assert.True(agent.CallState is SipCallState.Answered);

        // Остановка обязана закрыть оставшийся настоящий диалог до остановки
        // транспорта — это ядро сценария отключения в приложении.
        await agent.StopAsync();
        Assert.Contains(server.ReceivedRequests, request => request.Method == SipMethod.Bye);
        Assert.Null(agent.CallState);
    }

    [Fact]
    public async Task Линий_три_четвёртая_отклоняется()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        List<string> identifiers = [];
        foreach (string number in (string[])["600", "601", "602"])
        {
            identifiers.Add(agent.PlaceCall(number, SdpOffer()).CallId);
        }

        Assert.True(await TestSupport.WaitUntilAsync(() => agent.Lines.Count == SipUserAgent.MaximumLines));
        Assert.Equal(3, identifiers.Distinct(StringComparer.Ordinal).Count());

        // Четвёртая линия отказывается сразу, не отправляя INVITE.
        SipOutgoingCall extra = agent.PlaceCall("603", SdpOffer());

        string? reason = null;
        await foreach (SipCallEvent value in extra.Events)
        {
            if (value is SipCallEvent.Failed failed)
            {
                reason = failed.Reason;
            }
        }

        Assert.Equal(SipCallErrors.TooManyLines(3), reason);
        Assert.Equal(3, agent.Lines.Count);

        // Считаем не запросы, а линии, до которых они дошли: каждая шлёт по два
        // INVITE — без авторизации и с ней, — и момент второго нам не подвластен.
        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.ReceivedRequests
                .Where(request => request.Method == SipMethod.Invite)
                .Select(request => request.CallId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(identifiers.Order(StringComparer.Ordinal), StringComparer.Ordinal)));

        await agent.StopAsync();
    }

    [Fact]
    public async Task Пустой_номер_отклоняется_до_отправки_запроса()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        int before = server.ReceivedRequests.Count(request => request.Method == SipMethod.Invite);
        SipOutgoingCall call = agent.PlaceCall("   ", SdpOffer());

        string? reason = null;
        await foreach (SipCallEvent value in call.Events)
        {
            if (value is SipCallEvent.Failed failed)
            {
                reason = failed.Reason;
            }
        }

        Assert.Equal(SipCallErrors.EmptyTarget, reason);
        Assert.Equal(before, server.ReceivedRequests.Count(request => request.Method == SipMethod.Invite));

        await agent.StopAsync();
    }

    /// <summary>BYE со стороны сервера внутри установленного диалога.</summary>
    private static SipRequest MakeBye(SipRequest invite, string remoteTag, string branch, int sequence)
    {
        var bye = new SipRequest(SipMethod.Bye, new SipUri("192.168.1.50", user: "100"));
        bye.Headers.Append(SipHeaderName.Via, $"SIP/2.0/UDP 172.17.0.2:5060;branch={branch}");
        bye.Headers.Append(SipHeaderName.From, $"<sip:600@127.0.0.1>;tag={remoteTag}");
        bye.Headers.Append(SipHeaderName.To, invite.Headers.First(SipHeaderName.From) ?? string.Empty);
        bye.Headers.Append(SipHeaderName.CallId, invite.CallId ?? string.Empty);
        bye.Headers.Append(SipHeaderName.CSeq, $"{sequence} BYE");
        return bye;
    }
}
