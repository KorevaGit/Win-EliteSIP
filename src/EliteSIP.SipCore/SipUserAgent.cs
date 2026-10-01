using System.Globalization;
using System.Threading.Channels;

namespace EliteSIP.SipCore;

/// <summary>Что агент рассказывает наружу.</summary>
public abstract record SipUserAgentEvent
{
    private SipUserAgentEvent()
    {
    }

    public sealed record Registration(SipRegistrationState State) : SipUserAgentEvent;

    public sealed record Log(SipLogLevel Level, string Message) : SipUserAgentEvent;

    /// <summary>
    /// Нам звонят. Дальше решает тот, кто это событие получил:
    /// <see cref="SipUserAgent.AnswerIncomingCallAsync"/> или
    /// <see cref="SipUserAgent.RejectIncomingCallAsync"/>.
    /// </summary>
    public sealed record IncomingCall(SipIncomingCall Call) : SipUserAgentEvent;

    /// <summary>
    /// Запрос, на который мы ответили отказом, потому что он не поддерживается.
    /// Нужен, чтобы такие вещи было видно, а не только в журнале.
    /// </summary>
    public sealed record UnsupportedRequest(SipMethod Method) : SipUserAgentEvent;

    /// <summary>
    /// Канал закрылся насовсем: дальше без пересборки транспорта не будет ни
    /// регистрации, ни звонков.
    ///
    /// Агент сам себе помочь тут не может — транспорт ему передали снаружи, и
    /// создать новый может только тот, кто создавал прежний. Повторы регистрации
    /// в этом состоянии бессмысленны: каждая попытка падает мгновенно на мёртвом
    /// канале, а человек читает «повтор через N с» как обещание, которое не
    /// сбудется.
    /// </summary>
    public sealed record ChannelClosed(string Reason) : SipUserAgentEvent;
}

/// <summary>
/// Пользовательский агент SIP: регистрация, звонки и ответы на входящие запросы.
///
/// Отвечать на OPTIONS не менее важно, чем держать регистрацию: chan_sip с
/// <c>qualify=yes</c> опрашивает пир каждые 30 секунд, и молчание в ответ
/// переводит пир в UNREACHABLE, после чего входящие звонки на него не приходят
/// вообще.
///
/// В оригинале это actor. Здесь — класс с блокировкой вокруг состояния линий и
/// регистрации; вся сетевая работа идёт вне неё, как и в оригинале, где actor
/// переоткрывался на каждом await.
/// </summary>
public sealed partial class SipUserAgent : IDisposable
{
    public const string DefaultUserAgentName = "EliteSIP/0.1 (Windows)";

    /// <summary>
    /// Сколько разговоров агент держит одновременно.
    ///
    /// Три — это исходный разговор, консультационный звонок и третий участник
    /// для конференции. Четвёртую линию оператор не удержит в голове, а каждая
    /// занимает пару портов RTP/RTCP и свой диалог на сервере.
    /// </summary>
    public const int MaximumLines = 3;

    /// <summary>
    /// Список растёт по этапам, и в нём только то, что мы действительно
    /// обрабатываем. REFER здесь нет: мы умеем его отправлять, но входящий явно
    /// получает 501, потому что удалённое управление нашим разговором — отдельная
    /// политика.
    ///
    /// INFO в списке нет тоже: DTMF мы шлём по RFC 4733, внутри потока RTP.
    /// Заявить INFO значило бы разрешить серверу присылать нам DTMF тем путём,
    /// которого мы не обрабатываем.
    /// </summary>
    internal const string AllowedMethods = "INVITE, ACK, CANCEL, BYE, OPTIONS, NOTIFY";

    /// <summary>
    /// Что мы умеем сверх ядра RFC 3261.
    ///
    /// <c>timer</c> здесь не украшение: chan_sip обновляет сессию повторным
    /// INVITE ровно потому, что UPDATE в нашем Allow нет. Появится UPDATE в
    /// списке разрешённых методов — Asterisk начнёт слать обновления им, и
    /// обработать их будет нечем. Эти две строки связаны, хотя стоят рядом
    /// случайно.
    /// </summary>
    internal const string SupportedOptionTags = "replaces, " + SipSessionTimerHeader.OptionTag;

    /// <summary>
    /// Заведомый потолок срока регистрации — сутки.
    ///
    /// У chan_sip <c>maxexpiry</c> по умолчанию час, так что суток хватает с
    /// запасом на любую настройку, которая имеет смысл.
    /// </summary>
    internal const int MaximumExpires = 86_400;

    /// <summary>
    /// Сколько терпеть гудки без финального ответа — значение по умолчанию.
    ///
    /// Слой транзакций после первого 1xx не ограничивает исходящий звонок ничем,
    /// и это правильно: таймер B живёт только в состоянии calling, потому что
    /// гудеть у вызываемого может сколько угодно. Но «сколько угодно» там
    /// означает буквально вечность, и линия оставалась в «Гудках» навсегда
    /// всякий раз, когда финальный ответ до нас не доехал: UDP через интернет,
    /// протухший NAT-биндинг — и все ретрансмиссии 486 по таймеру G уходят в
    /// никуда. Единственным выходом оставался отбой руками, а пара портов RTP и
    /// место линии всё это время числились занятыми.
    ///
    /// Три минуты — величина таймера C из RFC 3261 §16.6, той же природы
    /// («прокси не ждёт вечно») и с тем же смыслом: это не срок дозвона, а
    /// признак того, что разговаривать уже не с кем. Живой звонок столько не
    /// гудит: и АТС, и мобильная сеть сдаются раньше.
    /// </summary>
    public static readonly TimeSpan DefaultRingingLimit = TimeSpan.FromSeconds(180);

    private readonly Lock _gate = new();
    private readonly SipAccount _account;
    private readonly DigestAuthentication.Credentials _credentials;
    private readonly SipTransactionLayer _transactions;
    private readonly string _userAgentName;
    private readonly TimeSpan _ringingLimit;
    private readonly TimeSpan _transferResultTimeout;
    private readonly TimeSpan _keepAliveInterval;
    private readonly SipSessionTimerPolicy _sessionTimerPolicy;
    private readonly ISipPathOpener? _pathOpener;

