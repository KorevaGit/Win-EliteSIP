namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Удержание привязки NAT.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/KeepAliveTests.swift</c>.
/// </summary>
public sealed class KeepAliveTests
{
    private static SipUserAgent MakeAgent(ScriptedSipServer server, TimeSpan? keepAliveInterval = null) =>
        new(
            TestSupport.TestAccount(server.Transport),
            TestSupport.TestCredentials,
            server,
            TestSupport.FastTimers(),
            keepAliveInterval: keepAliveInterval ?? TimeSpan.FromMilliseconds(30));

    private static ScriptedSipServer AcceptingServer(int expires = 3600) =>
        new((request, index) => index == 0
            ? ScriptedSipServer.Unauthorized(request)
            : ScriptedSipServer.RegistrationAccepted(request, expires));

    [Fact]
    public async Task Между_обновлениями_регистрации_уходят_пакеты_удержания()
    {
        // Срок регистрации намеренно длинный: именно в этой паузе NAT и закрывает
        // привязку, и проверять надо, что клиент в ней не молчит.
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));
        Assert.True(await TestSupport.WaitUntilAsync(() => server.KeepAliveCount >= 3));

        await agent.StopAsync();

        // Регистрация за это время не обновлялась: 3600 секунд ещё не прошли.
        // Значит пакеты — не побочный эффект перерегистрации.
        Assert.True(server.ReceivedRequests.Count(request => request.Method == SipMethod.Register) <= 3);
    }

    [Fact]
    public void Пакет_удержания_ровно_CRLFCRLF_и_ничего_больше() =>
        Assert.Equal<byte[]>([0x0D, 0x0A, 0x0D, 0x0A], SipTransactionLayer.KeepAlivePing);

    [Fact]
    public async Task Привязка_удерживается_и_когда_регистрация_не_удалась()
    {
        // Сервер молчит на всё. Регистрация уходит в повтор с нарастающей
        // задержкой, и без отдельного цикла клиент замолчал бы на минуты — то есть
        // ровно тогда, когда открытая дорога нужнее всего: по ней должен прийти
        // ответ на следующую попытку.
        var server = new ScriptedSipServer((_, _) => null);
        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        Assert.True(await TestSupport.WaitUntilAsync(() => server.KeepAliveCount >= 3));
        Assert.False(agent.RegistrationState.IsRegistered);

        await agent.StopAsync();
    }

    [Fact]
    public async Task После_остановки_агента_пакеты_прекращаются()
    {
        ScriptedSipServer server = AcceptingServer();
        using SipUserAgent agent = MakeAgent(server);
        await agent.StartAsync();

        Assert.True(await TestSupport.WaitUntilAsync(() => server.KeepAliveCount >= 2));
        await agent.StopAsync();

        int afterStop = server.KeepAliveCount;

        // Несколько интервалов подряд: остановка обязана снимать задачу, а не
        // просто пропускать один тик.
        await Task.Delay(150);
        Assert.Equal(afterStop, server.KeepAliveCount);
    }

    [Fact]
    public void Интервал_по_умолчанию_зависит_от_транспорта()
    {
        // UDP — под самый короткий распространённый таймаут NAT в 30 секунд.
        Assert.Equal(TimeSpan.FromSeconds(25), SipUserAgent.DefaultKeepAliveInterval(SipTransport.Udp));

        // Потоковым транспортам столь частый пакет не нужен: там привязка живёт
        // кратно дольше, и RFC 5626 §4.4.1 называет как раз этот порядок.
        Assert.Equal(TimeSpan.FromSeconds(120), SipUserAgent.DefaultKeepAliveInterval(SipTransport.Tcp));
        Assert.Equal(TimeSpan.FromSeconds(120), SipUserAgent.DefaultKeepAliveInterval(SipTransport.Tls));
    }

    [Fact]
    public void Разброс_держится_в_пределах_десяти_процентов_и_не_вырождается_в_ноль()
    {
        TimeSpan @base = TimeSpan.FromSeconds(25);
        for (int attempt = 0; attempt < 200; attempt++)
        {
            TimeSpan value = SipUserAgent.Jittered(@base);
            Assert.True(value >= TimeSpan.FromSeconds(22.5));
            Assert.True(value <= TimeSpan.FromSeconds(27.5));
        }
    }

    [Fact]
    public void Нулевой_интервал_разброс_не_ломает()
    {
        // Пустой диапазон случайного числа — ловушка: на интервале меньше десяти
        // тиков разброс обнуляется, и без явной проверки получилось бы исключение
        // вместо тика таймера.
        Assert.Equal(TimeSpan.Zero, SipUserAgent.Jittered(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromTicks(5), SipUserAgent.Jittered(TimeSpan.FromTicks(5)));
    }
}
