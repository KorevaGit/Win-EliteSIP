using System.Net.Sockets;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Что бывает с каналом и кто об этом узнаёт.
///
/// Самая дорогая находка аудита оригинала жила ровно здесь: соединение в
/// состоянии отказа не поднималось никаким перезапуском, поток его событий
/// кончался, и об этом никто не узнавал. Регистрация продолжала ходить по кругу
/// с backoff, каждая попытка падала мгновенно, и рабочее место молча выпадало из
/// раздачи лидов до перезапуска приложения — с обещанием «повтор через N с» на
/// экране.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/ChannelLifecycleTests.swift</c>.
/// </summary>
public sealed class ChannelLifecycleTests
{
    private static SipAccount Account(ScriptedSipServer server) => new()
    {
        Username = "100",
        DisplayName = "Agent",
        Domain = server.Remote.Host,
        ServerPort = server.Remote.Port,
        Transport = SipTransport.Udp,
        RegistrationExpires = 300,
    };

    private static SipUserAgent Agent(ScriptedSipServer server) =>
        new(Account(server), new DigestAuthentication.Credentials("100", "секрет"), server);

    private static SipRequest Options()
    {
        var request = new SipRequest(SipMethod.Options, new SipUri("127.0.0.1"));
        var via = new SipVia(SipTransport.Udp, "192.168.1.50", 5060) { Branch = SipToken.Branch() };
        request.Headers.Append(SipHeaderName.Via, via.ToString());
        request.Headers.Append(SipHeaderName.CallId, SipToken.CallId());
        request.Headers.Append(SipHeaderName.CSeq, "1 OPTIONS");
        return request;
    }

    // Закрытие

    [Fact]
    public async Task Закрытие_канала_доходит_до_транзакционного_слоя()
    {
        var server = new ScriptedSipServer((_, _) => null);
        using var layer = new SipTransactionLayer(server);
        await layer.StartAsync();

        Task<string?> closures = Task.Run(async () =>
        {
            await foreach (string reason in layer.ChannelClosures)
            {
                return reason;
            }
            return null;
        });

        server.Close("сервер закрыл соединение");

        Assert.Equal("сервер закрыл соединение", await closures);
        await layer.StopAsync();
    }

    [Fact]
    public async Task Закрытие_канала_доходит_до_приложения_событием_агента()
    {
        var server = new ScriptedSipServer((_, _) => null);
        using SipUserAgent agent = Agent(server);

        Task<string?> closure = Task.Run(async () =>
        {
            await foreach (SipUserAgentEvent value in agent.Events)
            {
                if (value is SipUserAgentEvent.ChannelClosed closed)
                {
                    return closed.Reason;
                }
            }
            return null;
        });

        await agent.StartAsync();

        // Даём регистрации начаться: закрытие посреди живой работы — это тот
        // случай, который и надо поймать.
        await Task.Delay(50);
        server.Close("нет маршрута до 127.0.0.1");

        Assert.Equal("нет маршрута до 127.0.0.1", await closure);

        // И повторы регистрации на мёртвом канале прекращаются: обещать «повтор
        // через N с» там, где повтор ничего не даст, — врать человеку.
        var failed = Assert.IsType<SipRegistrationState.Failed>(agent.RegistrationState);
        Assert.Null(failed.RetryAt);

        await agent.StopAsync();
    }

    [Fact]
    public async Task Закрытие_роняет_запросы_в_пути()
    {
        var server = new ScriptedSipServer((_, _) => null);
        using var layer = new SipTransactionLayer(server);
        await layer.StartAsync();
        await layer.WaitUntilReadyAsync();

        Task<SipResponse> pending = layer.SendAsync(Options());

        await Task.Delay(50);
        server.Close("соединение отказало");

        SipTransactionException error = await Assert.ThrowsAsync<SipTransactionException>(() => pending);
        Assert.Equal(SipTransactionErrorKind.TransportFailed, error.Kind);

        await layer.StopAsync();
    }

    // Отказ, после которого канал жив

    [Fact]
    public async Task Временный_отказ_не_роняет_запрос_в_пути()
    {
        var server = new ScriptedSipServer((_, _) => null);
        using var layer = new SipTransactionLayer(server);
        await layer.StartAsync();
        await layer.WaitUntilReadyAsync();

        Task<SipResponse> pending = layer.SendAsync(Options());

        await Task.Delay(50);

        // Так выглядит подрагивание Wi-Fi: транспорт сообщает об отказе и
        // повторяет попытку сам. Раньше каждый такой отказ обрывал REGISTER,
        // backoff рос, и после перехода между точками доступа регистрация
        // возвращалась не сразу, а следующим его шагом — до пяти минут без
        // входящих.
        server.Fail("нет маршрута");
        await Task.Delay(150);

        Assert.False(pending.IsCompleted, "у транзакции есть свой таймер, он для этого и заведён");

        await layer.StopAsync();
    }

