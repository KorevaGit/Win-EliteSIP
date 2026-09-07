using System.Globalization;
using System.Threading.Channels;

namespace EliteSIP.SipCore;

public enum SipTransactionErrorKind
{
    /// <summary>Истёк таймер F (не-INVITE) или B (INVITE): 64*T1, по умолчанию 32 с.</summary>
    Timeout,
    TransportFailed,
    Cancelled,
    NotReady,
    UnknownTransaction,
}

public sealed class SipTransactionException : Exception
{
    public SipTransactionException()
        : this(SipTransactionErrorKind.NotReady, string.Empty)
    {
    }

    public SipTransactionException(string message)
        : this(SipTransactionErrorKind.NotReady, message)
    {
    }

    public SipTransactionException(string message, Exception innerException)
        : base(message, innerException) => Detail = message;

    public SipTransactionException(SipTransactionErrorKind kind, string detail = "")
        : base(detail.Length == 0 ? kind.ToString() : $"{kind}: {detail}")
    {
        Kind = kind;
        Detail = detail;
    }

    public SipTransactionErrorKind Kind { get; }

    public string Detail { get; } = string.Empty;
}

/// <summary>Таймеры RFC 3261 §17. Меняются только тестами и только в сторону укорочения.</summary>
public sealed class SipTransactionTimers
{
    /// <summary>Оценка RTT. Начальный интервал ретрансмиссий.</summary>
    public TimeSpan T1 { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Максимальный интервал ретрансмиссий.</summary>
    public TimeSpan T2 { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Максимальное время жизни сообщения в сети.</summary>
    public TimeSpan T4 { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Таймер D: сколько держать завершённую INVITE-транзакцию, чтобы поглощать
    /// ретрансмиссии неуспешного ответа.
    ///
    /// По RFC 3261 §17.1.1.2 он не выводится из T1, а задаётся отдельно и должен
    /// быть не меньше 32 секунд. Хранимое свойство, а не вычисляемое, именно
    /// поэтому — и чтобы тесты могли его укоротить.
    /// </summary>
    public TimeSpan CompletedLifetime { get; set; } = TimeSpan.FromSeconds(32);

    /// <summary>Таймер F для не-INVITE и таймер B для INVITE.</summary>
    public TimeSpan TransactionTimeout => T1 * 64;
}

/// <summary>
/// Слой транзакций: клиентские не-INVITE (RFC 3261 §17.1.2), клиентские INVITE
/// (§17.1.1) и приём входящих запросов.
///
/// Транзакция — это то, что отвечает за «дошло или нет». На UDP ответа можно не
/// дождаться просто потому, что пакет потерялся, и без ретрансмиссий регистрация
/// и звонки будут случайным образом отваливаться.
///
/// В оригинале это actor: состояние защищено от гонок, но на каждом await актор
/// переоткрывается, и внутрь может войти другая задача. Здесь то же самое даёт
/// обычная блокировка вокруг работы со словарями при том, что отправка в сеть
/// идёт вне неё. Порядок операций сохранён дословно — в частности, состояние
/// транзакции меняется ДО отправки ACK: обратный порядок в оригинале давал
/// «таймаут через девять миллисекунд» вместо полученного отказа.
/// </summary>
public sealed class SipTransactionLayer : IDisposable
{
    /// <summary>
    /// Сколько ответов держим одновременно.
    ///
    /// Больше сотни живых серверных транзакций у софтфона с тремя линиями не
    /// бывает: столько записей означают не работу, а поток мусора на порт.
    /// Лишнее вытесняется самым старым, потому что ретрансмиссия приходит вскоре
    /// после ответа, а не через минуту.
    /// </summary>
    internal const int MaximumCachedResponses = 128;

    /// <summary>CRLFCRLF. Константой, чтобы не собирать четыре байта на каждый тик.</summary>
    internal static readonly byte[] KeepAlivePing = [0x0D, 0x0A, 0x0D, 0x0A];

    private readonly Lock _gate = new();
    private readonly ISipTransportChannel _channel;
    private readonly SipTransactionTimers _timers;

    private readonly Channel<SipRequest> _inbound =
        Channel.CreateBounded<SipRequest>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly Channel<SipServerInviteEvent> _serverInviteEvents =
        Channel.CreateBounded<SipServerInviteEvent>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly Channel<string> _channelClosures =
        Channel.CreateBounded<string>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// Ключ — branch плюс метод: CANCEL несёт тот же branch, что отменяемый
    /// INVITE, и без метода в ключе транзакции затирали бы друг друга.
    /// </summary>
    private readonly Dictionary<string, ClientTransaction> _clientTransactions = new(StringComparer.Ordinal);

    private readonly Dictionary<string, InviteTransaction> _inviteTransactions = new(StringComparer.Ordinal);

    /// <summary>Серверные INVITE по branch входящего запроса.</summary>
    private readonly Dictionary<string, ServerInviteTransaction> _serverInvites = new(StringComparer.Ordinal);

    /// <summary>
    /// Call-ID плюс CSeq → branch: по этой паре ACK находит свою транзакцию,
    /// даже когда приходит со своим branch (случай 2xx).
    /// </summary>
    private readonly Dictionary<string, string> _serverInviteKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Ответы на входящие запросы, чтобы отвечать одинаково на ретрансмиссии
    /// (RFC 3261 §17.2.2). Ключ — branch входящего запроса.
    ///
    /// Со сроком годности у каждой записи. Раньше срок держала отдельная задача
    /// со сном на сорок секунд — по задаче на каждый отправленный ответ, — и
    /// ограничить это было нечем: поток запросов на открытый UDP-порт растил и
    /// словарь, и число задач без потолка. Теперь просроченное убирается на
    /// следующей же записи, а сверху стоит предел.
    /// </summary>
    private readonly Dictionary<string, CachedResponse> _sentResponses = new(StringComparer.Ordinal);

    /// <summary>
    /// Ожидающие готовности канала — каждый со своим таймером.
    ///
    /// Словарь, а не список, потому что таймер у каждого свой. Общий таймер
    /// ронял всех разом: второй звонок, начатый через девять секунд после
    /// первого, получал чужой таймаут через секунду. На исправном транспорте это
    /// не всплывает — локальный адрес обычно уже известен, — а всплывает ровно
    /// тогда, когда сеть тормозит, то есть когда разбираться труднее всего.
    /// </summary>
    private readonly Dictionary<Guid, ReadinessWaiter> _readinessWaiters = [];

    private SipEndpoint? _localEndpoint;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private string? _failureReason;

    public SipTransactionLayer(ISipTransportChannel channel, SipTransactionTimers? timers = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _timers = timers ?? new SipTransactionTimers();
    }

    public SipTransport Transport => _channel.Transport;

    public SipEndpoint Remote => _channel.Remote;

    public IAsyncEnumerable<SipRequest> InboundRequests => _inbound.Reader.ReadAllAsync();

    /// <summary>Судьба финальных ответов на входящие INVITE: дошли или нет.</summary>
    public IAsyncEnumerable<SipServerInviteEvent> ServerInviteEvents => _serverInviteEvents.Reader.ReadAllAsync();

    /// <summary>
    /// Канал закрылся насовсем — с причиной.
    ///
    /// Отдельным потоком, а не полем: закрытие надо не «спросить потом», а
    /// узнать сразу, потому что чинится оно только пересборкой транспорта, а
    /// транспорт нам передали снаружи. Своё <see cref="StopAsync"/> сюда ничего
    /// не кладёт.
    /// </summary>
    public IAsyncEnumerable<string> ChannelClosures => _channelClosures.Reader.ReadAllAsync();

    /// <summary>
    /// Сколько ответов лежит в кэше. Доступно проверкам: потолок, который никто
    /// не считает, потолком не является.
    /// </summary>
    internal int CachedResponseCount
    {
        get
        {
            lock (_gate)
            {
                return _sentResponses.Count;
            }
        }
    }

    // Жизненный цикл

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_pumpTask is not null)
            {
                return;
            }
            _pumpCts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
        }

        await _channel.StartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? pump;
        List<string> serverBranches;
        lock (_gate)
        {
            pump = _pumpCts;
            _pumpCts = null;
            _pumpTask = null;
            serverBranches = [.. _serverInvites.Keys];
        }

        pump?.Cancel();

        FailAll(SipTransactionErrorKind.Cancelled);
        foreach (string branch in serverBranches)
        {
            ForgetServerInviteByBranch(branch);
        }

        _inbound.Writer.TryComplete();
        _serverInviteEvents.Writer.TryComplete();
        _channelClosures.Writer.TryComplete();

        await _channel.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Отпускает задачу приёма. Полная остановка — это <see cref="StopAsync"/>;
    /// здесь только то, что обязан закрыть владелец, даже если о ней забыли.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? pump;
        lock (_gate)
        {
            pump = _pumpCts;
            _pumpCts = null;
        }

        pump?.Cancel();
        pump?.Dispose();
    }

