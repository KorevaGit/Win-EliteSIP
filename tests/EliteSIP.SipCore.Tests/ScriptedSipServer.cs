using System.Threading.Channels;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Поддельный сервер вместо сети.
///
/// Существует ради того, чтобы гонять сценарии регистрации целиком — с вызовом
/// 401, ретрансмиссиями и правкой Contact — за миллисекунды и без Asterisk.
/// Живой сервер проверяет совместимость, а этот — логику.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/ScriptedSIPServer.swift</c>.
/// </summary>
internal sealed class ScriptedSipServer : ISipTransportChannel
{
    /// <summary>
    /// Что ответить на очередной запрос. <see langword="null"/> — промолчать
    /// (проверка ретрансмиссий и таймаутов). Второй аргумент — номер запроса,
    /// начиная с 0.
    /// </summary>
    internal delegate SipResponse? Responder(SipRequest request, int index);

    private readonly Channel<SipTransportEvent> _events =
        Channel.CreateUnbounded<SipTransportEvent>();

    private readonly Responder _responder;
    private readonly Lock _gate = new();
    private readonly List<SipRequest> _receivedRequests = [];
    private readonly List<SipResponse> _sentResponses = [];
    private int _keepAliveCount;

    public ScriptedSipServer(
        Responder responder,
        SipTransport transport = SipTransport.Udp,
        SipEndpoint? remote = null,
        SipEndpoint? local = null)
    {
        _responder = responder;
        Transport = transport;
        Remote = remote ?? new SipEndpoint("127.0.0.1", 5060);
        Local = local ?? new SipEndpoint("192.168.1.50", 5060);
    }

    public SipTransport Transport { get; }

    public SipEndpoint Remote { get; }

    public SipEndpoint Local { get; }

    public IAsyncEnumerable<SipTransportEvent> Events => _events.Reader.ReadAllAsync();

    /// <summary>Не объявлять готовность в <see cref="StartAsync"/>. Только для проверок ожидания.</summary>
    public bool StaysSilentOnStart { get; set; }