    [Fact]
    public async Task Поднявшийся_канал_переживает_временный_отказ()
    {
        var server = new ScriptedSipServer((request, _) => ScriptedSipServer.Response(request, 200));
        using var layer = new SipTransactionLayer(server);
        await layer.StartAsync();
        await layer.WaitUntilReadyAsync();

        server.Fail("нет маршрута");
        await Task.Delay(50);

        // Локальный адрес известен, и отдавать его надо по-прежнему. Отказывать
        // здесь значило бы запретить набор на время подрагивания Wi-Fi — а
        // транспорт в это время как раз повторяет попытку.
        Assert.Equal(server.Local, await layer.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(100)));

        SipResponse response = await layer.SendAsync(Options());
        Assert.Equal(200, response.StatusCode);

        await layer.StopAsync();
    }

    [Fact]
    public async Task Отказ_до_готовности_называет_причину_а_не_молчит()
    {
        ScriptedSipServer server = ScriptedSipServer.NeverReady();
        using var layer = new SipTransactionLayer(server);
        await layer.StartAsync();

        server.Fail("нет маршрута до 192.168.1.2");
        await Task.Delay(50);

        // Канал ни разу не поднимался, и ждать его молча целый таймаут незачем:
        // причина уже известна, и человеку нужна именно она.
        SipTransactionException error = await Assert.ThrowsAsync<SipTransactionException>(
            () => layer.WaitUntilReadyAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(SipTransactionErrorKind.TransportFailed, error.Kind);
        Assert.Equal("нет маршрута до 192.168.1.2", error.Detail);

        // А когда канал всё-таки поднимется, прошлый отказ забывается.
        server.BecomeReady();
        await Task.Delay(50);
        Assert.Equal(server.Local, await layer.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(100)));

        await layer.StopAsync();
    }

    // Ожидание готовности

    [Fact]
    public async Task Таймаут_одного_ожидающего_не_роняет_остальных()
    {
        ScriptedSipServer server = ScriptedSipServer.NeverReady();
        using var layer = new SipTransactionLayer(server);
        await layer.StartAsync();

        // Первый ждёт недолго, второй — долго. Общий таймер ронял обоих на сроке
        // первого: второй звонок получал чужой таймаут.
        Task<SipEndpoint> impatient = layer.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(150));
        Task<SipEndpoint> patient = layer.WaitUntilReadyAsync(TimeSpan.FromSeconds(5));

        SipTransactionException failure = await Assert.ThrowsAsync<SipTransactionException>(() => impatient);
        Assert.Equal(SipTransactionErrorKind.Timeout, failure.Kind);

        // Второй всё ещё ждёт — и дожидается.
        server.BecomeReady();
        Assert.Equal(server.Local, await patient);

        await layer.StopAsync();
    }
}

/// <summary>
/// Кэш ответов на входящие запросы.
///
/// Он существует ради ретрансмиссий (RFC 3261 §17.2.2) и по природе своей растёт
/// от чужих запросов, а не от нашей работы. Раньше срок каждой записи держала
/// отдельная задача со сном на сорок секунд — по задаче на ответ, — и ни
/// словарь, ни число задач ограничить было нечем.
/// </summary>
public sealed class ResponseCacheTests
{
    private static SipRequest Request(string branch)
    {
        var request = new SipRequest(SipMethod.Options, new SipUri("127.0.0.1"));
        var via = new SipVia(SipTransport.Udp, "192.168.1.2", 5060) { Branch = branch };
        request.Headers.Append(SipHeaderName.Via, via.ToString());
        request.Headers.Append(SipHeaderName.CallId, SipToken.CallId());
        request.Headers.Append(SipHeaderName.CSeq, "1 OPTIONS");
        return request;
    }