    /// <summary>Ждёт готовности канала и возвращает локальный адрес.</summary>
    public async Task<SipEndpoint> WaitUntilReadyAsync(TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);

        var id = Guid.NewGuid();
        TaskCompletionSource<SipEndpoint> completion;

        lock (_gate)
        {
            if (_localEndpoint is SipEndpoint known)
            {
                return known;
            }
            if (_failureReason is string reason)
            {
                throw new SipTransactionException(SipTransactionErrorKind.TransportFailed, reason);
            }

            completion = new TaskCompletionSource<SipEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            _readinessWaiters[id] = new ReadinessWaiter(
                completion,
                Schedule(limit, () => ExpireReadiness(id)));
        }

        return await completion.Task.ConfigureAwait(false);
    }

    // Не-INVITE

    /// <summary>
    /// Отправляет запрос и ждёт финального ответа.
    ///
    /// Если в Via нет branch, он дописывается прямо в переданный запрос: в
    /// оригинале структура копировалась, здесь копии нет, и это единственное
    /// место, где слой правит чужой объект.
    /// </summary>
    public async Task<SipResponse> SendAsync(SipRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        SipVia? via = request.TopVia
            ?? throw new SipTransactionException(SipTransactionErrorKind.NotReady, "в запросе нет Via");

        if (via.Branch is null)
        {
            via.Branch = SipToken.Branch();
            request.Headers.Set(SipHeaderName.Via, via.ToString());
        }

        string branch = via.Branch
            ?? throw new SipTransactionException(SipTransactionErrorKind.NotReady, "в Via нет branch");

        byte[] data = request.Encoded();
        SipMethod method = request.Method;
        string key = TransactionKey(branch, method);

        var completion = new TaskCompletionSource<SipResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _clientTransactions[key] = new ClientTransaction(method, data, completion)
            {
                TimeoutCts = Schedule(_timers.TransactionTimeout, () =>
                {
                    Finish(key, null, new SipTransactionException(SipTransactionErrorKind.Timeout));
                    return Task.CompletedTask;
                }),
            };
        }

        await TransmitAsync(key, data).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }

    // INVITE

    /// <summary>
    /// Отправляет INVITE и отдаёт поток событий транзакции.
    ///
    /// Поток заканчивается на финальном ответе, таймауте или отказе транспорта.
    /// </summary>
    public IAsyncEnumerable<SipInviteEvent> SendInvite(SipRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Channel<SipInviteEvent> events = Channel.CreateBounded<SipInviteEvent>(
            new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });

        string? branchValue = null;
        if (request.TopVia is SipVia via)
        {
            if (via.Branch is null)
            {
                via.Branch = SipToken.Branch();
                request.Headers.Set(SipHeaderName.Via, via.ToString());
            }
            branchValue = via.Branch;
        }

        if (branchValue is not string branch)
        {
            events.Writer.TryWrite(new SipInviteEvent.TransportFailed("в запросе нет Via"));
            events.Writer.TryComplete();
            return events.Reader.ReadAllAsync();
        }

        byte[] data = request.Encoded();
        string key = TransactionKey(branch, SipMethod.Invite);

        lock (_gate)
        {
            _inviteTransactions[key] = new InviteTransaction(request, data, events.Writer)
            {
                // Таймер B живёт только в состоянии Calling: после первого 1xx
                // сервер уже получил запрос, и гудки могут идти сколько угодно —
                // обрывать их по таймеру нельзя, это решение пользователя.
                TimeoutCts = Schedule(_timers.TransactionTimeout, () =>
                {
                    ExpireInvite(key);
                    return Task.CompletedTask;
                }),
            };
        }

        _ = Task.Run(() => TransmitInviteAsync(key, data), CancellationToken.None);

        return events.Reader.ReadAllAsync();
    }

    /// <summary>
    /// Отменяет INVITE, на который ещё не пришёл финальный ответ.
    ///
    /// Возвращает <see langword="false"/>, если отменять уже нечего. CANCEL
    /// имеет смысл только после первого 1xx: до него сервер мог ещё не создать
    /// транзакцию, и отменять было бы нечего (RFC 3261 §9.1).
    /// </summary>
    public async Task<bool> CancelInviteAsync(string branch)
    {
        string key = TransactionKey(branch, SipMethod.Invite);

        SipRequest requestToCancel;
        lock (_gate)
        {
            if (!_inviteTransactions.TryGetValue(key, out InviteTransaction? transaction)
                || transaction.State == InviteState.Completed)
            {
                return false;
            }

            // Первого 1xx ещё не было — отмену откладываем до него (RFC 3261
            // §9.1). Наверх это всё равно «отменяем»: для звонящего разницы нет,
            // а таймер B в состоянии calling жив и закроет транзакцию сам, если
            // 1xx не придёт вовсе.
            if (transaction.State != InviteState.Proceeding)
            {
                transaction.CancelsWhenProceeding = true;
                return true;
            }

            requestToCancel = transaction.Request;
        }

        await SendCancelAsync(key, requestToCancel).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Шлёт CANCEL и заводит предел ожидания 487.
    ///
    /// Предел обязателен, и это вторая половина той же поломки. На первом 1xx
    /// таймер B снимается — гудки идут сколько угодно, обрывать их по таймеру
    /// нельзя. Но у ОТМЕНЁННОГО INVITE ждать больше нечего, кроме финального
    /// ответа, а он теряется ровно так же, как всякий другой. Без предела запись
    /// жила в таблице до выхода из приложения, и вместе с ней — подвешенная на
    /// её поток задача звонка: по одной на каждую отмену.
    /// </summary>
    private async Task SendCancelAsync(string key, SipRequest request)
    {
        try
        {
            await SendAsync(MakeCancel(request)).ConfigureAwait(false);
        }
        catch (SipTransactionException)
        {
            // Судьба самого CANCEL неважна: важно, что INVITE закроется по
            // пределу ниже, а не повиснет.
        }

        lock (_gate)
        {
            if (_inviteTransactions.TryGetValue(key, out InviteTransaction? transaction))
            {
                // Место таймера B, снятого на 1xx: он уже не нужен, а поле свободно.
                transaction.TimeoutCts?.Cancel();
                transaction.TimeoutCts = Schedule(_timers.TransactionTimeout, () =>
                {
                    ExpireCancelledInvite(key);
                    return Task.CompletedTask;
                });
            }
        }
    }

    /// <summary>Отменённый INVITE, на который так и не пришёл финальный ответ.</summary>
    private void ExpireCancelledInvite(string key)
    {
        InviteTransaction? transaction;
        lock (_gate)
        {
            if (!_inviteTransactions.Remove(key, out transaction))
            {
                return;
            }
        }

        transaction.RetransmitCts?.Cancel();
        transaction.CompletedCts?.Cancel();
        transaction.Writer.TryWrite(new SipInviteEvent.Timeout());
        transaction.Writer.TryComplete();
    }

    /// <summary>Отправляет ответ на входящий запрос и запоминает его для ретрансмиссий.</summary>
    public async Task RespondAsync(SipRequest request, SipResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if (request.TopVia?.Branch is string branch)
        {
            Remember(response, branch);
        }
        await _channel.SendAsync(response.Encoded()).ConfigureAwait(false);
    }

    /// <summary>
    /// Отправляет ответ на входящий INVITE и ведёт серверную транзакцию.
    ///
    /// Отдельный вход, а не <see cref="RespondAsync"/>, потому что у INVITE
    /// ответ живёт своей жизнью: 1xx можно послать сколько угодно, а финальный
    /// надо повторять, пока не придёт ACK. Молча положиться на ретрансмиссии
    /// самого INVITE нельзя — на 2xx собеседник INVITE больше не повторяет, и
    /// потерянный 200 OK означает разговор, о котором знаем только мы.
    /// </summary>
    public async Task RespondToInviteAsync(SipRequest request, SipResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if (request.Method != SipMethod.Invite || request.TopVia?.Branch is not string branch)
        {
            throw new SipTransactionException(SipTransactionErrorKind.NotReady, "это не серверный INVITE");
        }

        string callId = request.CallId
            ?? throw new SipTransactionException(SipTransactionErrorKind.NotReady, "в запросе нет Call-ID");

        lock (_gate)
        {
            if (!_serverInvites.TryGetValue(branch, out ServerInviteTransaction? transaction))
            {
                transaction = new ServerInviteTransaction(request, callId);
                _serverInvites[branch] = transaction;
            }

            transaction.RetransmitCts?.Cancel();
            transaction.RetransmitCts = null;
            transaction.TimeoutCts?.Cancel();
            transaction.TimeoutCts = null;

            transaction.State = response.IsFinal
                ? response.IsSuccess ? ServerInviteState.Accepted : ServerInviteState.Completed
                : ServerInviteState.Proceeding;

            if (AcknowledgementKey(request) is string acknowledgementKey)
            {
                _serverInviteKeys[acknowledgementKey] = branch;
            }
        }

        // Ретрансмиссия самого INVITE обязана получить последний наш ответ, а не
        // второй новый: иначе собеседник увидит два разных 1xx на один запрос.
        Remember(response, branch);

        await _channel.SendAsync(response.Encoded()).ConfigureAwait(false);

        if (!response.IsFinal)
        {
            return;
        }

        // На надёжном транспорте повторять нечего: TLS сам гарантирует доставку.
        // Ждать ACK всё равно надо — он подтверждает не доставку, а согласие.
        StartAwaitingAcknowledgement(branch, repeats: !_channel.Transport.IsReliable());
    }

    /// <summary>
    /// Запускает ожидание ACK: повторы финального ответа и общий предел 64*T1.
    ///
    /// Таймеры G (повтор) и H (предел) по RFC 3261 §17.2.1 — и та же схема для
    /// 2xx по §13.3.1.4. Разница между случаями только в том, что делать по
    /// истечении: на 3xx–6xx транзакция просто умирает, а на 2xx наверх уходит
    /// «не подтверждено», потому что диалог уже создан и его надо закрывать.
    /// </summary>
    private void StartAwaitingAcknowledgement(string branch, bool repeats)
    {
        lock (_gate)
        {
            if (!_serverInvites.TryGetValue(branch, out ServerInviteTransaction? transaction))
            {
                return;
            }

            transaction.TimeoutCts = Schedule(_timers.TransactionTimeout, () =>
            {
                ExpireServerInvite(branch);
                return Task.CompletedTask;
            });

            if (repeats)
            {
                transaction.RetransmitCts = RepeatWithBackoff(
                    _timers.T1,
                    _timers.T2,
                    () => ResendServerInviteResponseAsync(branch));
            }
        }
    }

    /// <summary>Повторяет последний финальный ответ. <see langword="false"/> — повторять больше нечего.</summary>
    private async Task<bool> ResendServerInviteResponseAsync(string branch)
    {
        SipResponse response;
        lock (_gate)
        {
            if (!_serverInvites.TryGetValue(branch, out ServerInviteTransaction? transaction)
                || transaction.State == ServerInviteState.Proceeding
                || !_sentResponses.TryGetValue(branch, out CachedResponse cached))
            {
                return false;
            }
            response = cached.Response;
        }

        try
        {
            await _channel.SendAsync(response.Encoded()).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Отказ отправки здесь ничего не решает: предел ожидания ACK всё
            // равно закроет транзакцию.
        }
        return true;
    }

    private void ExpireServerInvite(string branch)
    {
        bool wasAccepted;
        string callId;
        lock (_gate)
        {
            if (!_serverInvites.TryGetValue(branch, out ServerInviteTransaction? transaction))
            {
                return;
            }
            wasAccepted = transaction.State == ServerInviteState.Accepted;
            callId = transaction.CallId;
        }

        ForgetServerInviteByBranch(branch);

        if (wasAccepted)
        {
            _serverInviteEvents.Writer.TryWrite(new SipServerInviteEvent.NotAcknowledged(callId));
        }
    }

    /// <summary>
    /// Принимает ACK на наш финальный ответ.
    ///
    /// Возвращает <see langword="true"/>, если ACK относился к известной
    /// серверной транзакции — тогда наверх его отдавать незачем.
    /// </summary>
    private bool AbsorbAcknowledgement(SipRequest request)
    {
        string branch;
        bool wasAccepted;
        string callId;

        lock (_gate)
        {
            // Сначала по branch: так приходит ACK на 3xx–6xx, он часть той же
            // транзакции. Потом по Call-ID и CSeq: ACK на 2xx идёт со своим branch.
            string? found = null;
            if (request.TopVia?.Branch is string ownBranch && _serverInvites.ContainsKey(ownBranch))
            {
                found = ownBranch;
            }
            else if (AcknowledgementKey(request) is string key
                && _serverInviteKeys.TryGetValue(key, out string? mapped))
            {
                found = mapped;
            }

            if (found is null || !_serverInvites.TryGetValue(found, out ServerInviteTransaction? transaction))
            {
                return false;
            }
            if (transaction.State == ServerInviteState.Proceeding)
            {
                return false;
            }

            branch = found;
            wasAccepted = transaction.State == ServerInviteState.Accepted;
            callId = transaction.CallId;
        }

        ForgetServerInviteByBranch(branch);

        // Ответ из кэша убираем вместе с транзакцией: ретрансмиссии INVITE после
        // ACK не бывает, а вот перезвон с тем же branch — бывает.
        lock (_gate)
        {
            _sentResponses.Remove(branch);
        }

        if (wasAccepted)
        {
            _serverInviteEvents.Writer.TryWrite(new SipServerInviteEvent.Acknowledged(callId));
        }
        return true;
    }

    private void ForgetServerInviteByBranch(string branch)
    {
        ServerInviteTransaction? transaction;
        lock (_gate)
        {
            if (!_serverInvites.Remove(branch, out transaction))
            {
                return;
            }
            if (AcknowledgementKey(transaction.Request) is string key)
            {
                _serverInviteKeys.Remove(key);
            }
        }

        transaction.RetransmitCts?.Cancel();
        transaction.TimeoutCts?.Cancel();
    }

    /// <summary>
    /// Снимает серверную транзакцию по Call-ID.
    ///
    /// Нужно ровно на одном переходе: мы ответили 200, ACK пришёл, разговор
    /// завершился. Ждать таймеров после этого не за чем.
    /// </summary>
    public void ForgetServerInvite(string callId)
    {
        List<string> branches;
        lock (_gate)
        {
            branches = [.. _serverInvites
                .Where(pair => string.Equals(pair.Value.CallId, callId, StringComparison.Ordinal))
                .Select(pair => pair.Key)];
        }

        foreach (string branch in branches)
        {
            ForgetServerInviteByBranch(branch);
            lock (_gate)
            {
                _sentResponses.Remove(branch);
            }
        }
    }

    private void Remember(SipResponse response, string branch)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;

            // Просроченное убираем на записи — так очередь чистится сама, без
            // задачи на каждый ответ.
            foreach (string expired in _sentResponses
                .Where(pair => pair.Value.ExpiresAt <= now)
                .Select(pair => pair.Key)
                .ToList())
            {
                _sentResponses.Remove(expired);
            }

            _sentResponses[branch] = new CachedResponse(response, now + (long)(_timers.T4.TotalMilliseconds * 8));

            // Предел на случай, когда просроченного ещё нет, а записей уже
            // много: так выглядит поток запросов, а не разговор.
            while (_sentResponses.Count > MaximumCachedResponses)
            {
                string oldest = _sentResponses.MinBy(pair => pair.Value.ExpiresAt).Key;
                _sentResponses.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Отправляет запрос, не создавая транзакцию и не ожидая ответа.
    ///
    /// Нужно ровно для одного случая: ACK на 2xx. Он идёт вне транзакции, и
    /// ответа на него не бывает.
    /// </summary>
    public Task SendWithoutTransactionAsync(SipRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _channel.SendAsync(request.Encoded());
    }

    /// <summary>
    /// Пустой пакет, удерживающий привязку NAT (RFC 5626 §4.4.1).
    ///
    /// Это не запрос: двойной CRLF не имеет ни метода, ни заголовков, и по
    /// RFC 5626 сервер на него либо отвечает одиночным CRLF, либо, как
    /// chan_sip, молча отбрасывает. Ответа мы не ждём ни в каком случае — смысл
    /// в самом факте исходящего пакета, который обновляет запись в таблице
    /// трансляции по дороге.
    /// </summary>
    public Task SendKeepAliveAsync() => _channel.SendAsync(KeepAlivePing);

    // Передача

    private async Task TransmitAsync(string key, byte[] data)
    {
        try
        {
            await _channel.SendAsync(data).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Finish(key, null, new SipTransactionException(SipTransactionErrorKind.TransportFailed, error.Message));
            return;
        }

        if (_channel.Transport.IsReliable())
        {
            return;
        }

        lock (_gate)
        {
            if (_clientTransactions.TryGetValue(key, out ClientTransaction? transaction))
            {
                transaction.RetransmitCts = RepeatWithBackoff(_timers.T1, _timers.T2, () => ResendAsync(key));
            }
        }
    }

    private async Task TransmitInviteAsync(string key, byte[] data)
    {
        try
        {
            await _channel.SendAsync(data).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            FailInvite(key, error.Message);
            return;
        }

        if (_channel.Transport.IsReliable())
        {
            return;
        }

        lock (_gate)
        {
            if (_inviteTransactions.TryGetValue(key, out InviteTransaction? transaction))
            {
                // Таймер A: интервал удваивается без ограничения T2 — в отличие
                // от не-INVITE. Так требует RFC 3261 §17.1.1.2.
                transaction.RetransmitCts = RepeatWithBackoff(_timers.T1, TimeSpan.MaxValue, () => ResendInviteAsync(key));
            }
        }
    }

    private async Task<bool> ResendAsync(string key)
    {
        byte[]? data;
        lock (_gate)
        {
            data = _clientTransactions.TryGetValue(key, out ClientTransaction? transaction) ? transaction.Data : null;
        }
        if (data is null)
        {
            return false;
        }

        try
        {
            await _channel.SendAsync(data).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Молчим: у транзакции есть свой таймер, он и решит её судьбу.
        }
        return true;
    }

    private async Task<bool> ResendInviteAsync(string key)
    {
        byte[]? data;
        lock (_gate)
        {
            data = _inviteTransactions.TryGetValue(key, out InviteTransaction? transaction)
                && transaction.State == InviteState.Calling
                ? transaction.Data
                : null;
        }
        if (data is null)
        {
            return false;
        }

        try
        {
            await _channel.SendAsync(data).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        return true;
    }

    // Приём

    private async Task PumpAsync(CancellationToken token)
    {
        try
        {
            await foreach (SipTransportEvent transportEvent in _channel.Events.WithCancellation(token).ConfigureAwait(false))
            {
                await HandleAsync(transportEvent).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Обычная остановка.
        }
    }

    private async Task HandleAsync(SipTransportEvent transportEvent)
    {
        switch (transportEvent)
        {
            case SipTransportEvent.Ready ready:
                lock (_gate)
                {
                    _localEndpoint = ready.Local;
                    _failureReason = null;
                }
                ResolveReadiness(ready.Local, null);
                break;

            case SipTransportEvent.Received received:
                await HandleReceivedAsync(received.Data).ConfigureAwait(false);
                break;

            case SipTransportEvent.Failed failed:
                // Канал жив и повторит попытку сам. Причину запоминаем ради тех,
                // кто ждёт первой готовности: им она заменяет десять секунд
                // молчания внятным текстом. На уже поднятый канал она не влияет —
                // WaitUntilReady сначала отдаёт известный локальный адрес, и это
                // намеренно: запрещать набор на время подрагивания Wi-Fi значит
                // ломать работу там, где ломаться нечему.
                //
                // А вот транзакции в пути больше не роняем. Раньше их убивал
                // каждый «waiting» от сетевой подсистемы, то есть каждое
                // подрагивание Wi-Fi: REGISTER обрывался, backoff рос, и после
                // перехода между точками доступа регистрация возвращалась не
                // сразу, а следующим его шагом — до пяти минут без входящих. У
                // транзакций есть собственные таймеры, и они для этого и заведены.
                lock (_gate)
                {
                    _failureReason = failed.Reason;
                }
                break;

            case SipTransportEvent.Closed closed:
                lock (_gate)
                {
                    _failureReason = closed.Reason;
                }
                ResolveReadiness(null, new SipTransactionException(SipTransactionErrorKind.TransportFailed, closed.Reason));
                FailAll(SipTransactionErrorKind.TransportFailed, closed.Reason);

                // Наверх — один раз и с причиной. Дальше решает тот, кто создавал
                // транспорт: сами мы его пересобрать не можем.
                _channelClosures.Writer.TryWrite(closed.Reason);
                break;

            case SipTransportEvent.Cancelled:
                ResolveReadiness(null, new SipTransactionException(SipTransactionErrorKind.Cancelled));
                FailAll(SipTransactionErrorKind.Cancelled);
                break;

            default:
                break;
        }
    }

    private async Task HandleReceivedAsync(ReadOnlyMemory<byte> data)
    {
        SipMessage message;
        try
        {
            message = SipParser.Parse(data);
        }
        catch (SipParseException)
        {
            // Битое сообщение — не повод рвать канал: на UDP это может быть
            // чужой мусор, залетевший на наш порт.
            return;
        }

        if (message.AsResponse is SipResponse response)
        {
            await HandleResponseAsync(response).ConfigureAwait(false);
            return;
        }

        SipRequest request = message.AsRequest!;

        // ACK — подтверждение, а не запрос: отвечать на него нечем, и повторять
        // ему кэшированный ответ нельзя. А он приходит с тем же branch, что
        // INVITE, и без этой проверки на каждый ACK уходил бы повтор нашего же
        // 486 — собеседник читал бы это как новый отказ.
        if (request.Method == SipMethod.Ack)
        {
            if (AbsorbAcknowledgement(request))
            {
                return;
            }
            _inbound.Writer.TryWrite(request);
            return;
        }

        // Ретрансмиссия: тот же branch и тот же метод, что у уже отвеченного
        // запроса. Метод проверяем обязательно — CANCEL несёт branch отменяемого
        // INVITE, и без проверки получил бы в ответ его 180.
        SipResponse? cached = null;
        if (request.TopVia?.Branch is string branch)
        {
            lock (_gate)
            {
                if (_sentResponses.TryGetValue(branch, out CachedResponse previous)
                    && previous.Response.CSeq?.Method == request.Method)
                {
                    cached = previous.Response;
                }
            }
        }

        if (cached is not null)
        {
            try
            {
                await _channel.SendAsync(cached.Encoded()).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            return;
        }

        _inbound.Writer.TryWrite(request);
    }

    private async Task HandleResponseAsync(SipResponse response)
    {
        // Сопоставление по RFC 3261 §17.1.3: branch верхнего Via плюс метод из
        // CSeq. Только branch недостаточно.
        if (response.TopVia?.Branch is not string branch || response.CSeq is not (int, SipMethod) cseq)
        {
            return;
        }

        string key = TransactionKey(branch, cseq.Method);

        if (cseq.Method == SipMethod.Invite)
        {
            await HandleInviteResponseAsync(response, key).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            if (!_clientTransactions.TryGetValue(key, out ClientTransaction? transaction))
            {
                return;
            }

            if (!response.IsFinal)
            {
                // 1xx на не-INVITE: запрос сервером получен, ретрансмиссии
                // больше не нужны.
                transaction.RetransmitCts?.Cancel();
                transaction.RetransmitCts = null;
                return;
            }
        }

        Finish(key, response, null);
    }

    private async Task HandleInviteResponseAsync(SipResponse response, string key)
    {
        InviteTransaction transaction;
        bool cancelNow = false;
        SipRequest? requestToCancel = null;

        lock (_gate)
        {
            if (!_inviteTransactions.TryGetValue(key, out InviteTransaction? found)
                || found.State == InviteState.Completed)
            {
                return;
            }
            transaction = found;

            if (response.IsProvisional)
            {
                transaction.RetransmitCts?.Cancel();
                transaction.RetransmitCts = null;
                transaction.TimeoutCts?.Cancel();
                transaction.TimeoutCts = null;
                transaction.State = InviteState.Proceeding;

                if (transaction.CancelsWhenProceeding)
                {
                    // Отмену просили раньше, чем сервер отозвался. Вот он
                    // отозвался — теперь CANCEL законен.
                    transaction.CancelsWhenProceeding = false;
                    cancelNow = true;
                    requestToCancel = transaction.Request;
                }
            }
            else
            {
                transaction.RetransmitCts?.Cancel();
                transaction.TimeoutCts?.Cancel();

                if (response.IsSuccess)
                {
                    // Транзакция на 2xx завершается немедленно, а ACK отправит
                    // вызывающая сторона — он идёт вне транзакции.
                    _inviteTransactions.Remove(key);
                }
                else
                {
                    // Состояние меняем ДО отправки ACK.
                    //
                    // Отправка асинхронная: пока ACK уходит в сеть, внутрь
                    // успевает войти уже отменённая задача таймера B, увидеть
                    // состояние calling и объявить транзакцию истёкшей — вместо
                    // полученного отказа. В оригинале ошибка воспроизводилась
                    // стабильно и выглядела как таймаут через девять миллисекунд.
                    transaction.State = InviteState.Completed;
                }
            }
        }

        if (response.IsProvisional)
        {
            transaction.Writer.TryWrite(new SipInviteEvent.Provisional(response));

            // Наверх про этот 1xx уже сказано, и это правильно: звонок в этот
            // момент отменяется, а не начинается.
            if (cancelNow && requestToCancel is not null)
            {
                await SendCancelAsync(key, requestToCancel).ConfigureAwait(false);
            }
            return;
        }

        if (response.IsSuccess)
        {
            transaction.Writer.TryWrite(new SipInviteEvent.Success(response));
            transaction.Writer.TryComplete();
            return;
        }

        transaction.Writer.TryWrite(new SipInviteEvent.Failure(response));

        // Неуспешный финальный ответ: ACK — наша обязанность и часть транзакции.
        try
        {
            await _channel.SendAsync(MakeFailureAck(transaction.Request, response).Encoded()).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        // Таймер D: держим состояние, чтобы поглощать ретрансмиссии ответа и
        // отвечать на них тем же ACK.
        TimeSpan lifetime = _channel.Transport.IsReliable() ? TimeSpan.Zero : _timers.CompletedLifetime;
        lock (_gate)
        {
            if (_inviteTransactions.TryGetValue(key, out InviteTransaction? still))
            {
                still.CompletedCts = Schedule(lifetime, () =>
                {
                    RemoveInvite(key);
                    return Task.CompletedTask;
                });
            }
        }
    }

    private void ExpireInvite(string key)
    {
        InviteTransaction? transaction;
        lock (_gate)
        {
            if (!_inviteTransactions.TryGetValue(key, out transaction) || transaction.State != InviteState.Calling)
            {
                return;
            }
            _inviteTransactions.Remove(key);
        }

        transaction.RetransmitCts?.Cancel();
        transaction.Writer.TryWrite(new SipInviteEvent.Timeout());
        transaction.Writer.TryComplete();
    }

    private void FailInvite(string key, string reason)
    {
        InviteTransaction? transaction;
        lock (_gate)
        {
            if (!_inviteTransactions.Remove(key, out transaction))
            {
                return;
            }
        }

        transaction.RetransmitCts?.Cancel();
        transaction.TimeoutCts?.Cancel();
        transaction.Writer.TryWrite(new SipInviteEvent.TransportFailed(reason));
        transaction.Writer.TryComplete();
    }

    private void RemoveInvite(string key)
    {
        InviteTransaction? transaction;
        lock (_gate)
        {
            if (!_inviteTransactions.Remove(key, out transaction))
            {
                return;
            }
        }

        transaction.CompletedCts?.Cancel();
        transaction.Writer.TryComplete();
    }

    private void Finish(string key, SipResponse? response, Exception? error)
    {
        ClientTransaction? transaction;
        lock (_gate)
        {
            if (!_clientTransactions.Remove(key, out transaction))
            {
                return;
            }
        }

        transaction.RetransmitCts?.Cancel();
        transaction.TimeoutCts?.Cancel();

        if (response is not null)
        {
            transaction.Completion.TrySetResult(response);
        }
        else
        {
            transaction.Completion.TrySetException(error ?? new SipTransactionException(SipTransactionErrorKind.Cancelled));
        }
    }

    private void FailAll(SipTransactionErrorKind kind, string detail = "")
    {
        List<string> clientKeys;
        List<string> inviteKeys;
        lock (_gate)
        {
            clientKeys = [.. _clientTransactions.Keys];
            inviteKeys = [.. _inviteTransactions.Keys];
        }

        foreach (string key in clientKeys)
        {
            Finish(key, null, new SipTransactionException(kind, detail));
        }
        foreach (string key in inviteKeys)
        {
            FailInvite(key, detail.Length == 0 ? kind.ToString() : $"{kind}: {detail}");
        }
    }

    /// <summary>Истёк срок ожидания у одного — остальные ждут дальше свой.</summary>
    private Task ExpireReadiness(Guid id)
    {
        ReadinessWaiter waiter;
        lock (_gate)
        {
            if (!_readinessWaiters.Remove(id, out waiter!))
            {
                return Task.CompletedTask;
            }
        }

        waiter.Timeout?.Cancel();
        waiter.Completion.TrySetException(new SipTransactionException(SipTransactionErrorKind.Timeout));
        return Task.CompletedTask;
    }

    /// <summary>Отвечает всем: канал готов, закрылся или отменён.</summary>
    private void ResolveReadiness(SipEndpoint? local, Exception? error)
    {
        List<ReadinessWaiter> waiters;
        lock (_gate)
        {
            waiters = [.. _readinessWaiters.Values];
            _readinessWaiters.Clear();
        }

        foreach (ReadinessWaiter waiter in waiters)
        {
            waiter.Timeout?.Cancel();
            if (local is SipEndpoint endpoint)
            {
                waiter.Completion.TrySetResult(endpoint);
            }
            else
            {
                waiter.Completion.TrySetException(error ?? new SipTransactionException(SipTransactionErrorKind.Cancelled));
            }
        }
    }

    // Таймеры

    /// <summary>
    /// Выполняет действие через заданный срок, если задачу не отменили.
    ///
    /// Отмена обязана замолчать: в оригинале на этом месте стоял «catch с
    /// возвратом, а не try?» — проглоченная отмена приводила к срабатыванию
    /// таймера ровно тогда, когда его уже сняли за ненадобностью.
    /// </summary>
    private static CancellationTokenSource Schedule(TimeSpan delay, Func<Task> action)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }
                cts.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await action().ConfigureAwait(false);
        }, CancellationToken.None);
        return cts;
    }

    /// <summary>
    /// Повторяет действие с удвоением интервала, пока оно возвращает
    /// <see langword="true"/>. Потолок <paramref name="maximum"/> — это T2;
    /// у таймера A потолка нет (RFC 3261 §17.1.1.2), и туда передаётся
    /// <see cref="TimeSpan.MaxValue"/>.
    /// </summary>
    private static CancellationTokenSource RepeatWithBackoff(TimeSpan initial, TimeSpan maximum, Func<Task<bool>> action)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            TimeSpan interval = initial;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (cts.Token.IsCancellationRequested || !await action().ConfigureAwait(false))
                {
                    return;
                }

                TimeSpan doubled = interval + interval;
                interval = doubled > maximum ? maximum : doubled;
            }
        }, CancellationToken.None);
        return cts;
    }

    // Ключи и вспомогательные сообщения

    /// <summary>
    /// Ключ таблицы транзакций.
    ///
    /// Branch сам по себе не годится: CANCEL по RFC обязан нести тот же branch,
    /// что и отменяемый INVITE, и без метода в ключе эти две транзакции затирали
    /// бы друг друга.
    /// </summary>
    internal static string TransactionKey(string branch, SipMethod method) => $"{branch}|{method.Name()}";

    /// <summary>
    /// Ключ, по которому ACK находит серверную транзакцию.
    ///
    /// Branch не годится: ACK на 2xx по RFC 3261 §17.1.1.3 — отдельная
    /// транзакция со своим branch, и совпадать с INVITE он не обязан. Общими
    /// остаются Call-ID и номер CSeq, их и берём.
    /// </summary>
    internal static string AcknowledgementKey(string callId, int sequence) =>
        $"{callId}|{sequence.ToString(CultureInfo.InvariantCulture)}";

    internal static string? AcknowledgementKey(SipMessage message) =>
        message.CallId is string callId && message.CSeq is (int Number, SipMethod) cseq
            ? AcknowledgementKey(callId, cseq.Number)
            : null;

    /// <summary>
    /// Собирает ACK на неуспешный финальный ответ.
    ///
    /// Отличается от ACK на 2xx: этот идёт с тем же branch, что INVITE, на тот
    /// же Request-URI и внутри той же транзакции.
    /// </summary>
    internal static SipRequest MakeFailureAck(SipRequest request, SipResponse response)
    {
        var ack = new SipRequest(SipMethod.Ack, request.Uri);

        if (request.Headers.First(SipHeaderName.Via) is string via)
        {
            ack.Headers.Append(SipHeaderName.Via, via);
        }
        ack.Headers.Append(SipHeaderName.MaxForwards, "70");
        if (request.Headers.First(SipHeaderName.From) is string from)
        {
            ack.Headers.Append(SipHeaderName.From, from);
        }

        // To берём ИЗ ОТВЕТА: там уже есть тег собеседника, а в нашем запросе его
        // не было. Без этого сервер ACK не опознает.
        if (response.Headers.First(SipHeaderName.To) is string to)
        {
            ack.Headers.Append(SipHeaderName.To, to);
        }
        else if (request.Headers.First(SipHeaderName.To) is string ownTo)
        {
            ack.Headers.Append(SipHeaderName.To, ownTo);
        }

        if (request.Headers.First(SipHeaderName.CallId) is string callId)
        {
            ack.Headers.Append(SipHeaderName.CallId, callId);
        }
        if (request.CSeq is (int Number, SipMethod) cseq)
        {
            ack.Headers.Append(
                SipHeaderName.CSeq,
                $"{cseq.Number.ToString(CultureInfo.InvariantCulture)} {SipMethod.Ack.Name()}");
        }

        // Route повторяем: ACK должен пройти тем же путём.
        foreach (string route in request.Headers.Values(SipHeaderName.Route))
        {
            ack.Headers.Append(SipHeaderName.Route, route);
        }
        return ack;
    }

    /// <summary>Собирает CANCEL для ещё не отвеченного INVITE.</summary>
    internal static SipRequest MakeCancel(SipRequest request)
    {
        var cancel = new SipRequest(SipMethod.Cancel, request.Uri);

        // Тот же branch, что у INVITE — так сервер понимает, что отменять.
        if (request.Headers.First(SipHeaderName.Via) is string via)
        {
            cancel.Headers.Append(SipHeaderName.Via, via);
        }
        cancel.Headers.Append(SipHeaderName.MaxForwards, "70");

        foreach (string name in (string[])[SipHeaderName.From, SipHeaderName.To, SipHeaderName.CallId])
        {
            if (request.Headers.First(name) is string value)
            {
                cancel.Headers.Append(name, value);
            }
        }

        if (request.CSeq is (int Number, SipMethod) cseq)
        {
            cancel.Headers.Append(
                SipHeaderName.CSeq,
                $"{cseq.Number.ToString(CultureInfo.InvariantCulture)} {SipMethod.Cancel.Name()}");
        }

        foreach (string route in request.Headers.Values(SipHeaderName.Route))
        {
            cancel.Headers.Append(SipHeaderName.Route, route);
        }
        return cancel;
    }

    // Внутреннее состояние

    private enum InviteState
    {
        Calling,
        Proceeding,
        Completed,
    }

    /// <summary>
    /// Серверная сторона INVITE: RFC 3261 §17.2.1 плюс §13.3.1.4 для 2xx.
    ///
    /// Отдельный тип, а не переиспользование клиентского: у сторон разные
    /// состояния и разный смысл ретрансмиссий. Клиент повторяет запрос, пока не
    /// услышит ответ; сервер повторяет ОТВЕТ, пока не услышит подтверждение.
    /// </summary>
    private enum ServerInviteState
    {
        /// <summary>Ответили 1xx, финального ещё нет.</summary>
        Proceeding,

        /// <summary>Ответили 3xx–6xx, ждём ACK и повторяем ответ (таймеры G и H).</summary>
        Completed,

        /// <summary>Ответили 2xx, ждём ACK и повторяем ответ (§13.3.1.4).</summary>
        Accepted,
    }

    private sealed class ClientTransaction(SipMethod method, byte[] data, TaskCompletionSource<SipResponse> completion)
    {
        public SipMethod Method { get; } = method;

        public byte[] Data { get; } = data;

        public TaskCompletionSource<SipResponse> Completion { get; } = completion;

        public CancellationTokenSource? RetransmitCts { get; set; }

        public CancellationTokenSource? TimeoutCts { get; set; }
    }

    private sealed class InviteTransaction(SipRequest request, byte[] data, ChannelWriter<SipInviteEvent> writer)
    {
        public SipRequest Request { get; } = request;

        public byte[] Data { get; } = data;

        public ChannelWriter<SipInviteEvent> Writer { get; } = writer;

        public InviteState State { get; set; } = InviteState.Calling;

        public CancellationTokenSource? RetransmitCts { get; set; }

        public CancellationTokenSource? TimeoutCts { get; set; }

        public CancellationTokenSource? CompletedCts { get; set; }

        /// <summary>
        /// Отмену попросили раньше, чем пришёл первый 1xx.
        ///
        /// RFC 3261 §9.1: CANCEL нельзя слать, пока на INVITE не пришёл
        /// провизорный ответ. До него сервер мог ещё не завести транзакцию —
        /// такой CANCEL получает 481, а сам INVITE продолжает звонить у
        /// вызываемого, и вешает трубку в итоге только таймаут набора. Промах
        /// дешёвый: достаточно оператору передумать в первые полсекунды после
        /// набора.
        ///
        /// Поэтому просьба запоминается и исполняется на первом же 1xx.
        /// </summary>
        public bool CancelsWhenProceeding { get; set; }
    }

    private sealed class ServerInviteTransaction(SipRequest request, string callId)
    {
        public SipRequest Request { get; } = request;

        public string CallId { get; } = callId;

        public ServerInviteState State { get; set; } = ServerInviteState.Proceeding;

        public CancellationTokenSource? RetransmitCts { get; set; }

        public CancellationTokenSource? TimeoutCts { get; set; }
    }

    private readonly record struct CachedResponse(SipResponse Response, long ExpiresAt);

    private readonly record struct ReadinessWaiter(
        TaskCompletionSource<SipEndpoint> Completion,
        CancellationTokenSource? Timeout);
}
