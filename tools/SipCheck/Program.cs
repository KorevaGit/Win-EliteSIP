using System.Globalization;
using EliteSIP.SipCore;
using EliteSIP.SipCore.Udp;

namespace EliteSIP.Tools.SipCheck;

/// <summary>
/// Консольная проверка SipCore против живого Asterisk.
///
/// Нужна потому, что модульные тесты проверяют логику, а совместимость — нет.
/// chan_sip придирчив к деталям (Contact, rport, форма Authorization), и увидеть
/// это можно только на настоящем сервере. Интерфейс для такой проверки — лишний
/// слой: здесь виден весь обмен и точный код ответа.
///
/// На этапе W2 стенд умеет ровно то, чем этап и принимается: регистрацию с
/// digest-вызовом, обновление по таймеру и ответы на опрос сервера. Звонок,
/// удержание и перевод приедут сюда вместе с медиа — этапы W3–W5, — потому что
/// без RTP «позвонил» проверить нечем.
///
/// Примеры:
///   dotnet run --project tools/SipCheck -- --user 100 --password elite100
///   dotnet run --project tools/SipCheck -- --user 100 --password elite100 --host 10.0.0.1 --duration 120
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        var options = CommandLineOptions.Parse(arguments);

        if (options.User is not string user || options.Password is not string password)
        {
            Console.WriteLine(Usage);
            return 2;
        }

        SipTransport transport = options.Transport;
        string host = options.Host;
        ushort port = options.Port ?? transport.DefaultPort();

        var account = new SipAccount
        {
            Username = user,
            DisplayName = "sipcheck",
            Domain = host,
            ServerPort = port,
            Transport = transport,
            RegistrationExpires = options.Expires,
        };

        using var channel = new SocketSipTransport(account.SignalingEndpoint, transport);
        using var agent = new SipUserAgent(
            account,
            new DigestAuthentication.Credentials(user, password),
            channel);

        Console.WriteLine(
            $"-> {account.SignalingEndpoint} по {transport.ProtocolName()}, номер {user}, "
                + $"держим {options.Duration.ToString(CultureInfo.InvariantCulture)} с");

        using var stopping = new CancellationTokenSource();
        Task printer = PrintEventsAsync(agent, stopping.Token);

        await agent.StartAsync();

        // Ждём регистрации: без неё проверять дальше нечего.
        if (!await WaitForRegistrationAsync(agent, TimeSpan.FromSeconds(15)))
        {
            Console.WriteLine($"[x] регистрация не прошла: {Describe(agent.RegistrationState)}");
            await ShutdownAsync(agent, stopping, printer);
            return 1;
        }

        Console.WriteLine("[v] регистрация прошла");

        // Держим регистрацию: за это время должны пройти обновление по таймеру и
        // ответы на опрос сервера. Разрыв сети проверяется руками — выдернуть
        // кабель и посмотреть, поднимется ли регистрация сама.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(options.Duration);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        bool stillRegistered = agent.RegistrationState.IsRegistered;
        Console.WriteLine(stillRegistered
            ? "[v] регистрация дожила до конца проверки"
            : $"[x] регистрация потеряна: {Describe(agent.RegistrationState)}");

        await ShutdownAsync(agent, stopping, printer);
        return stillRegistered ? 0 : 1;
    }

    private const string Usage = """
        Использование: SipCheck --user <номер> --password <пароль> [опции]

          --host <адрес>        по умолчанию 127.0.0.1
          --port <порт>         по умолчанию 5060
          --transport udp|tcp   по умолчанию udp; tls появится на этапе W11
          --expires <секунды>   запрашиваемый срок регистрации, по умолчанию 120
          --duration <секунды>  сколько держать регистрацию, по умолчанию 10
        """;

    private static async Task<bool> WaitForRegistrationAsync(SipUserAgent agent, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (agent.RegistrationState.IsRegistered)
            {
                return true;
            }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task PrintEventsAsync(SipUserAgent agent, CancellationToken token)
    {
        try
        {
            await foreach (SipUserAgentEvent value in agent.Events.WithCancellation(token))
            {
                switch (value)
                {
                    case SipUserAgentEvent.Registration registration:
                        Console.WriteLine($"   состояние: {Describe(registration.State)}");
                        break;

                    case SipUserAgentEvent.Log log:
                        Console.WriteLine($"   [{log.Level.ToString().ToLowerInvariant()}] {log.Message}");
                        break;

                    case SipUserAgentEvent.IncomingCall incoming:
                        // Принять его нечем: медиа приедет этапами W3–W5. Пока
                        // отвечаем отказом, чтобы вызов вернулся в очередь.
                        Console.WriteLine(
                            $"<- входящий от {incoming.Call.DisplayNumber} на {incoming.Call.CalledNumber},"
                                + " отклоняем: звук появится на этапе W4");
                        await agent.RejectIncomingCallAsync(incoming.Call.CallId);
                        break;

                    case SipUserAgentEvent.UnsupportedRequest unsupported:
                        Console.WriteLine($"   отклонён запрос {unsupported.Method.Name()}");
                        break;

                    case SipUserAgentEvent.ChannelClosed closed:
                        // Дальше ничего не будет: канал мёртв, а пересобрать его
                        // инструменту нечем — в приложении это делает оболочка.
                        Console.WriteLine($"   [x] канал закрыт: {closed.Reason}");
                        break;

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ShutdownAsync(
        SipUserAgent agent,
        CancellationTokenSource stopping,
        Task printer)
    {
        await agent.StopAsync();
        await stopping.CancelAsync();
        try
        {
            await printer;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Describe(SipRegistrationState state) => state switch
    {
        SipRegistrationState.Idle => "не подключён",
        SipRegistrationState.Registering => "регистрируется",
        SipRegistrationState.Registered registered =>
            $"на линии до {registered.ExpiresAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}, "
                + $"Contact {registered.Contact}",
        SipRegistrationState.Unregistering => "снимает регистрацию",
        SipRegistrationState.Failed failed => failed.RetryAt is DateTimeOffset retry
            ? $"отказ: {failed.Reason}; повтор в "
                + retry.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : $"отказ: {failed.Reason}",
        _ => state.ToString(),
    };

    /// <summary>Разбор аргументов командной строки. Своими силами: одна зависимость ради шести ключей не окупается.</summary>
    private sealed record CommandLineOptions
    {
        public string? User { get; init; }

        public string? Password { get; init; }

        public string Host { get; init; } = "127.0.0.1";

        public ushort? Port { get; init; }

        public SipTransport Transport { get; init; } = SipTransport.Udp;

        public int Expires { get; init; } = 120;

        public double Duration { get; init; } = 10;

        public static CommandLineOptions Parse(string[] arguments)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);

            for (int index = 0; index < arguments.Length; index++)
            {
                if (!arguments[index].StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                string key = arguments[index][2..];
                string? value = index + 1 < arguments.Length
                    && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? arguments[++index]
                    : null;

                values[key] = value ?? string.Empty;
            }

            return new CommandLineOptions
            {
                User = values.GetValueOrDefault("user") is { Length: > 0 } user ? user : null,
                Password = values.GetValueOrDefault("password") is { Length: > 0 } password ? password : null,
                Host = values.GetValueOrDefault("host") is { Length: > 0 } host ? host : "127.0.0.1",
                Port = values.GetValueOrDefault("port") is { Length: > 0 } port
                    && ushort.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out ushort parsedPort)
                        ? parsedPort
                        : null,
                Transport = values.GetValueOrDefault("transport") is { Length: > 0 } transport
                    && SipTransportExtensions.Parse(transport) is SipTransport parsedTransport
                        ? parsedTransport
                        : SipTransport.Udp,
                Expires = values.GetValueOrDefault("expires") is { Length: > 0 } expires
                    && int.TryParse(expires, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedExpires)
                        ? parsedExpires
                        : 120,
                Duration = values.GetValueOrDefault("duration") is { Length: > 0 } duration
                    && double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDuration)
                        ? parsedDuration
                        : 10,
            };
        }
    }
}