    [Fact]
    public async Task Поток_чужих_запросов_не_растит_кэш_без_предела()
    {
        var server = new ScriptedSipServer((_, _) => null);
        using var layer = new SipTransactionLayer(server);
        await layer.StartAsync();

        // Вчетверо больше потолка. Так выглядит не разговор, а поток мусора на
        // открытый порт — и переживать его надо без роста памяти.
        for (int index = 0; index < SipTransactionLayer.MaximumCachedResponses * 4; index++)
        {
            await layer.RespondAsync(Request($"z9hG4bK-flood-{index}"), new SipResponse(200));
        }

        Assert.True(layer.CachedResponseCount <= SipTransactionLayer.MaximumCachedResponses);
        await layer.StopAsync();
    }

    [Fact]
    public async Task Просроченный_ответ_уходит_сам_без_задачи_на_каждую_запись()
    {
        var server = new ScriptedSipServer((_, _) => null);

        // Срок жизни записи — восемь T4. Укорачиваем, чтобы не ждать сорок
        // секунд: проверяется правило, а не боевое число.
        using var layer = new SipTransactionLayer(server, new SipTransactionTimers
        {
            T4 = TimeSpan.FromMilliseconds(10),
        });
        await layer.StartAsync();

        await layer.RespondAsync(Request("z9hG4bK-old"), new SipResponse(200));
        Assert.Equal(1, layer.CachedResponseCount);

        await Task.Delay(200);

        // Следующая запись убирает просроченное — уборка едет на ней, а не на
        // своей задаче.
        await layer.RespondAsync(Request("z9hG4bK-new"), new SipResponse(200));
        Assert.Equal(1, layer.CachedResponseCount);

        await layer.StopAsync();
    }
}

/// <summary>
/// Диагностика отказа подключения.
///
/// Тесты на текст сообщения — не педантизм. Настоящий случай из оригинала:
/// боевой Asterisk слушает незашифрованный SIP на 5060, в настройках выбрали TLS
/// и тот же порт 5060, а приложение написало «ожидание сети» и код ошибки.
/// Человек полчаса проверял сеть, хотя чинилась одна строка в настройках.
///
/// Перенесено из <c>TransportDiagnosticsTests.swift</c>. Коды <c>NWError</c>
/// заменены кодами сокета — это единственное, что здесь изменилось.
/// </summary>
public sealed class TransportDiagnosticsTests
{
    private static SipEndpoint Remote(ushort port) => new("192.168.1.2", port);

    [Fact]
    public void Отказ_TLS_на_порту_обычного_SIP_называет_причину_и_нужный_порт()
    {
        string reason = SipTransportFailureText.DescribeTlsFailure(Remote(5060), "код -9816");

        Assert.Contains("TLS", reason, StringComparison.Ordinal);
        Assert.Contains("5060", reason, StringComparison.Ordinal);
        Assert.Contains("5061", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ожидание сети", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// На штатном порту TLS тот же код означает уже другое: порт верный, а вот
    /// TLS на сервере может быть выключен или сертификат не подходит. Подсказка
    /// про порт здесь была бы враньём.
    /// </summary>
    [Fact]
    public void Отказ_TLS_на_порту_5061_не_советует_менять_порт()
    {
        string reason = SipTransportFailureText.DescribeTlsFailure(Remote(5061), "код -9816");

        Assert.Contains("TLS", reason, StringComparison.Ordinal);
        Assert.Contains("проверьте, включён ли TLS на сервере", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("нужен 5061", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Закрытый_порт_назван_закрытым_а_не_ожиданием_сети()
    {
        string reason = SipTransportFailureText.Describe(
            SocketError.ConnectionRefused,
            Remote(5070),
            SipTransport.Udp);

        Assert.Equal("порт 5070 закрыт: на нём никто не слушает", reason);
    }

    [Fact]
    public void Недоступный_адрес_и_неразрешимое_имя_различаются()
    {
        string unreachable = SipTransportFailureText.Describe(
            SocketError.HostUnreachable, Remote(5060), SipTransport.Udp);
        string dns = SipTransportFailureText.Describe(
            SocketError.HostNotFound, Remote(5060), SipTransport.Udp);

        Assert.Equal("нет маршрута до 192.168.1.2", unreachable);
        Assert.Equal("имя 192.168.1.2 не разрешается", dns);
        Assert.NotEqual(unreachable, dns);
    }

    /// <summary>
    /// Незнакомый код не должен ни падать, ни превращаться в пустую строку: хоть
    /// что-то в журнале лучше, чем ничего.
    /// </summary>
    [Fact]
    public void Незнакомая_ошибка_всё_равно_даёт_непустой_текст() =>
        Assert.NotEmpty(SipTransportFailureText.Describe(SocketError.Fault, Remote(5060), SipTransport.Udp));
}
