using System.Text;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Три линии, адресуемые по Call-ID.
///
/// Проверяется ровно то, что нельзя увидеть на одной линии: что запрос попадает
/// в свою линию, а не в первую попавшуюся, и что операция над одной не задевает
/// соседнюю. Ошибка здесь выглядит как завершённый не тот разговор, и на живой
/// АТС ловить её поздно.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/LinesTests.swift</c>.
/// </summary>
public sealed class LinesTests
{
    private static ReadOnlyMemory<byte> Offer => Encoding.UTF8.GetBytes(
        "v=0\r\no=- 1 1 IN IP4 10.0.0.5\r\ns=-\r\nc=IN IP4 10.0.0.5\r\nt=0 0\r\nm=audio 16000 RTP/AVP 0\r\n");

    private static ScriptedSipServer MakeServer() =>
        new((request, _) =>
        {
            if (request.Headers.First(SipHeaderName.Authorization) is null)
            {
                return ScriptedSipServer.Unauthorized(request);
            }

            return request.Method switch
            {
                SipMethod.Register => ScriptedSipServer.RegistrationAccepted(request),

                // Ответы на INVITE присылаются вручную: их несколько, и линий
                // тоже несколько.
                SipMethod.Invite => null,

                SipMethod.Refer => ScriptedSipServer.Response(request, 202),
                _ => ScriptedSipServer.Response(request, 200),
            };
        });

