using System.Text;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Таймер сессии в обмене с сервером: предложение в INVITE, отказ 422, молчание
/// сервера и поведение таймера внутри разговора.
///
/// Вторая половина <c>SessionTimerTests.swift</c> — та, которой нужен агент.
/// </summary>
public sealed class SessionTimerExchangeTests
{
    private static ReadOnlyMemory<byte> Offer => Encoding.UTF8.GetBytes("v=0");

    private static SipUserAgent MakeAgent(
        ScriptedSipServer server,
        SipSessionTimerPolicy? policy = null) =>
        new(
            TestSupport.TestAccount(server.Transport),
            TestSupport.TestCredentials,
            server,
            TestSupport.FastTimers(),
            sessionTimerPolicy: policy);

    [Fact]
    public async Task В_INVITE_уходит_предложение_таймера_и_объявляется_поддержка()
    {
        var server = new ScriptedSipServer((request, _) => request.Method == SipMethod.Register
            ? ScriptedSipServer.RegistrationAccepted(request, 300)
            : null);

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();
        agent.PlaceCall("600", Offer);

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Invite)));
        await agent.StopAsync();

        SipRequest invite = server.ReceivedRequests.First(request => request.Method == SipMethod.Invite);

        string? offered = invite.Headers.First(SipSessionTimerHeader.SessionExpires);
        Assert.NotNull(offered);

        (int Expires, SipRefresher? Refresher)? parsed = SipSessionTimer.Parse(offered);
        Assert.NotNull(parsed);
        Assert.Equal(1800, parsed.Value.Expires);

        // Обновлять просим сервер: цена нашей ошибки — брошенный живой разговор,
        // цена его — разговор, который и правда мёртв.
        Assert.Equal(SipRefresher.Uas, parsed.Value.Refresher);

        Assert.Equal(90, invite.Headers.Number(SipSessionTimerHeader.MinSE));

        string? supported = invite.Headers.First(SipHeaderName.Supported);
        Assert.NotNull(supported);
        Assert.Contains(SipSessionTimerHeader.OptionTag, supported, StringComparison.Ordinal);

        // UPDATE в Allow быть не должно: chan_sip шлёт обновление повторным
        // INVITE ровно потому, что мы не объявили UPDATE, а обработать UPDATE нам
        // нечем.
        string? allow = invite.Headers.First(SipHeaderName.Allow);
        Assert.NotNull(allow);
        Assert.DoesNotContain("UPDATE", allow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Отказ_422_повторяется_с_порогом_сервера()
    {
        var server = new ScriptedSipServer((request, _) =>
        {
            if (request.Method != SipMethod.Invite)
            {
                return ScriptedSipServer.RegistrationAccepted(request, 300);
            }

            string? raw = request.Headers.First(SipSessionTimerHeader.SessionExpires);
            int offered = raw is not null && SipSessionTimer.Parse(raw) is { } parsed ? parsed.Expires : 0;

            // Порог намеренно выше того, что клиент предлагает по умолчанию.
            return offered >= 3600
                ? null
                : ScriptedSipServer.Response(request, 422, extraHeaders: (SipSessionTimerHeader.MinSE, "3600"));
        });

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();
        agent.PlaceCall("600", Offer);

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Count(request => request.Method == SipMethod.Invite) >= 2));
        await agent.StopAsync();

        SipRequest second = server.ReceivedRequests
            .Where(request => request.Method == SipMethod.Invite)
            .ElementAt(1);

        string? retried = second.Headers.First(SipSessionTimerHeader.SessionExpires);
        Assert.NotNull(retried);

        // Повтор обязан нести срок не ниже названного сервером, иначе получился
        // бы вечный круг одинаковых запросов и одинаковых отказов.
        (int Expires, SipRefresher? Refresher)? parsedRetry = SipSessionTimer.Parse(retried);
        Assert.NotNull(parsedRetry);
        Assert.True(parsedRetry.Value.Expires >= 3600);
        Assert.Equal(3600, second.Headers.Number(SipSessionTimerHeader.MinSE));
    }

    [Fact]
    public async Task Сервер_промолчал_про_срок_таймер_не_заводится()
    {
        // Тот же сценарий, что в жизни у сервера без RFC 4028: звонок проходит,
        // трубку никто не кладёт.
        var server = new ScriptedSipServer((request, _) => request.Method switch
        {
            SipMethod.Register => ScriptedSipServer.RegistrationAccepted(request, 300),
            SipMethod.Invite => ScriptedSipServer.Response(
                request,
                200,
                extraHeaders: (SipHeaderName.Contact, "<sip:600@127.0.0.1>")),
            _ => ScriptedSipServer.Response(request, 200),
        });

        // Срок, на котором таймер сработал бы почти сразу, если бы завёлся.
        using SipUserAgent agent = MakeAgent(server, new SipSessionTimerPolicy { Expires = 2, MinimumExpires = 1 });
        await agent.StartAsync();

        SipOutgoingCall call = agent.PlaceCall("600", Offer);
        Assert.True(await AwaitAnsweredAsync(call));

        // Ждём заведомо дольше срока: BYE не должен появиться.
        await Task.Delay(300);
        Assert.DoesNotContain(server.ReceivedRequests, request => request.Method == SipMethod.Bye);

        await agent.StopAsync();
    }

    // Поведение таймера в разговоре

    /// <summary>Сервер отвечает на INVITE согласием на таймер с указанной ролью.</summary>
    private static ScriptedSipServer AnsweringServer(string sessionExpires) =>
        new((request, _) =>
        {
            if (request.Method == SipMethod.Register)
            {
                return ScriptedSipServer.RegistrationAccepted(request, 300);
            }

            if (request.Method != SipMethod.Invite)
            {
                return ScriptedSipServer.Response(request, 200);
            }

            // Повторный INVITE отличается от первого наличием тега To: диалог к
            // этому моменту уже собран.
            return request.To?.Tag is not null
                ? ScriptedSipServer.Response(
                    request,
                    200,
                    extraHeaders: (SipHeaderName.Contact, "<sip:600@127.0.0.1>"))
                : ScriptedSipServer.Response(
                    request,
                    200,
                    extraHeaders:
                    [
                        (SipHeaderName.Contact, "<sip:600@127.0.0.1>"),
                        (SipSessionTimerHeader.SessionExpires, sessionExpires),
                    ]);
        });

    private static async Task<SipUserAgent> AnsweredAgentAsync(ScriptedSipServer server)
    {
        SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        SipOutgoingCall call = agent.PlaceCall("600", Offer);
        await AwaitAnsweredAsync(call);
        return agent;
    }

    private static async Task<bool> AwaitAnsweredAsync(SipOutgoingCall call)
    {
        await foreach (SipCallEvent value in call.Events)
        {
            if (value is SipCallEvent.State { Value: SipCallState.Answered })
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public async Task Собеседник_не_обновил_сессию_кладём_трубку()
    {
        // Ради чего всё и затевалось: собеседник исчез, не прислав BYE — упало
        // питание, оборвался VPN, — и линия иначе висела бы вечно.
        ScriptedSipServer server = AnsweringServer("1;refresher=uas");
        using SipUserAgent agent = await AnsweredAgentAsync(server);

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Bye),
            TimeSpan.FromSeconds(6)));

        await agent.StopAsync();
    }

    [Fact]
    public async Task Обновление_от_собеседника_отменяет_трубку()
    {
        // Обратная сторона предыдущей проверки, и она важнее: механизм, который
        // кладёт трубку, обязан замолкать при живом собеседнике. Иначе он обрывал
        // бы каждый разговор длиннее срока сессии.
        ScriptedSipServer server = AnsweringServer("1;refresher=uas");
        using SipUserAgent agent = await AnsweredAgentAsync(server);

        // Срок сессии здесь — 2 секунды (нижняя граница), и обновление уходит
        // заведомо раньше.
        await Task.Delay(1200);

        var refresh = new SipRequest(
            SipMethod.Invite,
            new SipUri("127.0.0.1", user: "100"),
            InDialogHeaders(server, SipMethod.Invite));
        server.Inject(refresh);

        // Обновление принято: без ответа 200 повторный INVITE не состоялся бы, и
        // проверка ниже оказалась бы про несработавший таймер, а не про
        // перезаведённый.
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.SentResponses.Any(response => response.StatusCode == 200)));

        // Дальше ждём дольше исходного срока: не перезаведись отсчёт, трубка
        // легла бы на второй секунде разговора.
        await Task.Delay(1400);
        Assert.DoesNotContain(server.ReceivedRequests, request => request.Method == SipMethod.Bye);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Роль_обновляющего_у_нас_уходит_повторный_INVITE()
    {
        ScriptedSipServer server = AnsweringServer("2;refresher=uac");
        using SipUserAgent agent = await AnsweredAgentAsync(server);

        // Обновление уходит на середине срока, то есть примерно через секунду.
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Count(request => request.Method == SipMethod.Invite) >= 2,
            TimeSpan.FromSeconds(6)));

        SipRequest refresh = server.ReceivedRequests
            .Where(request => request.Method == SipMethod.Invite)
            .ElementAt(1);

        // Обновляющий запрос обязан нести срок: без него собеседник читает его
        // как отказ от договорённости и снимает свой таймер.
        string? carried = refresh.Headers.First(SipSessionTimerHeader.SessionExpires);
        Assert.NotNull(carried);

        (int Expires, SipRefresher? Refresher)? parsed = SipSessionTimer.Parse(carried);
        Assert.NotNull(parsed);
        Assert.Equal(SipRefresher.Uac, parsed.Value.Refresher);

        await agent.StopAsync();
    }

    /// <summary>
    /// Заголовки запроса внутри уже установленного диалога.
    ///
    /// Собираются из того, что клиент прислал сам: сервер в этих проверках
    /// поддельный, и другого источника тегов и Call-ID у него нет.
    /// </summary>
    private static SipHeaders InDialogHeaders(ScriptedSipServer server, SipMethod method)
    {
        SipRequest? invite = server.ReceivedRequests.FirstOrDefault(request => request.Method == SipMethod.Invite);
        var headers = new SipHeaders();

        var via = new SipVia(SipTransport.Udp, "127.0.0.1", 5060) { Branch = SipToken.Branch() };
        headers.Append(SipHeaderName.Via, via.ToString());
        headers.Append(SipHeaderName.MaxForwards, "70");

        // From и To меняются местами: запрос идёт в обратную сторону.
        var from = new NameAddress(new SipUri("127.0.0.1", user: "600")) { Tag = "as1a2b3c" };
        headers.Append(SipHeaderName.From, from.ToString());

        var to = new NameAddress(new SipUri("127.0.0.1", user: "100")) { Tag = invite?.From?.Tag };
        headers.Append(SipHeaderName.To, to.ToString());

        headers.Append(SipHeaderName.CallId, invite?.CallId ?? string.Empty);
        headers.Append(SipHeaderName.CSeq, $"9 {method.Name()}");
        headers.Append(SipHeaderName.Contact, "<sip:600@127.0.0.1>");
        return headers;
    }
}