    /// <summary>Запросы, которые прислал клиент.</summary>
    public IReadOnlyList<SipRequest> ReceivedRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _receivedRequests];
            }
        }
    }

    /// <summary>Ответы, которые прислал клиент (например на OPTIONS).</summary>
    public IReadOnlyList<SipResponse> SentResponses
    {
        get
        {
            lock (_gate)
            {
                return [.. _sentResponses];
            }
        }
    }

    /// <summary>Сколько пустых пакетов удержания NAT прислал клиент.</summary>
    public int KeepAliveCount
    {
        get
        {
            lock (_gate)
            {
                return _keepAliveCount;
            }
        }
    }

    public Task StartAsync()
    {
        if (!StaysSilentOnStart)
        {
            BecomeReady();
        }
        return Task.CompletedTask;
    }

    /// <summary>Объявляет готовность вручную — для тех, кто её так и не дождался.</summary>
    public void BecomeReady() => _events.Writer.TryWrite(new SipTransportEvent.Ready(Local));

    public Task SendAsync(ReadOnlyMemory<byte> data)
    {
        // Пакет удержания NAT — не сообщение: ни метода, ни заголовков, только
        // CRLFCRLF. Разбирать его парсером нечем, поэтому отделяем до разбора,
        // как это делает и настоящий сервер.
        bool onlyLineBreaks = data.Length > 0;
        foreach (byte value in data.Span)
        {
            if (value is not (0x0D or 0x0A))
            {
                onlyLineBreaks = false;
                break;
            }
        }

        if (onlyLineBreaks)
        {
            lock (_gate)
            {
                _keepAliveCount++;
            }
            return Task.CompletedTask;
        }

        SipMessage message = SipParser.Parse(data);

        if (message.AsResponse is SipResponse response)
        {
            lock (_gate)
            {
                _sentResponses.Add(response);
            }
            return Task.CompletedTask;
        }

        SipRequest request = message.AsRequest!;
        int index;
        lock (_gate)
        {
            _receivedRequests.Add(request);
            index = _receivedRequests.Count - 1;
        }

        if (_responder(request, index) is SipResponse scripted)
        {
            Inject(scripted);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _events.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <summary>Присылает клиенту запрос — так проверяются OPTIONS и входящий INVITE.</summary>
    public void Inject(SipRequest request) =>
        _events.Writer.TryWrite(new SipTransportEvent.Received(request.Encoded()));

    /// <summary>
    /// Присылает клиенту ответ отдельно от отвечающей функции.
    ///
    /// Нужно для INVITE: там на один запрос приходит несколько ответов (100,
    /// 180, потом 200), и выдать их одним возвратом из замыкания нельзя.
    /// </summary>
    public void Inject(SipResponse response) =>
        _events.Writer.TryWrite(new SipTransportEvent.Received(response.Encoded()));

    /// <summary>Отказ, после которого канал жив: сетевая подсистема повторит сама.</summary>
    public void Fail(string reason) => _events.Writer.TryWrite(new SipTransportEvent.Failed(reason));

    /// <summary>Канал закрылся насовсем — так выглядит закрытое сервером TCP-соединение.</summary>
    public void Close(string reason)
    {
        _events.Writer.TryWrite(new SipTransportEvent.Closed(reason));
        _events.Writer.TryComplete();
    }

    /// <summary>
    /// Канал так и не поднялся: готовность не приходит вовсе.
    ///
    /// Отдельным способом создания, потому что обычный сервер объявляет себя
    /// готовым в <see cref="StartAsync"/>, а проверять ожидание готовности надо
    /// на том, который этого не делает.
    /// </summary>
    public static ScriptedSipServer NeverReady() =>
        new((_, _) => null) { StaysSilentOnStart = true };

    // Сборка ответов

    /// <summary>
    /// Ответ с корректно скопированными заголовками, как это делает Asterisk,
    /// включая дописывание received и rport в верхний Via.
    /// </summary>
    public static SipResponse Response(
        SipRequest request,
        int status,
        string toTag = "as1a2b3c",
        SipEndpoint? observedAddress = null,
        bool withObservedAddress = true,
        params (string Name, string Value)[] extraHeaders)
    {
        SipEndpoint observed = observedAddress ?? new SipEndpoint("192.168.65.1", 54321);
        var headers = new SipHeaders();

        if (request.TopVia is SipVia via)
        {
            if (withObservedAddress)
            {
                via.SetParameter("received", observed.Host);
                via.SetParameter("rport", observed.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            headers.Append(SipHeaderName.Via, via.ToString());
        }

        if (request.Headers.First(SipHeaderName.From) is string from)
        {
            headers.Append(SipHeaderName.From, from);
        }
        if (request.To is NameAddress to)
        {
            to.Tag = toTag;
            headers.Append(SipHeaderName.To, to.ToString());
        }
        if (request.Headers.First(SipHeaderName.CallId) is string callId)
        {
            headers.Append(SipHeaderName.CallId, callId);
        }
        if (request.Headers.First(SipHeaderName.CSeq) is string cseq)
        {
            headers.Append(SipHeaderName.CSeq, cseq);
        }
        foreach ((string name, string value) in extraHeaders)
        {
            headers.Append(name, value);
        }

        return new SipResponse(status, headers: headers);
    }

    /// <summary>401 в том виде, в каком его шлёт chan_sip.</summary>
    public static SipResponse Unauthorized(
        SipRequest request,
        string nonce = "1234abcd",
        string realm = "asterisk",
        bool withObservedAddress = true) =>
        Response(
            request,
            401,
            withObservedAddress: withObservedAddress,
            extraHeaders: (SipHeaderName.WwwAuthenticate, $"Digest algorithm=MD5, realm=\"{realm}\", nonce=\"{nonce}\""));

    /// <summary>200 OK на REGISTER с подтверждённым сроком в параметре Contact.</summary>
    public static SipResponse RegistrationAccepted(
        SipRequest request,
        int expires = 300,
        bool withObservedAddress = true)
    {
        string contact;
        if (request.Contacts.Count > 0)
        {
            NameAddress copy = request.Contacts[0].Clone();
            copy.SetParameter("expires", expires.ToString(System.Globalization.CultureInfo.InvariantCulture));
            contact = copy.ToString();
        }
        else
        {
            contact = $"<sip:100@127.0.0.1>;expires={expires.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        return Response(
            request,
            200,
            withObservedAddress: withObservedAddress,
            extraHeaders: (SipHeaderName.Contact, contact));
    }
}

internal static class TestSupport
{
    /// <summary>
    /// Ждёт выполнения условия, опрашивая его часто.
    ///
    /// В тестах асинхронных машин состояний фиксированный сон — источник как
    /// ложных падений, так и медленных прогонов. Опрос по условию быстрее и
    /// стабильнее.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        long deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(5).ConfigureAwait(false);
        }
        return condition();
    }

    /// <summary>
    /// Быстрые таймеры: те же пропорции RFC, но в десять раз короче, чтобы
    /// проверки ретрансмиссий и таймаутов занимали доли секунды.
    /// </summary>
    public static SipTransactionTimers FastTimers() => new()
    {
        T1 = TimeSpan.FromMilliseconds(50),
        T2 = TimeSpan.FromMilliseconds(400),
        T4 = TimeSpan.FromMilliseconds(500),

        // Таймер D по RFC — 32 секунды. В тестах он держал бы поток событий
        // открытым столько же, и прогон вырастал с семи секунд до тридцати двух.
        CompletedLifetime = TimeSpan.FromMilliseconds(300),
    };

    public static SipAccount TestAccount(SipTransport transport = SipTransport.Udp, int expires = 300) => new()
    {
        Username = "100",
        DisplayName = "Agent 100",
        Domain = "127.0.0.1",
        Transport = transport,
        RegistrationExpires = expires,
    };

    public static readonly DigestAuthentication.Credentials TestCredentials = new("100", "elite100");
}