    private static async Task<SipUserAgent> MakeAgentAsync(ScriptedSipServer server)
    {
        var agent = new SipUserAgent(
            TestSupport.TestAccount(),
            TestSupport.TestCredentials,
            server,
            TestSupport.FastTimers());

        await agent.StartAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));
        return agent;
    }

    /// <summary>
    /// Заводит линию и доводит её до разговора.
    ///
    /// Тег собеседника у каждой линии свой: с одинаковым тегом проверка адресации
    /// теряет смысл — перепутанная линия совпала бы по всем полям.
    /// </summary>
    private static async Task<(string CallId, SipRequest Invite)> AnsweredLineAsync(
        SipUserAgent agent,
        ScriptedSipServer server,
        string number,
        string toTag)
    {
        SipOutgoingCall call = agent.PlaceCall(number, Offer);

        // Первый INVITE уходит без авторизации, второй — подписанный.
        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.ReceivedRequests.Count(request =>
                request.Method == SipMethod.Invite && request.CallId == call.CallId) >= 2));

        SipRequest invite = server.ReceivedRequests.Last(request =>
            request.Method == SipMethod.Invite && request.CallId == call.CallId);

        SipResponse ok = ScriptedSipServer.Response(
            invite,
            200,
            toTag,
            extraHeaders: (SipHeaderName.Contact, $"<sip:{number}@172.17.0.2:5060>"));

        ok.Body = Offer;
        ok.Headers.Append(SipHeaderName.ContentType, "application/sdp");
        server.Inject(ok);

        Assert.True(await TestSupport.WaitUntilAsync(
            () => agent.CallStateOf(call.CallId) is SipCallState.Answered));

        return (call.CallId, invite);
    }

    /// <summary>Запрос от сервера внутри уже установленного диалога.</summary>
    private static SipRequest RequestInside(
        SipMethod method,
        SipRequest invite,
        string toTag,
        string branch,
        int sequence)
    {
        var request = new SipRequest(method, new SipUri("192.168.1.50", user: "100"));
        request.Headers.Append(SipHeaderName.Via, $"SIP/2.0/UDP 172.17.0.2:5060;branch={branch}");
        request.Headers.Append(SipHeaderName.From, $"<sip:600@127.0.0.1>;tag={toTag}");
        request.Headers.Append(SipHeaderName.To, invite.Headers.First(SipHeaderName.From) ?? string.Empty);
        request.Headers.Append(SipHeaderName.CallId, invite.CallId ?? string.Empty);
        request.Headers.Append(SipHeaderName.CSeq, $"{sequence} {method.Name()}");
        request.Headers.Append(SipHeaderName.Contact, "<sip:600@172.17.0.2:5060>");
        return request;
    }

    [Fact]
    public async Task Три_линии_живут_одновременно_и_различаются_по_CallId()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        (string CallId, SipRequest Invite) first = await AnsweredLineAsync(agent, server, "600", "tag-600");
        (string CallId, SipRequest Invite) second = await AnsweredLineAsync(agent, server, "601", "tag-601");
        (string CallId, SipRequest Invite) third = await AnsweredLineAsync(agent, server, "602", "tag-602");

        IReadOnlyList<SipUserAgent.Line> lines = agent.Lines;
        Assert.Equal([first.CallId, second.CallId, third.CallId], lines.Select(line => line.CallId));
        Assert.Equal(["600", "601", "602"], lines.Select(line => line.Peer));
        Assert.All(lines, line => Assert.True(line.State is SipCallState.Answered));
        Assert.False(agent.HasFreeLine);

        // Единственной линии больше нет — значит и умолчания у адресации тоже.
        Assert.Null(agent.CallState);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Место_под_линию_занимается_до_отправки_INVITE()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        // Четыре набора подряд, без единого ожидания между ними. Диалогов в этот
        // момент нет ни одного: звонок ещё ждёт готовности транспорта, и если
        // место занимать по факту INVITE, все четыре пройдут проверку свободной
        // линии.
        List<SipOutgoingCall> placed = [];
        foreach (string number in (string[])["600", "601", "602", "603"])
        {
            placed.Add(agent.PlaceCall(number, Offer));
        }

        // Четвёртый поток уже закрыт отказом, поэтому читается до конца сразу.
        string? failure = null;
        await foreach (SipCallEvent value in placed[3].Events)
        {
            if (value is SipCallEvent.Failed failed)
            {
                failure = failed.Reason;
            }
        }
        Assert.Equal(SipCallErrors.TooManyLines(3), failure);

        // Ждём именно INVITE, а не появления линии в словаре: линия заводится на
        // шаг раньше отправки, и счёт запросов в этот момент ещё не сошёлся.
        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.ReceivedRequests
                .Where(request => request.Method == SipMethod.Invite)
                .Select(request => request.CallId)
                .Distinct(StringComparer.Ordinal)
                .Count() == 3));

        Assert.Equal(3, agent.Lines.Count);
        Assert.DoesNotContain(
            server.ReceivedRequests.Where(request => request.Method == SipMethod.Invite),
            request => request.CallId == placed[3].CallId);

        await agent.StopAsync();
    }

    [Fact]
    public async Task BYE_завершает_только_свою_линию()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        (string CallId, SipRequest Invite) first = await AnsweredLineAsync(agent, server, "600", "tag-600");
        (string CallId, SipRequest Invite) second = await AnsweredLineAsync(agent, server, "601", "tag-601");

        server.Inject(RequestInside(SipMethod.Bye, second.Invite, "tag-601", "z9hG4bK-bye-second", 2));

        Assert.True(await TestSupport.WaitUntilAsync(() => agent.Lines.Count == 1));
        Assert.Null(agent.CallStateOf(second.CallId));
        Assert.True(agent.CallStateOf(first.CallId) is SipCallState.Answered);

        // Тег второй линии по первой уже не проходит: тройка идентификаторов
        // сверяется целиком.
        server.Inject(RequestInside(SipMethod.Bye, first.Invite, "tag-601", "z9hG4bK-bye-wrong-tag", 3));

        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.SentResponses.Any(response =>
                response.CSeq?.Method == SipMethod.Bye && response.StatusCode == 481)));
        Assert.True(agent.CallStateOf(first.CallId) is SipCallState.Answered);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Повторный_INVITE_попадает_в_свою_линию()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        var seen = new CallIdLog();
        agent.SetMediaRenegotiator((callId, _) =>
        {
            seen.Store(callId);
            return Task.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                "v=0\r\no=- 2 2 IN IP4 10.0.0.5\r\ns=-\r\nc=IN IP4 10.0.0.5\r\nt=0 0\r\n"
                    + "m=audio 16000 RTP/AVP 0\r\na=recvonly\r\n"));
        });

        await AnsweredLineAsync(agent, server, "600", "tag-600");
        (string CallId, SipRequest Invite) second = await AnsweredLineAsync(agent, server, "601", "tag-601");

        SipRequest reinvite = RequestInside(
            SipMethod.Invite,
            second.Invite,
            "tag-601",
            "z9hG4bK-reinvite-second",
            2);

        reinvite.Body = Encoding.UTF8.GetBytes(
            "v=0\r\no=root 2 2 IN IP4 172.17.0.2\r\ns=-\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
                + "m=audio 14028 RTP/AVP 0\r\na=sendonly\r\n");
        reinvite.Headers.Append(SipHeaderName.ContentType, "application/sdp");
        server.Inject(reinvite);

        Assert.True(await TestSupport.WaitUntilAsync(() => seen.Values.Count == 1));
        Assert.Equal([second.CallId], seen.Values);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Удержание_одной_линии_не_трогает_соседнюю()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        (string CallId, SipRequest Invite) first = await AnsweredLineAsync(agent, server, "600", "tag-600");
        (string CallId, SipRequest Invite) second = await AnsweredLineAsync(agent, server, "601", "tag-601");

        ReadOnlyMemory<byte> held = Encoding.UTF8.GetBytes(
            "v=0\r\no=- 2 2 IN IP4 10.0.0.5\r\ns=-\r\nc=IN IP4 10.0.0.5\r\nt=0 0\r\n"
                + "m=audio 16000 RTP/AVP 0\r\na=sendonly\r\n");

        Task<ReadOnlyMemory<byte>> reinvite = agent.ReinviteAsync(first.CallId, held);

        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.ReceivedRequests.Any(request =>
                request.Method == SipMethod.Invite
                && request.CallId == first.CallId
                && request.CSeq?.Number > 1)));

        SipRequest sent = server.ReceivedRequests.Last(request =>
            request.Method == SipMethod.Invite && request.CSeq?.Number > 1);

        Assert.Equal(first.CallId, sent.CallId);
        Assert.Equal("tag-600", sent.To?.Tag);

        SipResponse ok = ScriptedSipServer.Response(
            sent,
            200,
            "tag-600",
            extraHeaders: (SipHeaderName.Contact, "<sip:600@172.17.0.2:5060>"));
        ok.Body = held;
        ok.Headers.Append(SipHeaderName.ContentType, "application/sdp");
        server.Inject(ok);

        await reinvite;
        Assert.True(agent.CallStateOf(first.CallId) is SipCallState.Answered);
        Assert.True(agent.CallStateOf(second.CallId) is SipCallState.Answered);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Консультационный_перевод_ссылается_на_диалог_второй_линии()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        (string CallId, SipRequest Invite) origin = await AnsweredLineAsync(agent, server, "600", "tag-600");
        (string CallId, SipRequest Invite) consultation = await AnsweredLineAsync(agent, server, "601", "tag-601");

        SipDialogIdentifier? replaces = agent.DialogIdentifierOf(consultation.CallId);
        Assert.NotNull(replaces);
        Assert.Equal(consultation.CallId, replaces.Value.CallId);
        Assert.Equal("tag-601", replaces.Value.RemoteTag);

        IAsyncEnumerable<SipTransferEvent> events = agent.Transfer(origin.CallId, "601", replaces);
        Task<List<SipTransferEvent>> collector = Task.Run(async () =>
        {
            List<SipTransferEvent> result = [];
            await foreach (SipTransferEvent value in events)
            {
                result.Add(value);
            }
            return result;
        });

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Refer)));

        SipRequest refer = server.ReceivedRequests.Last(request => request.Method == SipMethod.Refer);
        Assert.Equal(origin.CallId, refer.CallId);

        string? referTo = refer.Headers.First(SipHeaderName.ReferTo);
        Assert.NotNull(referTo);
        Assert.Contains("Replaces=", referTo, StringComparison.Ordinal);
        Assert.Contains("to-tag%3Dtag-601", referTo, StringComparison.Ordinal);

        // Финальный NOTIFY приходит внутри исходного диалога.
        SipRequest notify = RequestInside(
            SipMethod.Notify,
            origin.Invite,
            "tag-600",
            "z9hG4bK-notify-attended",
            3);

        notify.Headers.Append(SipHeaderName.Event, "refer");
        notify.Headers.Append(SipHeaderName.SubscriptionState, "terminated;reason=noresource");
        notify.Headers.Append(SipHeaderName.ContentType, "message/sipfrag;version=2.0");
        notify.Body = Encoding.UTF8.GetBytes("SIP/2.0 200 OK\r\n");
        server.Inject(notify);

        List<SipTransferEvent> result = await collector;
        Assert.Equal<SipTransferEvent>(
            [new SipTransferEvent.Accepted(), new SipTransferEvent.Succeeded()],
            result);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Выход_кладёт_трубку_на_каждой_линии()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        (string CallId, SipRequest Invite) first = await AnsweredLineAsync(agent, server, "600", "tag-600");
        (string CallId, SipRequest Invite) second = await AnsweredLineAsync(agent, server, "601", "tag-601");

        await agent.StopAsync();

        IReadOnlyList<SipRequest> requests = server.ReceivedRequests;
        HashSet<string> byes = [.. requests
            .Where(request => request.Method == SipMethod.Bye)
            .Select(request => request.CallId!)];

        Assert.Equal<HashSet<string>>([first.CallId, second.CallId], byes);

        // Снятие регистрации идёт последним: закрывать диалоги после отключения
        // транспорта уже некуда.
        int unregisterIndex = LastIndexOf(requests, request =>
            request.Method == SipMethod.Register && request.Headers.First(SipHeaderName.Expires) == "0");
        int lastByeIndex = LastIndexOf(requests, request => request.Method == SipMethod.Bye);

        Assert.True(unregisterIndex >= 0);
        Assert.True(lastByeIndex >= 0);
        Assert.True(lastByeIndex < unregisterIndex);

        static int LastIndexOf(IReadOnlyList<SipRequest> requests, Func<SipRequest, bool> predicate)
        {
            for (int index = requests.Count - 1; index >= 0; index--)
            {
                if (predicate(requests[index]))
                {
                    return index;
                }
            }
            return -1;
        }
    }

    [Fact]
    public async Task Занятому_оператору_входящий_по_прежнему_отвечает_486()
    {
        ScriptedSipServer server = MakeServer();
        using SipUserAgent agent = await MakeAgentAsync(server);

        await AnsweredLineAsync(agent, server, "600", "tag-600");

        var invite = new SipRequest(SipMethod.Invite, new SipUri("192.168.1.50", user: "100"), body: Offer);
        invite.Headers.Append(SipHeaderName.Via, "SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bK-incoming");
        invite.Headers.Append(SipHeaderName.From, "<sip:2929@127.0.0.1>;tag=queue-tag");
        invite.Headers.Append(SipHeaderName.To, "<sip:100@127.0.0.1>");
        invite.Headers.Append(SipHeaderName.CallId, "incoming-while-busy@lab");
        invite.Headers.Append(SipHeaderName.CSeq, "1 INVITE");
        invite.Headers.Append(SipHeaderName.Contact, "<sip:2929@172.17.0.2:5060>");
        invite.Headers.Append(SipHeaderName.ContentType, "application/sdp");
        server.Inject(invite);

        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.SentResponses.Any(response =>
                response.CallId == "incoming-while-busy@lab" && response.StatusCode == 486)));

        Assert.Single(agent.Lines);

        await agent.StopAsync();
    }

    /// <summary>
    /// Список линий, которые видел пересогласователь.
    ///
    /// Делегат зовут с чужого потока, поэтому просто переменной не обойтись.
    /// </summary>
    private sealed class CallIdLog
    {
        private readonly Lock _gate = new();
        private readonly List<string> _storage = [];

        public void Store(string callId)
        {
            lock (_gate)
            {
                _storage.Add(callId);
            }
        }

        public IReadOnlyList<string> Values
        {
            get
            {
                lock (_gate)
                {
                    return [.. _storage];
                }
            }
        }
    }
}
