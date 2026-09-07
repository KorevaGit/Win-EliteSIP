namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Транзакция INVITE.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/InviteTransactionTests.swift</c>.
/// </summary>
public sealed class InviteTransactionTests
{
    /// <summary>
    /// Собирает слой транзакций поверх поддельного сервера, который сам ничего
    /// не отвечает: ответы тест присылает вручную, как это и происходит с
    /// INVITE — их несколько на один запрос.
    /// </summary>
    private static async Task<(SipTransactionLayer Layer, ScriptedSipServer Server)> MakeLayerAsync(
        SipTransport transport = SipTransport.Udp)
    {
        var server = new ScriptedSipServer((_, _) => null, transport);
        var layer = new SipTransactionLayer(server, TestSupport.FastTimers());
        await layer.StartAsync();
        await layer.WaitUntilReadyAsync();
        return (layer, server);
    }

    private static SipRequest MakeInvite(string branch = "z9hG4bKinvite1")
    {
        var request = new SipRequest(SipMethod.Invite, new SipUri("127.0.0.1", user: "600"));
        request.Headers.Append(SipHeaderName.Via, $"SIP/2.0/UDP 192.168.1.50:5060;branch={branch};rport");
        request.Headers.Append(SipHeaderName.MaxForwards, "70");
        request.Headers.Append(SipHeaderName.From, "<sip:100@127.0.0.1>;tag=localtag");
        request.Headers.Append(SipHeaderName.To, "<sip:600@127.0.0.1>");
        request.Headers.Append(SipHeaderName.CallId, "call-invite-1");
        request.Headers.Append(SipHeaderName.CSeq, "20 INVITE");
        request.Headers.Append(SipHeaderName.Contact, "<sip:100@192.168.1.50:5060>");
        return request;
    }

    private static SipResponse MakeResponse(
        int status,
        string? toTag = "servertag",
        string branch = "z9hG4bKinvite1",
        string? contact = "<sip:600@172.17.0.2:5060>")
    {
        var headers = new SipHeaders();
        headers.Append(SipHeaderName.Via, $"SIP/2.0/UDP 192.168.1.50:5060;branch={branch}");
        headers.Append(SipHeaderName.From, "<sip:100@127.0.0.1>;tag=localtag");
        headers.Append(
            SipHeaderName.To,
            toTag is null ? "<sip:600@127.0.0.1>" : $"<sip:600@127.0.0.1>;tag={toTag}");
        headers.Append(SipHeaderName.CallId, "call-invite-1");
        headers.Append(SipHeaderName.CSeq, "20 INVITE");
        if (contact is not null)
        {
            headers.Append(SipHeaderName.Contact, contact);
        }
        return new SipResponse(status, headers: headers);
    }

