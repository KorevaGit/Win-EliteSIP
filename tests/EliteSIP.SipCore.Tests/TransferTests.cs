using System.Text;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Перевод звонка через REFER (RFC 3515) и его результат в NOTIFY.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/TransferTests.swift</c>.
/// </summary>
public sealed class TransferTests
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
                SipMethod.Invite => null,
                SipMethod.Refer => ScriptedSipServer.Response(request, 202),
                _ => ScriptedSipServer.Response(request, 200),
            };
        });

    private static async Task<(SipUserAgent Agent, SipRequest Invite)> AnsweredAgentAsync(
        ScriptedSipServer server,
        TimeSpan? transferResultTimeout = null)
    {
        var agent = new SipUserAgent(
            TestSupport.TestAccount(),
            TestSupport.TestCredentials,
            server,
            TestSupport.FastTimers(),
            transferResultTimeout: transferResultTimeout);

        await agent.StartAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));

        agent.PlaceCall("600", Offer);
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Count(request => request.Method == SipMethod.Invite) >= 2));

        SipRequest invite = server.ReceivedRequests.Last(request => request.Method == SipMethod.Invite);

        SipResponse ok = ScriptedSipServer.Response(
            invite,
            200,
            extraHeaders: (SipHeaderName.Contact, "<sip:600@172.17.0.2:5060>"));
        ok.Body = Offer;
        ok.Headers.Append(SipHeaderName.ContentType, "application/sdp");
        server.Inject(ok);

        Assert.True(await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Answered));
        return (agent, invite);
    }

    private static SipRequest MakeNotify(
        SipRequest invite,
        int status,
        string reason,
        bool terminated = true)
    {
        var request = new SipRequest(
            SipMethod.Notify,
            new SipUri("192.168.1.50", user: "100"),
            body: Encoding.UTF8.GetBytes($"SIP/2.0 {status} {reason}\r\n"));

        request.Headers.Append(SipHeaderName.Via, $"SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bKnotify{status}");
        request.Headers.Append(SipHeaderName.From, "<sip:600@127.0.0.1>;tag=as1a2b3c");
        request.Headers.Append(SipHeaderName.To, invite.Headers.First(SipHeaderName.From) ?? string.Empty);
        request.Headers.Append(SipHeaderName.CallId, invite.CallId ?? string.Empty);
        request.Headers.Append(SipHeaderName.CSeq, "3 NOTIFY");
        request.Headers.Append(SipHeaderName.Event, "refer");
        request.Headers.Append(
            SipHeaderName.SubscriptionState,
            terminated ? "terminated;reason=noresource" : "active;expires=60");
        request.Headers.Append(SipHeaderName.ContentType, "message/sipfrag;version=2.0");
        return request;
    }

    /// <summary>
    /// Копия NOTIFY с другим branch и CSeq: одинаковые значения транспорт
    /// считает ретрансмиссией и отвечает кэшированным ответом, не поднимая
    /// запрос наверх.
    /// </summary>
    private static SipRequest Distinct(SipRequest request, string mark, int sequence)
    {
        SipRequest copy = MakeNotifyCopy(request);
        copy.Headers.Set(SipHeaderName.Via, $"SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bKnotify-{mark}");
        copy.Headers.Set(SipHeaderName.CSeq, $"{sequence} NOTIFY");
        return copy;

        static SipRequest MakeNotifyCopy(SipRequest source)
        {
            var copy = new SipRequest(source.Method, source.Uri, new SipHeaders(source.Headers.Fields), source.Body);
            return copy;
        }
    }

    private static Task<List<SipTransferEvent>> CollectAsync(IAsyncEnumerable<SipTransferEvent> events) =>
        Task.Run(async () =>
        {
            List<SipTransferEvent> result = [];
            await foreach (SipTransferEvent value in events)
            {
                result.Add(value);
            }
            return result;
        });

    [Fact]
    public async Task Слепой_перевод_REFER_принят_результат_приходит_в_NOTIFY()
    {
        ScriptedSipServer server = MakeServer();
        (SipUserAgent agent, SipRequest invite) = await AnsweredAgentAsync(server);
        using SipUserAgent owned = agent;

        Task<List<SipTransferEvent>> collector = CollectAsync(agent.Transfer(target: "601"));

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Refer)));

        SipRequest refer = server.ReceivedRequests.Last(request => request.Method == SipMethod.Refer);
        Assert.Equal("<sip:601@127.0.0.1>", refer.Headers.First(SipHeaderName.ReferTo));
        Assert.Equal(invite.CallId, refer.CallId);

        // NOTIFY из чужого диалога подтверждать нельзя.
        SipRequest foreign = MakeNotify(invite, 200, "OK");
        foreign.Headers.Set(SipHeaderName.From, "<sip:600@127.0.0.1>;tag=wrong-remote");
        foreign.Headers.Set(SipHeaderName.Via, "SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bKnotify-foreign");
        foreign.Headers.Set(SipHeaderName.CSeq, "2 NOTIFY");
        server.Inject(foreign);

        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.SentResponses.Any(response =>
                response.CSeq?.Method == SipMethod.Notify && response.StatusCode == 481)));
        Assert.True(agent.CallState is SipCallState.Answered);

        server.Inject(MakeNotify(invite, 200, "OK"));

        Assert.Equal<SipTransferEvent>(
            [new SipTransferEvent.Accepted(), new SipTransferEvent.Succeeded()],
            await collector);

        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.SentResponses.Any(response =>
                response.CSeq?.Method == SipMethod.Notify && response.StatusCode == 200)));

        await agent.StopAsync();
    }

    [Fact]
    public async Task Консультационный_перевод_кодирует_Replaces_в_ReferTo()
    {
        ScriptedSipServer server = MakeServer();
        (SipUserAgent agent, SipRequest invite) = await AnsweredAgentAsync(server);
        using SipUserAgent owned = agent;

        var replacement = new SipDialogIdentifier("consult-42@example", "our-consult-tag", "their-consult-tag");
        Task<List<SipTransferEvent>> collector = CollectAsync(agent.Transfer(target: "602", replacing: replacement));

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Refer)));

        SipRequest refer = server.ReceivedRequests.Last(request => request.Method == SipMethod.Refer);
        string? referTo = refer.Headers.First(SipHeaderName.ReferTo);
        Assert.NotNull(referTo);
        Assert.StartsWith("<sip:602@127.0.0.1?Replaces=", referTo, StringComparison.Ordinal);
        Assert.Contains("consult-42%40example", referTo, StringComparison.Ordinal);
        Assert.Contains(
            "%3Bto-tag%3Dtheir-consult-tag%3Bfrom-tag%3Dour-consult-tag",
            referTo,
            StringComparison.Ordinal);

        server.Inject(MakeNotify(invite, 200, "OK"));
        Assert.Contains(new SipTransferEvent.Succeeded(), await collector);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Отказ_созданного_INVITE_объясняется_результатом_NOTIFY()
    {
        ScriptedSipServer server = MakeServer();
        (SipUserAgent agent, SipRequest invite) = await AnsweredAgentAsync(server);
        using SipUserAgent owned = agent;

        Task<List<SipTransferEvent>> collector = CollectAsync(agent.Transfer(target: "999"));

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Refer)));

        server.Inject(MakeNotify(invite, 486, "Busy Here"));

        List<SipTransferEvent> result = await collector;
        Assert.Equal(new SipTransferEvent.Accepted(), result[0]);
        Assert.Equal(
            new SipTransferEvent.Failed(486, SipCallErrors.DescribeCallFailure(486, "Busy Here")),
            result[^1]);

        await agent.StopAsync();
    }

    [Fact]
    public void Sipfrag_разбирает_код_и_причину()
    {
        (int Status, string Reason)? parsed = SipFragment.ParseStatus(
            Encoding.UTF8.GetBytes("SIP/2.0 503 Service Unavailable\r\n"));

        Assert.NotNull(parsed);
        Assert.Equal(503, parsed.Value.Status);
        Assert.Equal("Service Unavailable", parsed.Value.Reason);
        Assert.Null(SipFragment.ParseStatus(Encoding.UTF8.GetBytes("garbage")));
    }

    [Fact]
    public async Task Подписка_закрыта_без_финального_кода_перевод_не_зависает()
    {
        ScriptedSipServer server = MakeServer();
        (SipUserAgent agent, SipRequest invite) = await AnsweredAgentAsync(server);
        using SipUserAgent owned = agent;

        var collected = new EventLog();
        _ = collected.PumpAsync(agent.Transfer(target: "601"));

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Refer)));
        Assert.True(await TestSupport.WaitUntilAsync(() => collected.Events.Count == 1));

        // chan_sip сообщает промежуточный код, а затем закрывает подписку, так и
        // не сказав судьбу созданного INVITE.
        server.Inject(MakeNotify(invite, 100, "Trying", terminated: false));
        server.Inject(Distinct(MakeNotify(invite, 100, "Trying"), "closing", 4));

        Assert.True(
            await TestSupport.WaitUntilAsync(() => collected.Events.Count == 2),
            "закрытая подписка обязана завершить перевод, а не ждать таймаута");

        Assert.IsType<SipTransferEvent.Failed>(collected.Events[^1]);
        Assert.True(agent.CallState is SipCallState.Answered, "исходный разговор обязан сохраниться");

        await agent.StopAsync();
    }

    [Fact]
    public async Task REFER_с_вызовом_авторизации_повторяется_со_свежим_nonce()
    {
        var referChallenges = new Counter();
        var server = new ScriptedSipServer((request, _) =>
        {
            if (request.Headers.First(SipHeaderName.Authorization) is null)
            {
                return ScriptedSipServer.Unauthorized(request);
            }

            return request.Method switch
            {
                SipMethod.Register => ScriptedSipServer.RegistrationAccepted(request),
                SipMethod.Invite => null,

                // Первый REFER получает свежий вызов, как это делает chan_sip
                // после смены nonce.
                SipMethod.Refer => referChallenges.Next() == 0
                    ? ScriptedSipServer.Unauthorized(request, "second-nonce")
                    : ScriptedSipServer.Response(request, 202),

                _ => ScriptedSipServer.Response(request, 200),
            };
        });

        (SipUserAgent agent, SipRequest invite) = await AnsweredAgentAsync(server);
        using SipUserAgent owned = agent;

        var collected = new EventLog();
        _ = collected.PumpAsync(agent.Transfer(target: "601"));

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Count(request => request.Method == SipMethod.Refer) == 2));

        List<SipRequest> refers = [.. server.ReceivedRequests.Where(request => request.Method == SipMethod.Refer)];
        Assert.NotEqual(refers[0].CSeq?.Number, refers[1].CSeq?.Number);
        Assert.Contains(
            "second-nonce",
            refers[1].Headers.First(SipHeaderName.Authorization) ?? string.Empty,
            StringComparison.Ordinal);

        server.Inject(MakeNotify(invite, 200, "OK"));
        Assert.True(await TestSupport.WaitUntilAsync(() => collected.Events.Count == 2));
        Assert.IsType<SipTransferEvent.Succeeded>(collected.Events[^1]);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Собеседник_положил_трубку_во_время_перевода()
    {
        ScriptedSipServer server = MakeServer();
        (SipUserAgent agent, SipRequest invite) = await AnsweredAgentAsync(server);
        using SipUserAgent owned = agent;

        var collected = new EventLog();
        _ = collected.PumpAsync(agent.Transfer(target: "601"));
        Assert.True(await TestSupport.WaitUntilAsync(() => collected.Events.Count == 1));

        var bye = new SipRequest(SipMethod.Bye, new SipUri("192.168.1.50", user: "100"));
        bye.Headers.Append(SipHeaderName.Via, "SIP/2.0/UDP 172.17.0.2:5060;branch=z9hG4bKbye1");
        bye.Headers.Append(SipHeaderName.From, "<sip:600@127.0.0.1>;tag=as1a2b3c");
        bye.Headers.Append(SipHeaderName.To, invite.Headers.First(SipHeaderName.From) ?? string.Empty);
        bye.Headers.Append(SipHeaderName.CallId, invite.CallId ?? string.Empty);
        bye.Headers.Append(SipHeaderName.CSeq, "5 BYE");
        server.Inject(bye);

        Assert.True(await TestSupport.WaitUntilAsync(() => collected.Events.Count == 2));
        Assert.IsType<SipTransferEvent.Failed>(collected.Events[^1]);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Сервер_не_сообщил_результат_перевод_заканчивается_таймаутом()
    {
        ScriptedSipServer server = MakeServer();
        (SipUserAgent agent, _) = await AnsweredAgentAsync(server, TimeSpan.FromMilliseconds(200));
        using SipUserAgent owned = agent;

        List<SipTransferEvent> result = await CollectAsync(agent.Transfer(target: "601"));

        Assert.Equal(new SipTransferEvent.Accepted(), result[0]);
        Assert.Equal(new SipTransferEvent.Failed(408, SipTransferErrors.NoResult), result[^1]);
        Assert.True(agent.CallState is SipCallState.Answered, "истёкший перевод не завершает разговор");

        await agent.StopAsync();
    }

    [Fact]
    public async Task Номер_не_может_подставить_второй_SIP_заголовок()
    {
        ScriptedSipServer server = MakeServer();
        using var agent = new SipUserAgent(
            TestSupport.TestAccount(),
            TestSupport.TestCredentials,
            server,
            TestSupport.FastTimers());

        SipTransferEvent? result = null;
        await foreach (SipTransferEvent value in agent.Transfer(target: "601\r\nRefer-To: <sip:999@evil>"))
        {
            result = value;
        }

        Assert.Equal(new SipTransferEvent.Failed(0, SipTransferErrors.InvalidTarget), result);
        Assert.Empty(server.ReceivedRequests);
    }

    /// <summary>
    /// Накопитель событий перевода: поток читается отдельной задачей, а проверки
    /// смотрят на снимок, не дожидаясь конца потока.
    /// </summary>
    private sealed class EventLog
    {
        private readonly Lock _gate = new();
        private readonly List<SipTransferEvent> _events = [];

        public IReadOnlyList<SipTransferEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public Task PumpAsync(IAsyncEnumerable<SipTransferEvent> events) =>
            Task.Run(async () =>
            {
                await foreach (SipTransferEvent value in events)
                {
                    lock (_gate)
                    {
                        _events.Add(value);
                    }
                }
            });
    }

    /// <summary>
    /// Счётчик попыток для сценарного сервера: отвечающую функцию зовут с чужого
    /// потока, и хранить состояние прямо в ней нельзя.
    /// </summary>
    private sealed class Counter
    {
        private int _value = -1;

        public int Next() => Interlocked.Increment(ref _value);
    }
}