    private readonly Channel<SipUserAgentEvent> _events =
        Channel.CreateBounded<SipUserAgentEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    // Идентификаторы серии регистраций: Call-ID и tag постоянны, пока агент жив.
    private readonly string _registrationCallId = SipToken.CallId();
    private readonly string _localTag = SipToken.Tag();

    /// <summary>
    /// Линии, адресуемые по Call-ID.
    ///
    /// Ключ именно Call-ID, а не порядковый номер линии: все входящие запросы
    /// приходят с ним, и любой другой ключ пришлось бы искать перебором на каждый
    /// BYE, повторный INVITE и NOTIFY.
    /// </summary>
    private readonly Dictionary<string, ActiveCall> _calls = new(StringComparer.Ordinal);

    /// <summary>
    /// Порядок появления линий. Словарь его не хранит, а оператору линии нужны в
    /// том порядке, в каком он их завёл: первая — разговор, вторая —
    /// консультация, третья — конференция.
    /// </summary>
    private readonly List<string> _lineOrder = [];

    /// <summary>
    /// Линии, место под которые занято, а диалога ещё нет.
    ///
    /// Между началом звонка и первым INVITE есть ожидание готовности транспорта,
    /// и без этого множества два быстрых нажатия проходят проверку свободной
    /// линии оба: словарь линий в этот момент ещё пуст. Занять место надо там,
    /// где решение принимается, — в синхронной части.
    /// </summary>
    private readonly HashSet<string> _reservedLines = new(StringComparer.Ordinal);

    /// <summary>
    /// Активная подписка, созданная REFER. Ключ — Call-ID исходного диалога:
    /// NOTIFY приходит внутри него и тем же ключом однозначно находится.
    /// </summary>
    private readonly Dictionary<string, ChannelWriter<SipTransferEvent>> _transferSubscriptions =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, CancellationTokenSource> _transferTimeouts = new(StringComparer.Ordinal);

    /// <summary>Доходит ли до нас опрос сервера. Устройство и цена — в <see cref="QualifyWatch"/>.</summary>
    private readonly QualifyWatch _qualifyWatch = new();

    private int _cseq;

    /// <summary>
    /// Адрес, который мы указываем в Contact. Сначала локальный, после первого
    /// ответа — тот, которым нас видит сервер.
    /// </summary>
    private SipEndpoint? _contactEndpoint;

    private (DigestChallenge Challenge, string ResponseHeader)? _cachedChallenge;
    private int _nonceCount;

    private SipRegistrationState _state = new SipRegistrationState.Idle();
    private CancellationTokenSource? _registrationCts;
    private CancellationTokenSource? _keepAliveCts;
    private CancellationTokenSource? _pumpCts;
    private bool _isStopping;

    /// <summary>
    /// Канал закрылся насовсем — и это состояние необратимо для этого агента.
    ///
    /// Отдельный признак, а не одна отмена задачи, потому что отмена
    /// останавливает цикл не сразу: попытка регистрации, уже вошедшая в обработку
    /// отказа, успевает записать своё «повтор через N с» поверх нашего «канал
    /// закрыт». Ровно эту гонку и поймал тест.
    /// </summary>
    private bool _isChannelClosed;

    private int _consecutiveFailures;
    private SipMediaRenegotiator? _mediaRenegotiator;

    public SipUserAgent(
        SipAccount account,
        DigestAuthentication.Credentials credentials,
        ISipTransportChannel channel,
        SipTransactionTimers? timers = null,
        string userAgentName = DefaultUserAgentName,
        TimeSpan? transferResultTimeout = null,
        TimeSpan? ringingLimit = null,
        TimeSpan? keepAliveInterval = null,
        SipSessionTimerPolicy? sessionTimerPolicy = null,
        ISipPathOpener? pathOpener = null)
    {
        ArgumentNullException.ThrowIfNull(account);

        _account = account;
        _credentials = credentials;
        _transactions = new SipTransactionLayer(channel, timers)
        {
            // Отброшенное входящее уходит в тот же журнал, что и всё остальное.
            // Без этой строки оно не оставляло следа нигде, а объяснять им
            // приходится самые дорогие жалобы — «разговор висит после того, как
            // собеседник положил трубку».
            OnDiscarded = (level, text) => Log(level, text),
        };
        _userAgentName = userAgentName;
        _transferResultTimeout = transferResultTimeout ?? TimeSpan.FromSeconds(60);
        _ringingLimit = ringingLimit ?? DefaultRingingLimit;
        _keepAliveInterval = keepAliveInterval ?? DefaultKeepAliveInterval(account.Transport);
        _sessionTimerPolicy = sessionTimerPolicy ?? new SipSessionTimerPolicy();
        _pathOpener = pathOpener;
    }

    public IAsyncEnumerable<SipUserAgentEvent> Events => _events.Reader.ReadAllAsync();

    public SipRegistrationState RegistrationState
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Почему регистрация не удалась в последний раз.
    ///
    /// <see langword="null"/> — ни одной неудачи с последнего успеха: значение
    /// сбрасывается на каждой удавшейся регистрации, чтобы вчерашний отказ не
    /// читался как сегодняшний.
    /// </summary>
    public SipRegistrationException? LastRegistrationFailure { get; private set; }

    /// <summary>
    /// Адрес, который надо указывать в SDP.
    ///
    /// Это тот же адрес, что в Contact, то есть внешний, сообщённый сервером в
    /// received. Указать в SDP локальный адрес за NAT — значит получить
    /// установленный звонок без звука.
    /// </summary>
    public string? MediaAddress
    {
        get
        {
            lock (_gate)
            {
                return _contactEndpoint?.Host;
            }
        }
    }

    /// <summary>
    /// Локальный адрес, с которого идёт сигнализация. <see langword="null"/> —
    /// канал ещё не поднят.
    ///
    /// Не путать с <see cref="MediaAddress"/>: тот — адрес, каким нас видит
    /// сервер, этот — адрес нашего сокета. Нужен медиа, чтобы привязать RTP к
    /// тому же интерфейсу. Сокет сигнализации соединённый, и его адрес
    /// выбирается один раз при подключении, а сокет RTP, привязанный «к
    /// любому», получает маршрут на каждом пакете заново. 11 сентября 2026 на
    /// машине оператора VPN проложил маршрут до сети АТС: SIP остался на
    /// Ethernet, а RTP ушёл через VPN с чужим адресом — сигнализация исправна,
    /// звука нет.
    /// </summary>
    public string? LocalSignalingAddress => _transactions.LocalEndpoint?.Host;