    /// <summary>Одно событие потока — короткой строкой, чтобы падение читалось как фраза.</summary>
    private static string Name(SipInviteEvent value) => value switch
    {
        SipInviteEvent.Provisional provisional => $"prov{provisional.Response.StatusCode}",
        SipInviteEvent.Success success => $"ok{success.Response.StatusCode}",
        SipInviteEvent.Failure failure => $"fail{failure.Response.StatusCode}",
        SipInviteEvent.Timeout => "timeout",
        SipInviteEvent.TransportFailed failed => $"transport:{failed.Reason}",
        _ => "неизвестное событие",
    };

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<SipInviteEvent> events)
    {
        List<string> names = [];
        await foreach (SipInviteEvent value in events)
        {
            names.Add(Name(value));
        }
        return names;
    }

    [Fact]
    public async Task Гудки_и_ответ_100_180_200()
    {
        (SipTransactionLayer layer, ScriptedSipServer server) = await MakeLayerAsync();
        Task<List<string>> collector = Task.Run(() => CollectAsync(layer.SendInvite(MakeInvite())));

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Invite)));

        server.Inject(MakeResponse(100, toTag: null, contact: null));
        server.Inject(MakeResponse(180, contact: null));
        server.Inject(MakeResponse(200));

        Assert.Equal(["prov100", "prov180", "ok200"], await collector);

        // ACK на 2xx слой НЕ отправляет: он идёт вне транзакции, по маршруту
        // диалога, и это обязанность вызывающей стороны.
        Assert.DoesNotContain(server.ReceivedRequests, request => request.Method == SipMethod.Ack);

        await layer.StopAsync();
        layer.Dispose();
    }

    [Fact]
    public async Task На_неуспешный_ответ_слой_сам_отправляет_ACK()
    {
        (SipTransactionLayer layer, ScriptedSipServer server) = await MakeLayerAsync();
        IAsyncEnumerable<SipInviteEvent> events = layer.SendInvite(MakeInvite());

        // Возвращаем первое же событие, каким бы оно ни было: если вместо отказа
        // придёт таймаут, сообщение об ошибке должно это показать, а не просто
        // сказать «не 486».
        Task<string> collector = Task.Run(async () =>
        {
            await foreach (SipInviteEvent value in events)
            {
                return Name(value);
            }
            return "поток закрылся без событий";
        });

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Invite)));

        server.Inject(MakeResponse(486));
        Assert.Equal("fail486", await collector);

        // ACK на 3xx–6xx — часть транзакции, и отправить его обязан слой.
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Ack)));

        SipRequest ack = server.ReceivedRequests.Last(request => request.Method == SipMethod.Ack);
        Assert.Equal("z9hG4bKinvite1", ack.TopVia?.Branch);
        Assert.Equal("servertag", ack.To?.Tag);
        Assert.Equal(20, ack.CSeq?.Number);

        await layer.StopAsync();
        layer.Dispose();
    }

    [Fact]
    public async Task Ретрансмиссия_INVITE_до_первого_ответа()
    {
        (SipTransactionLayer layer, ScriptedSipServer server) = await MakeLayerAsync();
        layer.SendInvite(MakeInvite());

        // Таймер A: интервал удваивается без ограничения T2 — в отличие от
        // не-INVITE, где он упирается в T2.
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Count(request => request.Method == SipMethod.Invite) >= 2));

        List<SipRequest> invites = [.. server.ReceivedRequests.Where(request => request.Method == SipMethod.Invite)];
        Assert.Equal(invites[0].TopVia?.Branch, invites[1].TopVia?.Branch);
        Assert.Equal(invites[0].CSeq?.Number, invites[1].CSeq?.Number);

        await layer.StopAsync();
        layer.Dispose();
    }

    [Fact]
    public async Task Первый_1xx_прекращает_ретрансмиссии()
    {
        (SipTransactionLayer layer, ScriptedSipServer server) = await MakeLayerAsync();
        layer.SendInvite(MakeInvite());

        Assert.True(await TestSupport.WaitUntilAsync(() => server.ReceivedRequests.Count > 0));
        server.Inject(MakeResponse(180, contact: null));

        // Даём заведомо больше нескольких интервалов T1.
        await Task.Delay(500);
        int count = server.ReceivedRequests.Count(request => request.Method == SipMethod.Invite);
        Assert.True(count <= 2, $"после 1xx запрос повторять не нужно, отправлено {count}");

        await layer.StopAsync();
        layer.Dispose();
    }

    [Fact]
    public async Task Молчание_сервера_заканчивается_таймаутом_а_гудки_нет()
    {
        (SipTransactionLayer layer, ScriptedSipServer server) = await MakeLayerAsync();

        // Таймер B с быстрыми таймерами — 50 мс * 64 = 3,2 с.
        List<string> names = await CollectAsync(layer.SendInvite(MakeInvite()));
        Assert.Contains("timeout", names);

        // А вот после 1xx таймера быть не должно: гудки могут идти сколько
        // угодно, и обрывать их — решение пользователя, а не стека.
        IAsyncEnumerable<SipInviteEvent> second = layer.SendInvite(MakeInvite("z9hG4bKinvite2"));
        Assert.True(await TestSupport.WaitUntilAsync(() => server.ReceivedRequests.Count >= 2));
        server.Inject(MakeResponse(180, branch: "z9hG4bKinvite2", contact: null));

        using var stillRinging = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        bool timedOut = false;
        try
        {
            await foreach (SipInviteEvent value in second.WithCancellation(stillRinging.Token))
            {
                if (value is SipInviteEvent.Timeout)
                {
                    timedOut = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Именно этого и ждём: поток не закончился сам.
        }

        Assert.False(timedOut, "после 1xx гудки обрывать нельзя");

        await layer.StopAsync();
        layer.Dispose();
    }

    [Fact]
    public async Task CANCEL_отправляется_с_тем_же_branch_и_только_до_финального_ответа()
    {
        (SipTransactionLayer layer, ScriptedSipServer server) = await MakeLayerAsync();
        layer.SendInvite(MakeInvite());

        Assert.True(await TestSupport.WaitUntilAsync(() => server.ReceivedRequests.Count > 0));
        server.Inject(MakeResponse(180, contact: null));

        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Count > 0),
            "ждём, пока 180 доедет");

        Assert.True(await CancelWhenPossibleAsync(layer, "z9hG4bKinvite1"));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Cancel)));

        SipRequest cancel = server.ReceivedRequests.Last(request => request.Method == SipMethod.Cancel);
        Assert.Equal("z9hG4bKinvite1", cancel.TopVia?.Branch);
        Assert.Equal(20, cancel.CSeq?.Number);

        // Отвечаем 487, как это делает сервер после CANCEL.
        server.Inject(MakeResponse(487));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Ack)));

        // Отменять больше нечего.
        Assert.False(await layer.CancelInviteAsync("z9hG4bKinvite1"));

        await layer.StopAsync();
        layer.Dispose();
    }

    [Fact]
    public async Task CANCEL_до_первого_1xx_откладывается_и_уходит_на_нём()
    {
        (SipTransactionLayer layer, ScriptedSipServer server) = await MakeLayerAsync();
        layer.SendInvite(MakeInvite());
        Assert.True(await TestSupport.WaitUntilAsync(() => server.ReceivedRequests.Count > 0));

        // Отбой в первые полсекунды после набора — то есть до всякого ответа
        // сервера. RFC 3261 §9.1: такой CANCEL слать нельзя, сервер мог ещё не
        // завести транзакцию и ответит 481, а INVITE продолжит звонить.
        Assert.True(await layer.CancelInviteAsync("z9hG4bKinvite1"), "наверх это всё равно «отменяем»");

        await Task.Delay(300);
        Assert.DoesNotContain(server.ReceivedRequests, request => request.Method == SipMethod.Cancel);

        server.Inject(MakeResponse(180, contact: null));
        Assert.True(
            await TestSupport.WaitUntilAsync(
                () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Cancel)),
            "а на первом же 1xx — обязан");

        SipRequest cancel = server.ReceivedRequests.Last(request => request.Method == SipMethod.Cancel);
        Assert.Equal("z9hG4bKinvite1", cancel.TopVia?.Branch);

        await layer.StopAsync();
        layer.Dispose();
    }

    [Fact]
    public async Task Отменённый_INVITE_без_487_закрывается_по_пределу_а_не_висит()
    {
        (SipTransactionLayer layer, ScriptedSipServer server) = await MakeLayerAsync();
        IAsyncEnumerable<SipInviteEvent> events = layer.SendInvite(MakeInvite());
        Assert.True(await TestSupport.WaitUntilAsync(() => server.ReceivedRequests.Count > 0));

        server.Inject(MakeResponse(180, contact: null));
        Assert.True(await CancelWhenPossibleAsync(layer, "z9hG4bKinvite1"));
        Assert.True(await TestSupport.WaitUntilAsync(
            () => server.ReceivedRequests.Any(request => request.Method == SipMethod.Cancel)));

        // 487 не присылаем: ровно это и происходит, когда финальный ответ
        // теряется. На первом 1xx таймер B снят, и без своего предела запись
        // жила бы в таблице до выхода из приложения — вместе с подвешенной на её
        // поток задачей звонка.
        List<string> names = await CollectAsync(events);
        Assert.Contains("timeout", names);

        await layer.StopAsync();
        layer.Dispose();
    }

    [Fact]
    public async Task Отмена_неизвестной_транзакции_ничего_не_ломает()
    {
        (SipTransactionLayer layer, _) = await MakeLayerAsync();
        Assert.False(await layer.CancelInviteAsync("нет-такого"));
        await layer.StopAsync();
        layer.Dispose();
    }

    [Fact]
    public async Task На_надёжном_транспорте_INVITE_не_повторяется()
    {
        (SipTransactionLayer layer, ScriptedSipServer server) = await MakeLayerAsync(SipTransport.Tls);
        layer.SendInvite(MakeInvite());

        await Task.Delay(600);
        int count = server.ReceivedRequests.Count(request => request.Method == SipMethod.Invite);
        Assert.True(count == 1, $"доставку гарантирует TCP, отправлено {count}");

        await layer.StopAsync();
        layer.Dispose();
    }

    /// <summary>
    /// Отмена после 1xx, но 1xx едет через очередь событий, и момент его
    /// прибытия не наблюдаем снаружи. Поэтому пробуем, пока не уйдёт настоящий
    /// CANCEL, а не отложенный.
    /// </summary>
    private static async Task<bool> CancelWhenPossibleAsync(SipTransactionLayer layer, string branch)
    {
        await Task.Delay(50);
        return await layer.CancelInviteAsync(branch);
    }
}
