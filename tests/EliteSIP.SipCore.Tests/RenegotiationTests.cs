using System.Text;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Повторный INVITE в обе стороны — то, из чего сделано удержание.
///
/// Проверяется то, что на живой АТС различить нельзя: разговор в обоих случаях
/// продолжается, а звук пропадает или остаётся в одну сторону. Своё
/// пересогласование — что запрос собран внутри диалога и подтверждён; чужое —
/// что мы отвечаем новым описанием, а не прежним, и что встречные предложения не
/// сцепляются намертво.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/RenegotiationTests.swift</c>.
/// </summary>
public sealed class RenegotiationTests
{
    private const string OfferSdp =
        "v=0\r\no=root 1 1 IN IP4 172.17.0.2\r\ns=Asterisk\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
        + "m=audio 14002 RTP/AVP 0 101\r\na=rtpmap:0 PCMU/8000\r\n"
        + "a=rtpmap:101 telephone-event/8000\r\na=sendrecv\r\n\r\n";

    /// <summary>Предложение удержания: то же, но с a=sendonly и следующей версией.</summary>
    private static readonly string HoldSdp = OfferSdp
        .Replace("a=sendrecv", "a=sendonly", StringComparison.Ordinal)
        .Replace("o=root 1 1", "o=root 1 2", StringComparison.Ordinal);

    private const string AnswerSdp =
        "v=0\r\no=elitesip 1 1 IN IP4 192.168.1.50\r\ns=-\r\nc=IN IP4 192.168.1.50\r\nt=0 0\r\n"
        + "m=audio 16000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n\r\n";

    private static readonly string HeldAnswerSdp =
        AnswerSdp.Replace("a=sendrecv", "a=recvonly", StringComparison.Ordinal);

    private static SipRequest MakeInvite(
        string callId = "reneg-1",
        string branch = "z9hG4bKinbound1",
        int sequence = 102,
        string body = OfferSdp)
    {
        var request = new SipRequest(
            SipMethod.Invite,
            new SipUri("192.168.1.50", user: "100"),
            body: Encoding.UTF8.GetBytes(body));

        request.Headers.Append(SipHeaderName.Via, $"SIP/2.0/UDP 172.17.0.2:5060;branch={branch};rport");
        request.Headers.Append(SipHeaderName.MaxForwards, "70");
        request.Headers.Append(SipHeaderName.From, "\"AutoDialer\" <sip:2929@172.17.0.2>;tag=as77aabb");
        request.Headers.Append(SipHeaderName.To, "<sip:100@192.168.1.50>");
        request.Headers.Append(SipHeaderName.CallId, callId);
        request.Headers.Append(SipHeaderName.CSeq, $"{sequence} INVITE");
        request.Headers.Append(SipHeaderName.Contact, "<sip:2929@172.17.0.2:5060>");
        request.Headers.Append(SipHeaderName.ContentType, "application/sdp");
        return request;
    }

    /// <summary>Повторный INVITE внутри уже установленного диалога: с нашим тегом в To.</summary>
    private static SipRequest MakeReinvite(
        string toTag,
        int sequence = 103,
        string branch = "z9hG4bKinbound2",
        string? body = null)
    {
        SipRequest request = MakeInvite(branch: branch, sequence: sequence, body: body ?? HoldSdp);
        request.Headers.Set(SipHeaderName.To, $"<sip:100@192.168.1.50>;tag={toTag}");
        return request;
    }

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

    /// <summary>
    /// Сервер, который отвечает только на регистрацию.
    ///
    /// Молчание в ответ на INVITE здесь обязательно: ответы на повторный INVITE
    /// тесты подают руками, а автоответ на любой запрос подтверждал бы наше
    /// пересогласование раньше, чем тест успевает вмешаться.
    /// </summary>
    private static ScriptedSipServer AcceptingServer() =>
        new((request, index) => request.Method != SipMethod.Register
            ? null
            : index == 0
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

    /// <summary>Доводит входящий звонок до разговора и возвращает наш тег.</summary>
    private static async Task<string> AnsweredCallAsync(SipUserAgent agent, ScriptedSipServer server)
    {
        Task<SipIncomingCall?> pending = Task.Run(async () =>
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

        SipRequest invite = MakeInvite();
        server.Inject(invite);
        Assert.NotNull(await pending);

        await agent.AnswerIncomingCallAsync(answer: Encoding.UTF8.GetBytes(AnswerSdp));

        string? toTag = server.SentResponses.Last(response => response.StatusCode == 200).To?.Tag;
        Assert.NotNull(toTag);

        server.Inject(MakeAck(invite, toTag));
        await TestSupport.WaitUntilAsync(() => agent.CallState is SipCallState.Answered);
        return toTag;
    }

    // Чужое пересогласование

    [Fact]
    public async Task Ответ_на_чужое_удержание_собирает_пересогласователь()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);

        var seen = new OfferBox();
        agent.SetMediaRenegotiator((_, offer) =>
        {
            seen.Store(offer);
            return Task.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(HeldAnswerSdp));
        });

        string toTag = await AnsweredCallAsync(agent, server);
        int before = server.SentResponses.Count;