    /// <summary>
    /// Что снаружи видно про одну линию.
    ///
    /// Снимок, а не ссылка на живой звонок: отдавать наружу изменяемое состояние
    /// значило бы отдать гонку.
    /// </summary>
    public readonly record struct Line(
        string CallId,
        string Peer,
        SipCallState State,
        bool IsOutgoing,
        SipDialogIdentifier? DialogIdentifier);

    /// <summary>Линии в порядке появления.</summary>
    public IReadOnlyList<Line> Lines
    {
        get
        {
            lock (_gate)
            {
                List<Line> result = [];
                foreach (string callId in _lineOrder)
                {
                    if (_calls.TryGetValue(callId, out ActiveCall? call))
                    {
                        result.Add(new Line(
                            call.CallId,
                            call.Peer,
                            call.State,
                            call.Role == CallRole.Caller,
                            call.Dialog is SipDialog dialog ? new SipDialogIdentifier(dialog) : null));
                    }
                }
                return result;
            }
        }
    }

    /// <summary>Свободна ли ещё одна линия.</summary>
    public bool HasFreeLine
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count + _reservedLines.Count < MaximumLines;
            }
        }
    }

    /// <summary>
    /// Состояние единственной линии. При двух и более возвращает
    /// <see langword="null"/> — спрашивать надо <see cref="CallStateOf"/> или
    /// <see cref="Lines"/>.
    /// </summary>
    public SipCallState? CallState
    {
        get
        {
            lock (_gate)
            {
                return Resolve(null)?.State;
            }
        }
    }

    public SipCallState? CallStateOf(string callId)
    {
        lock (_gate)
        {
            return _calls.TryGetValue(callId, out ActiveCall? call) ? call.State : null;
        }
    }

    /// <summary>
    /// Идентификатор установленного диалога для Replaces.
    ///
    /// Он нужен исходному разговору, чтобы сослаться на консультационный. До
    /// ответа диалога ещё нет, поэтому возвращается <see langword="null"/>.
    /// </summary>
    public SipDialogIdentifier? DialogIdentifierOf(string callId)
    {
        lock (_gate)
        {
            return _calls.TryGetValue(callId, out ActiveCall? call) && call.Dialog is SipDialog dialog
                ? new SipDialogIdentifier(dialog)
                : null;
        }
    }

    /// <summary>То же для единственной линии.</summary>
    public SipDialogIdentifier? CurrentDialogIdentifier
    {
        get
        {
            lock (_gate)
            {
                return Resolve(null)?.Dialog is SipDialog dialog ? new SipDialogIdentifier(dialog) : null;
            }
        }
    }

    /// <summary>Как отвечать на чужой повторный INVITE. Задаёт приложение.</summary>
    public void SetMediaRenegotiator(SipMediaRenegotiator? handler)
    {
        lock (_gate)
        {
            _mediaRenegotiator = handler;
        }
    }

    // Жизненный цикл

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_registrationCts is not null)
            {
                return;
            }
            _isStopping = false;
            _isChannelClosed = false;
        }

        await _transactions.StartAsync().ConfigureAwait(false);

        var pumpCts = new CancellationTokenSource();
        var registrationCts = new CancellationTokenSource();
        var keepAliveCts = new CancellationTokenSource();

        lock (_gate)
        {
            _pumpCts = pumpCts;
            _registrationCts = registrationCts;
            _keepAliveCts = keepAliveCts;
        }

        _ = Task.Run(() => PumpInboundAsync(pumpCts.Token), CancellationToken.None);
        _ = Task.Run(() => PumpServerInvitesAsync(pumpCts.Token), CancellationToken.None);
        _ = Task.Run(() => PumpChannelClosuresAsync(pumpCts.Token), CancellationToken.None);
        _ = Task.Run(() => RunRegistrationLoopAsync(registrationCts.Token), CancellationToken.None);
        _ = Task.Run(() => RunKeepAliveLoopAsync(keepAliveCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Немедленно перерегистрироваться, не дожидаясь планового обновления.
    ///
    /// Нужна и пользователю (кнопка «Переподключить»), и при смене сети: старая
    /// регистрация после смены адреса указывает не туда, и ждать её истечения
    /// значит не принимать звонки несколько минут.
    /// </summary>
    public Task ReregisterNowAsync()
    {
        CancellationTokenSource fresh;
        lock (_gate)
        {
            if (_isStopping || _isChannelClosed)
            {
                return Task.CompletedTask;
            }
            _registrationCts?.Cancel();
            _consecutiveFailures = 0;
            fresh = new CancellationTokenSource();
            _registrationCts = fresh;
        }

        _ = Task.Run(() => RunRegistrationLoopAsync(fresh.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Снимает регистрацию и закрывает канал.
    ///
    /// Снятие делается по возможности и с коротким пределом: если сервер
    /// недоступен, приложение не должно висеть на выходе. Регистрация всё равно
    /// истечёт сама.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? registration;
        CancellationTokenSource? keepAlive;
        lock (_gate)
        {
            _isStopping = true;
            registration = _registrationCts;
            _registrationCts = null;
            keepAlive = _keepAliveCts;
            _keepAliveCts = null;
        }

        registration?.Cancel();
        keepAlive?.Cancel();

        // Сначала закрываем текущие диалоги, пока транспорт ещё доступен. Иначе
        // приложение уже отключено, а сервер продолжает держать канал; потоки
        // событий звонков также обязаны завершиться до остановки насосов.
        await HangUpAsync().ConfigureAwait(false);

        if (RegistrationState.IsRegistered)
        {
            SetState(new SipRegistrationState.Unregistering());

            // Именно регистрация со сроком 0, а не одиночный REGISTER: снятие
            // сервер тоже требует авторизовать, и без обработки 401 пир остаётся
            // зарегистрированным до истечения срока.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await RegisterAsync(0, limit.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SipRegistrationException or SipTransactionException or OperationCanceledException)
            {
                // Сервер недоступен — регистрация истечёт сама.
            }
        }

        CancellationTokenSource? pump;
        lock (_gate)
        {
            pump = _pumpCts;
            _pumpCts = null;
        }
        pump?.Cancel();

        await _transactions.StopAsync().ConfigureAwait(false);
        SetState(new SipRegistrationState.Idle());
        _events.Writer.TryComplete();
    }

    public void Dispose()
    {
        _registrationCts?.Dispose();
        _keepAliveCts?.Dispose();
        _pumpCts?.Dispose();
        _transactions.Dispose();
    }

    // Насосы

    private async Task PumpInboundAsync(CancellationToken token)
    {
        try
        {
            await foreach (SipRequest request in _transactions.InboundRequests.WithCancellation(token).ConfigureAwait(false))
            {
                await HandleInboundAsync(request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PumpServerInvitesAsync(CancellationToken token)
    {
        try
        {
            await foreach (SipServerInviteEvent value in _transactions.ServerInviteEvents.WithCancellation(token).ConfigureAwait(false))
            {
                await HandleServerInviteAsync(value).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PumpChannelClosuresAsync(CancellationToken token)
    {
        try
        {
            await foreach (string reason in _transactions.ChannelClosures.WithCancellation(token).ConfigureAwait(false))
            {
                HandleChannelClosed(reason);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Канал закрылся насовсем.
    ///
    /// Всё, что можно сделать изнутри агента, — перестать делать вид, что повторы
    /// помогут: цикл регистрации останавливается, а наверх уходит событие. Дальше
    /// либо приложение пересоберёт транспорт и поднимет нового агента, либо не
    /// пересоберёт — но тогда и «повтор через N с» на экране врать не будет.
    /// </summary>
    private void HandleChannelClosed(string reason)
    {
        CancellationTokenSource? registration;
        CancellationTokenSource? keepAlive;

        var closed = new SipRegistrationState.Failed(reason, null);

        lock (_gate)
        {
            if (_isStopping || _isChannelClosed)
            {
                return;
            }
            _isChannelClosed = true;
            registration = _registrationCts;
            _registrationCts = null;
            keepAlive = _keepAliveCts;
            _keepAliveCts = null;

            // Состояние пишется под той же блокировкой, что и признак закрытия.
            // Иначе между ними успевает войти цикл регистрации, уже вошедший в
            // обработку отказа, и кладёт своё «повтор через N с» поверх «канал
            // закрыт». В оригинале эту гонку закрывала изоляция actor: между
            // проверкой признака и записью состояния там нет ни одного await.
            _state = closed;
        }

        registration?.Cancel();
        keepAlive?.Cancel();

        Log(SipLogLevel.Error, $"канал закрыт: {reason}");
        _events.Writer.TryWrite(new SipUserAgentEvent.Registration(closed));
        _events.Writer.TryWrite(new SipUserAgentEvent.ChannelClosed(reason));
    }

    // Регистрация

    private async Task RunRegistrationLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (_isStopping || _isChannelClosed)
                {
                    return;
                }
            }

            try
            {
                // Перед REGISTER, а не параллельно с ним: пока дорога закрыта,
                // запрос уходит в никуда и стоит нам полного цикла ожидания
                // ответа плюс шага backoff. Повод «повтор» не пропускается
                // никогда — самая правдоподобная причина отказа в том, что
                // публичный адрес сменился и в списке шлюза его больше нет.
                if (_pathOpener is not null)
                {
                    await _pathOpener.OpenPathAsync(
                        _consecutiveFailures > 0 ? SipPathOpenReason.Retry : SipPathOpenReason.Registration)
                        .ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                SetState(new SipRegistrationState.Registering());
                int granted = await RegisterAsync(_account.RegistrationExpires, token).ConfigureAwait(false);

                lock (_gate)
                {
                    _consecutiveFailures = 0;
                }
                LastRegistrationFailure = null;
                _qualifyWatch.NoteReachable(Now);

                string contact;
                lock (_gate)
                {
                    contact = _contactEndpoint?.ToString() ?? "—";
                }

                SetState(new SipRegistrationState.Registered(
                    DateTimeOffset.UtcNow.AddSeconds(granted),
                    contact));
                Log(SipLogLevel.Info, $"зарегистрирован на {granted.ToString(CultureInfo.InvariantCulture)} с, Contact {contact}");

                int refreshAfter = RefreshInterval(granted);
                Log(SipLogLevel.Debug, $"обновление регистрации через {refreshAfter.ToString(CultureInfo.InvariantCulture)} с");
                await Task.Delay(TimeSpan.FromSeconds(refreshAfter), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                // Закрытый канал уже всё сказал про себя сам, и «повтор через
                // N с» поверх этого был бы обещанием, которое не сбудется.
                int failures;
                lock (_gate)
                {
                    if (_isStopping || _isChannelClosed)
                    {
                        return;
                    }
                    failures = ++_consecutiveFailures;
                }

                LastRegistrationFailure = error as SipRegistrationException;
                int delay = RetryDelay(failures, error);
                string reason = Describe(error);

                // Проверка признака повторяется внутри записи состояния: между
                // ней и записью канал может закрыться, и тогда писать нечего.
                if (!SetStateUnlessClosed(new SipRegistrationState.Failed(
                    reason,
                    DateTimeOffset.UtcNow.AddSeconds(delay))))
                {
                    return;
                }

                Log(
                    SipLogLevel.Warning,
                    $"регистрация не удалась: {reason}. Повтор через {delay.ToString(CultureInfo.InvariantCulture)} с");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Одна попытка регистрации. Возвращает выданный сервером срок в секундах.
    ///
    /// Срок 0 означает снятие регистрации — путь тот же, включая обязательную
    /// авторизацию.
    /// </summary>
    private async Task<int> RegisterAsync(int expires, CancellationToken token)
    {
        int requestedExpires = expires;

        /// Сколько раз в рамках этой попытки мы уже ответили на вызов сервера.
        int challengeAnswers = 0;

        /// Сколько раз уже правили Contact по подсказке сервера.
        int contactCorrections = 0;

        // Попыток немного и они разные по смыслу: ответ на вызов, повтор с
        // увеличенным сроком по 423 и повтор с исправленным Contact после того,
        // как сервер сообщил наш внешний адрес.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            token.ThrowIfCancellationRequested();
            SipResponse response = await SendRegisterAsync(requestedExpires).ConfigureAwait(false);

            // Внешний адрес узнаём из ЛЮБОГО ответа, включая 401. Тогда повтор с
            // авторизацией уже несёт правильный Contact, и лишнего круга
            // регистрации не происходит.
            bool contactChanged = LearnObservedEndpoint(response);

            if (response.IsSuccess)
            {
                // Правим Contact не более одного раза за попытку. Некоторые NAT
                // выдают новый внешний порт чуть ли не на каждую датаграмму — без
                // ограничения регистрация уходила бы в круг «исправили Contact,
                // сервер видит новый порт» и падала по числу попыток.
                if (contactChanged && contactCorrections == 0)
                {
                    contactCorrections++;
                    Log(SipLogLevel.Info, "Contact исправлен на внешний адрес, перерегистрируемся");
                    continue;
                }
                if (contactChanged)
                {
                    Log(SipLogLevel.Debug, "сервер снова сообщил другой адрес; регистрацию принимаем как есть");
                }
                return GrantedExpires(response, requestedExpires);
            }

            if (response.IsAuthenticationRequired)
            {
                IReadOnlyList<(DigestChallenge Challenge, string ResponseHeader)> challenges =
                    response.AuthenticationChallenges();

                if (challenges.Count == 0)
                {
                    throw new SipRegistrationException(
                        SipRegistrationErrorKind.Rejected,
                        response.StatusCode,
                        response.ReasonPhrase);
                }

                // Второй вызов подряд после того, как мы уже ответили, означает
                // неверные креды. Asterisk с alwaysauthreject=yes на неверный
                // пароль отвечает не 403, а тем же 401 — чтобы не выдавать,
                // существует ли такой номер. Без этой проверки самая частая
                // реальная ошибка выглядела бы как «слишком много попыток».
                if (challengeAnswers >= 1 && !challenges[0].Challenge.Stale)
                {
                    throw new SipRegistrationException(SipRegistrationErrorKind.AuthenticationFailed);
                }

                lock (_gate)
                {
                    _cachedChallenge = challenges[0];
                    _nonceCount = 0;
                }
                challengeAnswers++;
                continue;
            }

            if (response.StatusCode == 423)
            {
                // Сервер считает срок слишком коротким и говорит минимум.
                int? minimum = response.Headers.Number(SipHeaderName.MinExpires);
                if (minimum is not int value || value <= requestedExpires)
                {
                    throw new SipRegistrationException(
                        SipRegistrationErrorKind.Rejected,
                        423,
                        "Interval Too Brief без Min-Expires");
                }
                Log(SipLogLevel.Info, $"сервер требует минимум {value.ToString(CultureInfo.InvariantCulture)} с");
                requestedExpires = value;
                continue;
            }

            throw new SipRegistrationException(
                SipRegistrationErrorKind.Rejected,
                response.StatusCode,
                response.ReasonPhrase);
        }

        throw new SipRegistrationException(SipRegistrationErrorKind.TooManyAttempts);
    }

    private async Task<SipResponse> SendRegisterAsync(int expires)
    {
        SipRequest request = await MakeRegisterAsync(expires).ConfigureAwait(false);
        Log(SipLogLevel.Debug, $"-> REGISTER expires={expires.ToString(CultureInfo.InvariantCulture)}");

        SipResponse response = await _transactions.SendAsync(request).ConfigureAwait(false);
        Log(SipLogLevel.Debug, $"<- {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}");
        return response;
    }

    private async Task<SipRequest> MakeRegisterAsync(int expires)
    {
        SipEndpoint local = await _transactions.WaitUntilReadyAsync().ConfigureAwait(false);

        SipEndpoint contact;
        int sequence;
        (DigestChallenge Challenge, string ResponseHeader)? cached;
        lock (_gate)
        {
            _contactEndpoint ??= local;
            contact = _contactEndpoint.Value;
            _cseq++;
            sequence = _cseq;
            cached = _cachedChallenge;
        }

        var request = new SipRequest(SipMethod.Register, _account.RegistrarUri);

        // Via содержит адрес, С КОТОРОГО отправляем, а не тот, которым нас видно:
        // rport просит сервер сообщить второе, и подменять первое нельзя.
        var via = new SipVia(_account.Transport, local.Host, local.Port) { Branch = SipToken.Branch() };
        via.RequestRport();
        request.Headers.Append(SipHeaderName.Via, via.ToString());
        request.Headers.Append(SipHeaderName.MaxForwards, "70");

        var from = new NameAddress(_account.AddressOfRecord, _account.EffectiveDisplayName) { Tag = _localTag };
        request.Headers.Append(SipHeaderName.From, from.ToString());
        request.Headers.Append(SipHeaderName.To, new NameAddress(_account.AddressOfRecord).ToString());
        request.Headers.Append(SipHeaderName.CallId, _registrationCallId);
        request.Headers.Append(
            SipHeaderName.CSeq,
            $"{sequence.ToString(CultureInfo.InvariantCulture)} {SipMethod.Register.Name()}");

        var contactUri = new SipUri(contact.Host, user: _account.Username, port: contact.Port);
        if (_account.Transport != SipTransport.Udp)
        {
            contactUri.SetParameter("transport", _account.Transport.ProtocolName().ToLowerInvariant());
        }
        request.Headers.Append(SipHeaderName.Contact, new NameAddress(contactUri).ToString());

        request.Headers.Append(SipHeaderName.Expires, expires.ToString(CultureInfo.InvariantCulture));
        request.Headers.Append(SipHeaderName.Allow, AllowedMethods);
        request.Headers.Append(SipHeaderName.Supported, "replaces");
        request.Headers.Append(SipHeaderName.UserAgent, _userAgentName);

        // Упреждающая авторизация: если вызов уже известен, отвечаем сразу и
        // экономим полный обмен 401 на каждом обновлении. Если nonce устарел,
        // сервер пришлёт новый вызов, и мы повторим.
        if (cached is { } challenge)
        {
            request.Headers.Append(
                challenge.ResponseHeader,
                Authorization(SipMethod.Register, _account.RegistrarUri, challenge.Challenge));
        }

        return request;
    }

    /// <summary>
    /// Запоминает внешний адрес, сообщённый сервером в received и rport.
    ///
    /// Возвращает <see langword="true"/>, если адрес отличается от того, что уже
    /// стоит в Contact. Без этой правки при работе через NAT регистрация
    /// формально успешна, а входящие звонки уходят на локальный адрес и не
    /// доходят никогда.
    /// </summary>
    private bool LearnObservedEndpoint(SipResponse response)
    {
        if (response.TopVia?.ObservedAddress is not { } observed || observed.Port is not ushort port)
        {
            return false;
        }

        var discovered = new SipEndpoint(observed.Host, port);
        string previous;
        lock (_gate)
        {
            if (_contactEndpoint == discovered)
            {
                return false;
            }
            previous = _contactEndpoint?.ToString() ?? "—";
            _contactEndpoint = discovered;
        }

        Log(SipLogLevel.Debug, $"сервер видит нас как {discovered}, в Contact было {previous}");
        return true;
    }

    private int GrantedExpires(SipResponse response, int requested)
    {
        // Срок может приехать и в Expires, и параметром у Contact. Второе
        // приоритетнее: оно относится именно к нашей привязке.
        int? raw = null;
        foreach (NameAddress contact in response.Contacts)
        {
            if (string.Equals(contact.Uri.User, _account.Username, StringComparison.Ordinal) && contact.Expires is int ours)
            {
                raw = ours;
                break;
            }
        }

        if (raw is null)
        {
            foreach (NameAddress contact in response.Contacts)
            {
                if (contact.Expires is int any)
                {
                    raw = any;
                    break;
                }
            }
        }

        raw ??= response.Expires ?? requested;
        return SanitizedExpires(raw.Value, requested);
    }

    /// <summary>
    /// Приводит срок из ответа сервера к разумным границам.
    ///
    /// Это число приезжает из сети, а дальше становится интервалом сна — и без
    /// границ ломается дважды. Огромное значение уводит обновление регистрации на
    /// годы, привязка на сервере тем временем истекает, и софтфон молча перестаёт
    /// принимать вызовы, считая себя на линии.
    /// </summary>
    internal static int SanitizedExpires(int value, int requested) =>
        value < 0
            ? Math.Min(Math.Max(requested, 0), MaximumExpires)
            : Math.Min(value, MaximumExpires);

    // Удержание привязки NAT

    /// <summary>
    /// Держит открытой дорогу от сервера к нам.
    ///
    /// Без этого цикла единственный исходящий трафик между звонками — обновление
    /// регистрации, а оно редкое: при боевых <c>Reg. default duration: 3600</c>
    /// пауза между пакетами доходит до часа. NAT и межсетевые экраны закрывают
    /// привязку UDP заметно раньше — типичные таймауты 30–120 секунд. Дальше
    /// выглядит это так: клиент показывает «зарегистрирован», сервер считает пир
    /// живым, а INVITE из очереди раздачи до рабочего места не доходит вовсе,
    /// потому что обратной дороги через NAT уже нет. Для колл-центра, где входящий
    /// звонок и есть работа, это отказ, который ничем себя не обнаруживает.
    ///
    /// Пакет уходит независимо от состояния регистрации: пока идёт повтор после
    /// отказа (backoff доходит до 300 секунд), привязка нужна ровно затем, чтобы
    /// до нас дошёл ответ на следующий REGISTER.
    /// </summary>
    private async Task RunKeepAliveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Jittered(_keepAliveInterval), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                if (_isStopping)
                {
                    return;
                }
            }

            // Стук едет на этом же цикле, а не на своём таймере: своя задача
            // означала бы третий жизненный цикл со своими start и stop, а нужен
            // ровно тот же — «пока мы подключены».
            if (_pathOpener is not null)
            {
                await _pathOpener.OpenPathAsync(SipPathOpenReason.Periodic).ConfigureAwait(false);
            }

            try
            {
                await _transactions.SendKeepAliveAsync().ConfigureAwait(false);

                // Успешную отправку тоже пишем, и это не шум. Раньше keep-alive не
                // оставлял в журнале ни строки, и разобрать по архиву жалобу
                // «перестал быть доступен» было нечем: «пакеты уходили, но не
                // помогли» и «пакеты не уходили» выглядели одинаково, а лечатся
                // по-разному.
                Log(SipLogLevel.Debug, "keep-alive ушёл");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Молча: транспорт сообщает о своих отказах сам, через события
                // канала. Дублировать их предупреждением на каждый тик значит
                // залить журнал одним и тем же, пока сеть лежит.
                Log(SipLogLevel.Debug, $"keep-alive не ушёл: {error.Message}");
            }

            CheckQualifySilence();
        }
    }

    /// <summary>
    /// Проверяет, не пропал ли опрос сервера, и лечит это перерегистрацией.
    ///
    /// Едет на такте keep-alive, а не на своём таймере: третий жизненный цикл со
    /// своими start и stop не нужен, а нужный уже есть — «пока мы подключены».
    ///
    /// Лечение выбрано по журналу, а не по догадке: в архиве 18 августа 2026
    /// каждый провал опроса кончался ровно в ту миллисекунду, когда уходил
    /// очередной REGISTER. То есть исходящий запрос открывает обратную дорогу, и
    /// ждать планового обновления — значит ждать зря.
    /// </summary>
    private void CheckQualifySilence()
    {
        if (!RegistrationState.IsRegistered)
        {
            return;
        }
        if (_qualifyWatch.SilenceIfLost(Now) is not TimeSpan silence)
        {
            return;
        }

        Log(
            SipLogLevel.Warning,
            $"сервер не опрашивал нас {((int)silence.TotalSeconds).ToString(CultureInfo.InvariantCulture)} с при живой "
                + "регистрации — обратной дороги нет, перерегистрируемся");

        _ = ReregisterNowAsync();
    }

    /// <summary>
    /// Интервал по умолчанию для транспорта.
    ///
    /// UDP — 25 секунд: меньше самого короткого распространённого таймаута NAT
    /// (30 секунд) с запасом на дрожание. Для TCP и TLS привязка живёт кратно
    /// дольше, и там хватает 120 секунд из RFC 5626 §4.4.1.
    /// </summary>
    internal static TimeSpan DefaultKeepAliveInterval(SipTransport transport) =>
        transport.IsReliable() ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(25);

    /// <summary>
    /// Разброс ±10 %, чтобы рабочие места не били в сервер в такт.
    ///
    /// Смена в колл-центре начинается одновременно, и без разброса полсотни
    /// клиентов синхронно шлют keep-alive в одну и ту же секунду — ровно то, от
    /// чего RFC 5626 §4.4.1 и предостерегает.
    /// </summary>
    internal static TimeSpan Jittered(TimeSpan interval)
    {
        long spread = interval.Ticks / 10;
        if (spread <= 0)
        {
            return interval;
        }
        // Разброс — не секрет, поэтому обычный генератор: криптографический здесь
        // ничего не добавил бы, кроме стоимости.
        return TimeSpan.FromTicks(interval.Ticks + Random.Shared.NextInt64(-spread, spread));
    }

    /// <summary>
    /// Когда обновлять регистрацию.
    ///
    /// Обновляемся заведомо раньше истечения: если ждать до последней секунды,
    /// любая потеря пакета оставит нас незарегистрированными, и входящие звонки
    /// пропадут до следующей попытки.
    /// </summary>
    internal static int RefreshInterval(int grantedExpires)
    {
        if (grantedExpires <= 0)
        {
            return 30;
        }
        if (grantedExpires <= 20)
        {
            return Math.Max(grantedExpires - 5, 5);
        }
        return Math.Max(grantedExpires - 30, grantedExpires / 2);
    }

    /// <summary>Задержка перед повтором — по тому, чем кончилась попытка.</summary>
    ///
    /// <remarks>
    /// <para>
    /// Две причины отказа требуют противоположного. Сервер <b>ответил</b> отказом
    /// (неверный пароль, 403, 404) — частые повторы ничего не исправят, а
    /// FreePBX с fail2ban за серию неудачных входов банит адрес целиком, то есть
    /// весь офис за NAT. Тут откат долгий, до 300 с.
    /// </para>
    /// <para>
    /// Сервер <b>не ответил</b> (молчание, обрыв, ICMP) или ответил временным
    /// отказом (408, 480, 5xx) — так выглядит перезапуск Asterisk или моргнувшая
    /// сеть. До 0.1.67 и этот случай уходил на 300 с: сервер поднимался, а
    /// рабочие места ещё до пяти минут не принимали звонков из очереди — в
    /// журнале 29 сентября 2026 видна ровно такая серия. Здесь потолок
    /// <see cref="UnreachableBackoffLimit"/>; нагрузка от этого на сервер —
    /// один REGISTER в полминуты-минуту с места, то есть никакая.
    /// </para>
    /// </remarks>
    internal static int RetryDelay(int attempt, Exception error) =>
        IsServerRefusal(error)
            ? BackoffDelay(attempt)
            : Math.Min(BackoffDelay(attempt), UnreachableBackoffLimit);

    /// <summary>Потолок отката, пока сервер недоступен, в секундах.</summary>
    internal const int UnreachableBackoffLimit = 15;

    /// <summary>
    /// Сервер ответил окончательным отказом, а не промолчал и не отказал временно.
    /// </summary>
    internal static bool IsServerRefusal(Exception error) => error switch
    {
        SipRegistrationException { Kind: SipRegistrationErrorKind.Rejected, Status: 408 or 480 or >= 500 } => false,
        SipRegistrationException => true,
        _ => false,
    };

    /// <summary>Задержка перед повтором: 5, 10, 20, 40, 80, 160, дальше 300.</summary>
    internal static int BackoffDelay(int attempt)
    {
        if (attempt <= 0)
        {
            return 5;
        }
        int exponent = Math.Min(attempt - 1, 6);
        return Math.Min(5 << exponent, 300);
    }

    private static TimeSpan Now => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// Записывает состояние, если канал ещё жив.
    ///
    /// Проверка и запись — под одной блокировкой: разнести их значит вернуть ту
    /// самую гонку, из-за которой на экране оставалось «повтор через N с» поверх
    /// закрытого канала.
    /// </summary>
    private bool SetStateUnlessClosed(SipRegistrationState newState)
    {
        bool changed;
        lock (_gate)
        {
            if (_isStopping || _isChannelClosed)
            {
                return false;
            }
            changed = _state != newState;
            if (changed)
            {
                _state = newState;
            }
        }

        if (changed)
        {
            _events.Writer.TryWrite(new SipUserAgentEvent.Registration(newState));
        }
        return true;
    }

    private void SetState(SipRegistrationState newState)
    {
        lock (_gate)
        {
            if (_state == newState)
            {
                return;
            }
            _state = newState;
        }
        _events.Writer.TryWrite(new SipUserAgentEvent.Registration(newState));
    }

    private void Log(SipLogLevel level, string message) =>
        _events.Writer.TryWrite(new SipUserAgentEvent.Log(level, message));

    internal static string Describe(Exception error) => error switch
    {
        SipRegistrationException registration => registration.Message,
        SipTransactionException transaction => transaction.Kind switch
        {
            SipTransactionErrorKind.Timeout => "сервер не ответил",
            SipTransactionErrorKind.TransportFailed => $"сеть: {transaction.Detail}",
            SipTransactionErrorKind.Cancelled => "соединение закрыто",
            SipTransactionErrorKind.NotReady => "транспорт не готов",
            _ => "ответ не относится ни к одному запросу",
        },
        _ => error.Message,
    };

    private string Authorization(SipMethod method, SipUri uri, DigestChallenge challenge)
    {
        int count;
        lock (_gate)
        {
            _nonceCount++;
            count = _nonceCount;
        }

        return DigestAuthentication.AuthorizationValue(
            new DigestAuthentication.Credentials(_account.EffectiveAuthUsername, _credentials.Password),
            challenge,
            method,
            uri.ToString(),
            count);
    }

    // Состояние линий

    private enum CallRole
    {
        Caller,
        Callee,
    }

    private sealed class ActiveCall
    {
        public required CallRole Role { get; init; }

        public required string CallId { get; init; }

        /// <summary>Номер собеседника: цель набора у исходящего, From у входящего.</summary>
        public required string Peer { get; init; }

        public required string LocalTag { get; init; }

        public required string Branch { get; init; }

        /// <summary>Номер CSeq INVITE. ACK на 2xx обязан повторить его.</summary>
        public required int InviteSequence { get; init; }

        public required ChannelWriter<SipCallEvent> Writer { get; init; }

        public SipDialog? Dialog { get; set; }

        public required SipCallState State { get; set; }

        /// <summary>Входящий INVITE целиком: на него надо отвечать, и не один раз.</summary>
        public SipRequest? InviteRequest { get; set; }

        /// <summary>
        /// Последнее тело SDP, которое мы отправили по этому звонку.
        ///
        /// Нужно на повторный INVITE без предложения: такой запрос означает
        /// «объяви заново, чем ты располагаешь», и отвечать на него надо тем, о
        /// чём уже договорились.
        /// </summary>
        public ReadOnlyMemory<byte>? LocalSdp { get; set; }

        /// <summary>
        /// Наш повторный INVITE в пути.
        ///
        /// Встречное предложение в этот момент обязано получить 491, а не второй
        /// ответ: два пересогласования одного диалога, разошедшиеся в пути, — это
        /// классическая ничья, из которой без 491 не выбраться.
        /// </summary>
        public bool IsRenegotiating { get; set; }

        /// <summary>
        /// REFER уже принят в работу. Второй перевод того же диалога до финального
        /// NOTIFY двусмысленен и потому отклоняется локально.
        /// </summary>
        public bool IsTransferring { get; set; }

        /// <summary>
        /// Договорённость об обновлении сессии, если она состоялась.
        ///
        /// <see langword="null"/> — таймера нет, и это не то же самое, что «таймер
        /// с большим сроком»: без договорённости не заводится ни обновление, ни
        /// слежение, потому что вторая сторона о них не знает.
        /// </summary>
        public SipSessionTimer? SessionTimer { get; set; }

        /// <summary>
        /// Обновление сессии или слежение за чужим — смотря какая роль нам
        /// досталась. Задача одна: ролей взаимоисключающие две.
        /// </summary>
        public CancellationTokenSource? SessionTimerCts { get; set; }

        /// <summary>Предел гудков. Заводится на первом 1xx, снимается вместе с линией.</summary>
        public CancellationTokenSource? RingingTimeoutCts { get; set; }
    }

    private void Add(ActiveCall call)
    {
        lock (_gate)
        {
            _reservedLines.Remove(call.CallId);
            if (!_calls.ContainsKey(call.CallId))
            {
                _lineOrder.Add(call.CallId);
            }
            _calls[call.CallId] = call;
        }
    }

    private void RemoveCall(string callId)
    {
        ActiveCall? removed;
        lock (_gate)
        {
            _reservedLines.Remove(callId);
            _calls.Remove(callId, out removed);
            _lineOrder.Remove(callId);
        }

        // Снимать таймер сессии надо именно здесь: это единственная точка, через
        // которую линия исчезает, а переживший её таймер положил бы трубку на
        // чужом разговоре — Call-ID к тому времени принадлежит уже не ему.
        removed?.SessionTimerCts?.Cancel();
        removed?.RingingTimeoutCts?.Cancel();
    }

    /// <summary>
    /// Приводит необязательный Call-ID к существующей линии.
    ///
    /// Без аргумента адресуется единственная линия — так вызывают проверки и
    /// стенд, которым многолинейность не нужна. При двух и более линиях умолчания
    /// нет: догадка здесь означала бы положить трубку не тому.
    /// </summary>
    private ActiveCall? Resolve(string? callId)
    {
        if (callId is not null)
        {
            return _calls.GetValueOrDefault(callId);
        }
        return _lineOrder.Count == 1 ? _calls.GetValueOrDefault(_lineOrder[0]) : null;
    }

    /// <summary>Линия, на которой сейчас звонят нам.</summary>
    private ActiveCall? IncomingCall()
    {
        foreach (string callId in _lineOrder)
        {
            if (_calls.TryGetValue(callId, out ActiveCall? call) && call.State is SipCallState.Incoming)
            {
                return call;
            }
        }
        return null;
    }

    private void EmitCallState(SipCallState newState, string callId)
    {
        ActiveCall? call;
        lock (_gate)
        {
            if (!_calls.TryGetValue(callId, out call) || call.State == newState)
            {
                return;
            }
            call.State = newState;
        }
        call.Writer.TryWrite(new SipCallEvent.State(newState));
    }

    private void FinishCall(string callId, SipCallEvent finalEvent, ChannelWriter<SipCallEvent> writer)
    {
        bool wasCallee;
        ChannelWriter<SipTransferEvent>? transfer;
        lock (_gate)
        {
            wasCallee = _calls.TryGetValue(callId, out ActiveCall? call) && call.Role == CallRole.Callee;
            _transferSubscriptions.Remove(callId, out transfer);
            if (_transferTimeouts.Remove(callId, out CancellationTokenSource? timeout))
            {
                timeout.Cancel();
            }
        }

        // Серверную транзакцию снимаем вместе со звонком: ждать ACK на ответ по
        // завершённому вызову незачем, а её таймеры пережили бы сам звонок.
        if (wasCallee)
        {
            _transactions.ForgetServerInvite(callId);
        }

        if (transfer is not null)
        {
            transfer.TryWrite(new SipTransferEvent.Failed(0, SipTransferErrors.CallEnded));
            transfer.TryComplete();
        }

        RemoveCall(callId);

        string reason = finalEvent switch
        {
            SipCallEvent.Failed failed => failed.Reason,
            SipCallEvent.Ended ended => ended.Reason,
            _ => "завершён",
        };

        writer.TryWrite(new SipCallEvent.State(new SipCallState.Ended(reason)));
        writer.TryWrite(finalEvent);
        writer.TryComplete();
    }
}
