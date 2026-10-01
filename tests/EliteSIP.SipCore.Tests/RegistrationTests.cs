namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Регистрация: вызов 401, внешний адрес, обновление, отказы и снятие.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/RegistrationTests.swift</c>.
/// </summary>
public sealed class RegistrationTests
{
    private static SipUserAgent MakeAgent(ScriptedSipServer server, SipAccount? account = null) =>
        new(
            account ?? TestSupport.TestAccount(server.Transport),
            TestSupport.TestCredentials,
            server,
            TestSupport.FastTimers());

    private static Task<bool> WaitForFailureAsync(SipUserAgent agent) =>
        TestSupport.WaitUntilAsync(() => agent.RegistrationState is SipRegistrationState.Failed);

    [Fact]
    public async Task Проходит_вызов_401_и_регистрируется()
    {
        var server = new ScriptedSipServer((request, index) => index == 0
            ? ScriptedSipServer.Unauthorized(request)
            : ScriptedSipServer.RegistrationAccepted(request, 300));

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));
        await agent.StopAsync();

        IReadOnlyList<SipRequest> requests = server.ReceivedRequests;
        Assert.True(requests.Count >= 2);

        // Первый запрос идёт без авторизации: пароль отдаём только в ответ на
        // конкретный nonce, а не всем, кто попросит.
        Assert.Null(requests[0].Headers.First("Authorization"));

        string? authorization = requests[1].Headers.First("Authorization");
        Assert.NotNull(authorization);
        Assert.Contains("Digest ", authorization, StringComparison.Ordinal);
        Assert.Contains("username=\"100\"", authorization, StringComparison.Ordinal);
        Assert.Contains("realm=\"asterisk\"", authorization, StringComparison.Ordinal);
        Assert.Contains("nonce=\"1234abcd\"", authorization, StringComparison.Ordinal);
        Assert.Contains("uri=\"sip:127.0.0.1\"", authorization, StringComparison.Ordinal);

        // CSeq обязан расти: повтор с тем же номером Asterisk сочтёт
        // ретрансмиссией и ответит тем же 401.
        Assert.Equal(requests[0].CSeq?.Number + 1, requests[1].CSeq?.Number);

        // Call-ID и tag в серии регистраций не меняются.
        Assert.Equal(requests[0].CallId, requests[1].CallId);
        Assert.Equal(requests[0].From?.Tag, requests[1].From?.Tag);
    }

    [Fact]
    public async Task Contact_берёт_внешний_адрес_из_received_и_rport()
    {
        // Ключевая проверка для удалённых сотрудников: за NAT локальный адрес в
        // Contact означает «регистрация есть, звонки не приходят».
        var observed = new SipEndpoint("203.0.113.7", 41234);
        var server = new ScriptedSipServer((request, index) => index == 0
            ? ScriptedSipServer.Unauthorized(request, observedAddress: observed)
            : ScriptedSipServer.RegistrationAccepted(request, observedAddress: observed));

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));
        await agent.StopAsync();

        IReadOnlyList<SipRequest> requests = server.ReceivedRequests;

        // До ответа сервера знаем только локальный адрес.
        Assert.Equal("192.168.1.50", requests[0].Contacts[0].Uri.Host);

        // Внешний адрес узнаём из ЛЮБОГО ответа, включая 401: повтор с
        // авторизацией уже несёт правильный Contact, и лишнего круга нет.
        Assert.Equal("203.0.113.7", requests[1].Contacts[0].Uri.Host);
        Assert.Equal((ushort?)41234, requests[1].Contacts[0].Uri.Port);

        // Via при этом остаётся с локальным адресом: это адрес отправителя, а не
        // тот, которым нас видно.
        Assert.Equal("192.168.1.50", requests[1].TopVia?.Host);
        Assert.True(requests[1].TopVia?.HasParameter("rport"));
    }

    [Fact]
    public async Task Повторная_регистрация_несёт_авторизацию_сразу()
    {
        var server = new ScriptedSipServer((request, index) => index == 0
            ? ScriptedSipServer.Unauthorized(request)
            : ScriptedSipServer.RegistrationAccepted(request));

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));

        await agent.ReregisterNowAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => server.ReceivedRequests.Count >= 3));
        await agent.StopAsync();

        // Третий запрос — уже обновление. Оно должно нести Authorization без
        // нового круга 401, иначе каждое обновление удваивает трафик.
        string? authorization = server.ReceivedRequests[2].Headers.First("Authorization");
        Assert.NotNull(authorization);
        Assert.Contains("nonce=\"1234abcd\"", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ответ_423_повторяется_с_MinExpires()
    {
        var server = new ScriptedSipServer((request, index) => index switch
        {
            0 => ScriptedSipServer.Unauthorized(request),
            1 => ScriptedSipServer.Response(request, 423, extraHeaders: (SipHeaderName.MinExpires, "600")),
            _ => ScriptedSipServer.RegistrationAccepted(request, 600),
        });

        using SipUserAgent agent = MakeAgent(server, TestSupport.TestAccount(expires: 120));
        await agent.StartAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));
        await agent.StopAsync();

        IReadOnlyList<SipRequest> requests = server.ReceivedRequests;
        Assert.Equal(120, requests[1].Expires);
        Assert.Equal(600, requests[2].Expires);
    }

    [Fact]
    public async Task Ответ_403_объясняется_человеческим_языком_и_не_повторяется_бесконечно()
    {
        var server = new ScriptedSipServer((request, index) => index == 0
            ? ScriptedSipServer.Unauthorized(request)
            : ScriptedSipServer.Response(request, 403));

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        Assert.True(await WaitForFailureAsync(agent));
        var failed = Assert.IsType<SipRegistrationState.Failed>(agent.RegistrationState);
        await agent.StopAsync();

        Assert.Equal(
            new SipRegistrationException(SipRegistrationErrorKind.Rejected, 403, "Forbidden").Message,
            failed.Reason);
    }

    [Fact]
    public async Task Повторный_401_на_уже_подписанный_запрос_это_неверный_пароль()
    {
        // Asterisk с alwaysauthreject=yes на неверный пароль отвечает не 403, а
        // тем же 401, чтобы не выдавать, существует ли номер. Если не распознать
        // это, самая частая реальная ошибка выглядит как «слишком много попыток».
        var server = new ScriptedSipServer((request, index) =>
            ScriptedSipServer.Unauthorized(request, $"nonce-{index}"));

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        Assert.True(await WaitForFailureAsync(agent));
        var failed = Assert.IsType<SipRegistrationState.Failed>(agent.RegistrationState);
        await agent.StopAsync();

        Assert.Equal(
            new SipRegistrationException(SipRegistrationErrorKind.AuthenticationFailed).Message,
            failed.Reason);
        Assert.True(server.ReceivedRequests.Count <= 3, "не должно долбить сервер по кругу");
    }

    [Fact]
    public async Task Снятие_регистрации_тоже_проходит_авторизацию()
    {
        // Первый REGISTER, вызов, успех — а на снятии сервер выдаёт НОВЫЙ nonce.
        // Без обработки этого 401 пир остаётся зарегистрированным до истечения.
        var server = new ScriptedSipServer((request, index) => index switch
        {
            0 => ScriptedSipServer.Unauthorized(request, "first"),
            1 => ScriptedSipServer.RegistrationAccepted(request),
            2 => ScriptedSipServer.Unauthorized(request, "second"),
            _ => ScriptedSipServer.RegistrationAccepted(request, 0),
        });

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));
        await agent.StopAsync();

        IReadOnlyList<SipRequest> requests = server.ReceivedRequests;
        Assert.True(requests.Count >= 4, "снятие должно быть повторено с новым вызовом");

        List<SipRequest> unregisters = [.. requests.Where(request => request.Expires == 0)];
        Assert.Equal(2, unregisters.Count);

        string? authorized = unregisters[^1].Headers.First("Authorization");
        Assert.NotNull(authorized);
        Assert.Contains("nonce=\"second\"", authorized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task На_UDP_запрос_повторяется_если_ответа_нет()
    {
        // Потеря одной датаграммы не должна ронять регистрацию — иначе она будет
        // случайным образом отваливаться на плохой сети.
        var server = new ScriptedSipServer((request, index) =>
            index == 0 ? null : ScriptedSipServer.Unauthorized(request));

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => server.ReceivedRequests.Count >= 2));
        await agent.StopAsync();

        IReadOnlyList<SipRequest> requests = server.ReceivedRequests;
        Assert.Equal(requests[0].CSeq?.Number, requests[1].CSeq?.Number);
        Assert.Equal(requests[0].TopVia?.Branch, requests[1].TopVia?.Branch);
    }

    [Fact]
    public async Task На_TLS_ретрансмиссий_нет()
    {
        // Доставку гарантирует TCP. Повтор сервер воспримет как новый запрос.
        var server = new ScriptedSipServer((_, _) => null, SipTransport.Tls);

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        // Ждём дольше, чем несколько интервалов T1, но меньше таймера F.
        await Task.Delay(700);
        int count = server.ReceivedRequests.Count;
        await agent.StopAsync();

        Assert.True(count == 1, $"на надёжном транспорте запрос отправляется один раз, отправлено {count}");
    }

    [Fact]
    public async Task Молчание_сервера_заканчивается_понятной_ошибкой()
    {
        var server = new ScriptedSipServer((_, _) => null);

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        // Таймер F с быстрыми таймерами — 50 мс * 64 = 3,2 с.
        Assert.True(await TestSupport.WaitUntilAsync(
            () => agent.RegistrationState is SipRegistrationState.Failed { RetryAt: not null },
            TimeSpan.FromSeconds(8)));

        var failed = Assert.IsType<SipRegistrationState.Failed>(agent.RegistrationState);
        await agent.StopAsync();

        Assert.Equal("сервер не ответил", failed.Reason);
    }

    [Fact]
    public async Task Обрыв_транспорта_не_оставляет_регистрацию_в_подвешенном_состоянии()
    {
        var server = new ScriptedSipServer((_, _) => null);
        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        Assert.True(await TestSupport.WaitUntilAsync(() => server.ReceivedRequests.Count >= 1));
        server.Fail("сеть недоступна");

        Assert.True(await WaitForFailureAsync(agent));
        await agent.StopAsync();
    }

    [Fact]
    public async Task Снятие_регистрации_отправляет_нулевой_срок()
    {
        var server = new ScriptedSipServer((request, index) => index == 0
            ? ScriptedSipServer.Unauthorized(request)
            : ScriptedSipServer.RegistrationAccepted(request));

        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));
        await agent.StopAsync();

        SipRequest last = server.ReceivedRequests[^1];
        Assert.Equal(SipMethod.Register, last.Method);
        Assert.Equal(0, last.Expires);
    }

    [Fact]
    public void Интервал_обновления_заведомо_раньше_истечения()
    {
        // Обновляться в последнюю секунду нельзя: одна потерянная датаграмма
        // оставит клиента без регистрации, и входящие пропадут.
        Assert.Equal(270, SipUserAgent.RefreshInterval(300));
        Assert.Equal(30, SipUserAgent.RefreshInterval(60));
        Assert.Equal(20, SipUserAgent.RefreshInterval(40));
        Assert.Equal(15, SipUserAgent.RefreshInterval(20));
        Assert.Equal(5, SipUserAgent.RefreshInterval(10));
        Assert.Equal(30, SipUserAgent.RefreshInterval(0));

        for (int expires = 1; expires <= 600; expires++)
        {
            int interval = SipUserAgent.RefreshInterval(expires);
            Assert.True(interval > 0);
            Assert.True(interval < expires || expires <= 10, $"срок {expires} обновляется через {interval}");
        }
    }

    [Fact]
    public void Откат_растёт_и_упирается_в_потолок()
    {
        Assert.Equal(5, SipUserAgent.BackoffDelay(1));
        Assert.Equal(10, SipUserAgent.BackoffDelay(2));
        Assert.Equal(20, SipUserAgent.BackoffDelay(3));
        Assert.Equal(40, SipUserAgent.BackoffDelay(4));
        Assert.Equal(80, SipUserAgent.BackoffDelay(5));
        Assert.Equal(160, SipUserAgent.BackoffDelay(6));
        Assert.Equal(300, SipUserAgent.BackoffDelay(7));
        Assert.Equal(300, SipUserAgent.BackoffDelay(100));
    }

    [Fact]
    public void Сервер_молчит_повтор_не_реже_потолка_недоступности()
    {
        var silence = new SipTransactionException(SipTransactionErrorKind.Timeout, "сервер не ответил");

        Assert.Equal(5, SipUserAgent.RetryDelay(1, silence));
        Assert.Equal(10, SipUserAgent.RetryDelay(2, silence));
        Assert.Equal(SipUserAgent.UnreachableBackoffLimit, SipUserAgent.RetryDelay(3, silence));
        Assert.Equal(SipUserAgent.UnreachableBackoffLimit, SipUserAgent.RetryDelay(100, silence));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(480)]
    [InlineData(500)]
    [InlineData(503)]
    public void Временный_отказ_сервера_как_недоступность(int status)
    {
        var refusal = new SipRegistrationException(SipRegistrationErrorKind.Rejected, status, "временно");

        Assert.Equal(SipUserAgent.UnreachableBackoffLimit, SipUserAgent.RetryDelay(100, refusal));
    }

    [Theory]
    [InlineData(SipRegistrationErrorKind.AuthenticationFailed, 0)]
    [InlineData(SipRegistrationErrorKind.Rejected, 403)]
    [InlineData(SipRegistrationErrorKind.Rejected, 404)]
    [InlineData(SipRegistrationErrorKind.TooManyAttempts, 0)]
    public void Окончательный_отказ_сервера_откатывается_долго(SipRegistrationErrorKind kind, int status)
    {
        // Неверный пароль раз в 15 с — это бан fail2ban на весь офис.
        var refusal = new SipRegistrationException(kind, status, "отказ");

        Assert.Equal(300, SipUserAgent.RetryDelay(100, refusal));
        Assert.Equal(40, SipUserAgent.RetryDelay(4, refusal));
    }

    /// <summary>
    /// Срок из ответа сервера идёт прямо в интервал сна. Очень большое значение
    /// уводит обновление регистрации на годы, и софтфон молча перестаёт принимать
    /// вызовы, считая себя на линии.
    /// </summary>
    [Fact]
    public void Срок_регистрации_из_ответа_ограничен_разумными_рамками()
    {
        Assert.Equal(300, SipUserAgent.SanitizedExpires(300, 300));
        Assert.Equal(0, SipUserAgent.SanitizedExpires(0, 0));

        Assert.Equal(SipUserAgent.MaximumExpires, SipUserAgent.SanitizedExpires(int.MaxValue, 300));
        Assert.Equal(SipUserAgent.MaximumExpires, SipUserAgent.SanitizedExpires(100_000_000, 300));

        // Отрицательного срока не бывает: берём то, что просили сами.
        Assert.Equal(300, SipUserAgent.SanitizedExpires(-5, 300));
        Assert.Equal(300, SipUserAgent.SanitizedExpires(int.MinValue, 300));

        // И собственная настройка тоже не должна уводить сон в годы.
        Assert.Equal(SipUserAgent.MaximumExpires, SipUserAgent.SanitizedExpires(-1, int.MaxValue));
    }

    /// <summary>
    /// Прямая проверка того, ради чего нужен предел: интервал сна после
    /// ограничения обязан переводиться во время без переполнения.
    /// </summary>
    [Fact]
    public void Ограниченный_срок_безопасно_становится_интервалом()
    {
        int granted = SipUserAgent.SanitizedExpires(int.MaxValue, 300);
        int refresh = SipUserAgent.RefreshInterval(granted);

        Assert.True(refresh > 0);
        Assert.True(TimeSpan.FromSeconds(refresh) > TimeSpan.Zero);
    }
}