        server.Inject(MakeReinvite(toTag));
        Assert.True(await TestSupport.WaitUntilAsync(() => server.SentResponses.Count > before));
        await agent.StopAsync();

        // Предложение обязано доехать до приложения целиком: удержание видно
        // только в теле, и разбирать его — не дело слоя сигнализации.
        Assert.Equal(HoldSdp, seen.Text);

        SipResponse ok = server.SentResponses.Last(
            response => response.StatusCode == 200 && response.CSeq?.Method == SipMethod.Invite);

        Assert.Equal(HeldAnswerSdp, Encoding.UTF8.GetString(ok.Body.Span));
        Assert.Equal("application/sdp", ok.ContentType);

        // Тег диалога меняться не должен: это тот же разговор.
        Assert.Equal(toTag, ok.To?.Tag);
    }

    [Fact]
    public async Task Повторный_INVITE_с_чужим_локальным_тегом_получает_481()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);

        var seen = new OfferBox();
        agent.SetMediaRenegotiator((_, offer) =>
        {
            seen.Store(offer);
            return Task.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(HeldAnswerSdp));
        });

        await AnsweredCallAsync(agent, server);
        int before = server.SentResponses.Count;

        server.Inject(MakeReinvite("wrong-local-tag"));
        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.SentResponses.Skip(before).Any(response =>
                response.StatusCode == 481 && response.CSeq?.Method == SipMethod.Invite)));

        Assert.Null(seen.Text);
        Assert.True(agent.CallState is SipCallState.Answered);
        await agent.StopAsync();
    }

    [Fact]
    public async Task Повторный_INVITE_подтверждается_сразу_сначала_100_потом_200()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        agent.SetMediaRenegotiator((_, _) =>
            Task.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(HeldAnswerSdp)));

        string toTag = await AnsweredCallAsync(agent, server);
        int before = server.SentResponses.Count;

        server.Inject(MakeReinvite(toTag));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.SentResponses.Skip(before).Any(response => response.StatusCode == 200)));
        await agent.StopAsync();

        // На UDP ретрансмиссии INVITE начинаются через полсекунды, а пересборка
        // медиа столько вполне может занять.
        List<SipResponse> after = [.. server.SentResponses.Skip(before)
            .Where(response => response.CSeq?.Method == SipMethod.Invite)];

        Assert.Equal(100, after[0].StatusCode);
        Assert.Equal(200, after[^1].StatusCode);
    }

    [Fact]
    public async Task Пересогласователь_отказался_уходит_488_а_не_молчание()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        agent.SetMediaRenegotiator((_, _) => Task.FromResult<ReadOnlyMemory<byte>?>(null));

        string toTag = await AnsweredCallAsync(agent, server);
        int before = server.SentResponses.Count;

        server.Inject(MakeReinvite(toTag));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.SentResponses.Skip(before).Any(response => response.StatusCode == 488)));
        await agent.StopAsync();

        // Разговор при этом продолжается на прежних параметрах — RFC 3261 §14.1.
        SipResponse last = server.SentResponses.Last(response => response.CSeq?.Method == SipMethod.Invite);
        Assert.Equal(488, last.StatusCode);
    }

    [Fact]
    public async Task Медиа_ещё_не_поднято_повторный_INVITE_подтверждается_прежним_описанием()
    {
        // Asterisk присылает повторный INVITE через десятки миллисекунд после
        // нашего 200 OK, когда тракт ещё поднимается. 488 на это — лишний
        // повод станции считать разговор сломанным.
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        agent.SetMediaRenegotiator((_, _) => Task.FromResult<ReadOnlyMemory<byte>?>(ReadOnlyMemory<byte>.Empty));

        string toTag = await AnsweredCallAsync(agent, server);
        int before = server.SentResponses.Count;

        server.Inject(MakeReinvite(toTag));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.SentResponses.Skip(before).Any(response => response.StatusCode == 200)));
        await agent.StopAsync();

        SipResponse ok = server.SentResponses.Last(
            response => response.StatusCode == 200 && response.CSeq?.Method == SipMethod.Invite);
        Assert.Equal(AnswerSdp, Encoding.UTF8.GetString(ok.Body.Span));
    }

    [Fact]
    public async Task Повторный_INVITE_без_предложения_подтверждается_прежним_описанием()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);

        bool renegotiatorCalled = false;
        agent.SetMediaRenegotiator((_, _) =>
        {
            renegotiatorCalled = true;
            return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        });

        string toTag = await AnsweredCallAsync(agent, server);
        int before = server.SentResponses.Count;

        SipRequest empty = MakeReinvite(toTag, body: "");
        empty.Headers.Remove(SipHeaderName.ContentType);
        server.Inject(empty);

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.SentResponses.Skip(before).Any(response => response.StatusCode == 200)));
        await agent.StopAsync();

        Assert.False(renegotiatorCalled, "пересогласовывать нечего: предложения не было");

        SipResponse ok = server.SentResponses.Last(
            response => response.StatusCode == 200 && response.CSeq?.Method == SipMethod.Invite);
        Assert.Equal(AnswerSdp, Encoding.UTF8.GetString(ok.Body.Span));
    }

    // Своё пересогласование

    [Fact]
    public async Task Своё_удержание_уходит_повторным_INVITE_внутри_диалога()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        agent.SetMediaRenegotiator((_, _) => Task.FromResult<ReadOnlyMemory<byte>?>(null));

        string toTag = await AnsweredCallAsync(agent, server);

        string heldText = AnswerSdp.Replace("a=sendrecv", "a=sendonly", StringComparison.Ordinal);
        ReadOnlyMemory<byte> held = Encoding.UTF8.GetBytes(heldText);

        Task<ReadOnlyMemory<byte>> reinvite = agent.ReinviteAsync(offer: held);

        Assert.True(await TestSupport.WaitUntilAsync(() =>
            server.ReceivedRequests.Any(request =>
                request.Method == SipMethod.Invite && request.Body.Span.SequenceEqual(held.Span))));

        SipRequest request = server.ReceivedRequests.Last(value => value.Method == SipMethod.Invite);

        SipResponse accepted = ScriptedSipServer.Response(
            request,
            200,
            "as77aabb",
            extraHeaders:
            [
                (SipHeaderName.Contact, "<sip:2929@172.17.0.2:5060>"),
                (SipHeaderName.ContentType, "application/sdp"),
            ]);
        accepted.Body = Encoding.UTF8.GetBytes(HeldAnswerSdp);
        server.Inject(accepted);

        // Тело ответа отдаётся вызывающему байтами: разбирать SDP — не дело слоя
        // сигнализации ни на первом INVITE, ни на повторном.
        ReadOnlyMemory<byte> answer = await reinvite;
        Assert.Equal(HeldAnswerSdp, Encoding.UTF8.GetString(answer.Span));

        // Запрос обязан быть внутри диалога: тот же Call-ID, оба тега на месте,
        // CSeq больше нуля, и адрес — Contact собеседника.
        Assert.Equal("reneg-1", request.CallId);
        Assert.Equal(toTag, request.From?.Tag);
        Assert.Equal("as77aabb", request.To?.Tag);
        Assert.True(request.CSeq?.Number > 0);
        Assert.Contains("2929@172.17.0.2:5060", request.Uri.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(request.Contacts);

        // И подтверждён ACK с тем же номером CSeq.
        SipRequest ack = server.ReceivedRequests.Last(value => value.Method == SipMethod.Ack);
        Assert.Equal(request.CSeq?.Number, ack.CSeq?.Number);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Отказ_на_своё_удержание_разговор_не_рвёт()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);
        agent.SetMediaRenegotiator((_, _) => Task.FromResult<ReadOnlyMemory<byte>?>(null));

        await AnsweredCallAsync(agent, server);

        Task<ReadOnlyMemory<byte>> reinvite = agent.ReinviteAsync(offer: Encoding.UTF8.GetBytes(AnswerSdp));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Invite)));

        SipRequest request = server.ReceivedRequests.Last(value => value.Method == SipMethod.Invite);
        server.Inject(ScriptedSipServer.Response(request, 488, "as77aabb"));

        SipRenegotiationException error = await Assert.ThrowsAsync<SipRenegotiationException>(() => reinvite);
        Assert.Equal(SipRenegotiationErrorKind.Rejected, error.Kind);
        Assert.Equal(488, error.Status);

        // Главное: звонок продолжается. Оператор потерял удержание, а не связь.
        Assert.True(agent.CallState is SipCallState.Answered);
        await agent.StopAsync();
    }

    [Fact]
    public async Task Встречное_предложение_получает_491_а_не_второй_ответ()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = await RegisteredAgentAsync(server);

        bool renegotiatorCalled = false;
        agent.SetMediaRenegotiator((_, _) =>
        {
            renegotiatorCalled = true;
            return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        });

        string toTag = await AnsweredCallAsync(agent, server);

        // Наш INVITE ушёл и ответа ещё нет — ровно то состояние, в котором
        // встречное предложение обязано получить отказ.
        Task<ReadOnlyMemory<byte>> reinvite = agent.ReinviteAsync(offer: Encoding.UTF8.GetBytes(AnswerSdp));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Invite)));

        server.Inject(MakeReinvite(toTag, 104, "z9hG4bKglare"));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.SentResponses.Any(response => response.StatusCode == 491)));

        Assert.False(renegotiatorCalled, "на встречное предложение отвечаем 491, а не пересогласовываем");

        await agent.StopAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => reinvite);
    }

    /// <summary>
    /// Ящик для предложения, увиденного пересогласователем.
    ///
    /// Делегат зовут с чужого потока, поэтому просто переменной не обойтись.
    /// </summary>
    private sealed class OfferBox
    {
        private readonly Lock _gate = new();
        private string? _storage;

        public void Store(ReadOnlyMemory<byte> data)
        {
            lock (_gate)
            {
                _storage = Encoding.UTF8.GetString(data.Span);
            }
        }

        public string? Text
        {
            get
            {
                lock (_gate)
                {
                    return _storage;
                }
            }
        }
    }
}
