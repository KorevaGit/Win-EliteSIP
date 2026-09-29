using EliteSIP.Diagnostics;
using System.Windows.Threading;
using EliteSIP.App.Incoming;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.Audio;
using EliteSIP.CallHistory;
using EliteSIP.Lines;
using EliteSIP.MediaCore;
using EliteSIP.SipCore;
using EliteSIP.SipCore.Udp;

// Псевдонимы: транспорт называется одинаково у настроек и у SipCore, и без них
// имя двусмысленно в каждой строке этого файла.
using SettingsSite = EliteSIP.App.Settings.WorkplaceSite;
using SettingsTransport = EliteSIP.App.Settings.SipTransport;
using SignalingTransport = EliteSIP.SipCore.SipTransport;
using KnockSite = EliteSIP.SipCore.WorkplaceSite;

namespace EliteSIP.App.Shell;

/// <summary>
/// Телефон: то, что связывает окна с сигнализацией, звуком и историей.
/// </summary>
///
/// <remarks>
/// Это тот слой, который в оригинале назывался <c>AppModel</c>. Ниже него лежат
/// уже перенесённые пакеты — SipCore с транспортом (W2), MediaCore (W3),
/// тракт (W4), линии (W6), история (W7), — и ни один из них ничего не знает про
/// окна. Здесь они встречаются, и здесь же принимаются решения, которых нет ни
/// у одного из них порознь: когда поднимать медиа, что писать в историю и что
/// показать оператору вместо кода ответа.
///
/// <b>Входящий принимает человек, и только он.</b> Просьбе сервера снять трубку
/// самому (<c>X-Autoanswer</c>) приложение не подчиняется намеренно: автоответ
/// отдал бы лид пустому месту — ровно то, от чего приложение и защищает. Окно и
/// разбор попытки живут в <see cref="Incoming.IncomingCallPresenter"/>, здесь
/// остаётся то, что происходит после решения: 200 OK с нашим SDP и подъём
/// медиа — или 486, чтобы вызов вернулся в очередь следующему оператору, а не
/// висел до таймаута сервера.
/// </remarks>
public sealed class PhoneService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly PanelViewModel _panel;
    private readonly CallHistoryStore _history;
    private readonly IncomingCallPresenter _incoming;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;

    /// <summary>Гудки и рингтон. Живёт всё время работы: устройство берётся на звук.</summary>
    private readonly SignalSoundPlayer _sounds;

    private SocketSipTransport? _transport;
    private PortKnocker? _knocker;
    private SipUserAgent? _agent;
    private VoiceAudioBus? _bus;
    private LineController? _lines;
    private CancellationTokenSource? _running;
    private Task? _pump;

    /// <summary>Записи истории по Call-ID: их дописывают по ходу разговора.</summary>
    private readonly Dictionary<string, CallRecord> _records = [];

    /// <summary>Исходящий, на который ещё не ответили. <c>null</c> — такого нет.</summary>
    private string? _pendingCallId;

    /// <summary>
    /// Что слышно до ответа. Трогается под <see cref="_preAnswerGate"/>: пишет
    /// поток сигнализации, читает поток окна.
    /// </summary>
    private readonly Lock _preAnswerGate = new();

    /// <summary>Станция прислала 180 без SDP: гудки наши.</summary>
    private bool _ringbackWanted;

    /// <summary>Поток ранних медиа неотвеченного исходящего. Главнее наших гудков.</summary>
    private MediaSession? _earlySession;

    /// <summary>
    /// Тракт ранних медиа отказал. Второй раз до ответа его не дёргаем: каждая
    /// сверка линий повторяла бы ту же попытку и ту же строку в журнале.
    /// </summary>
    private bool _earlyAudioFailed;

    /// <summary>
    /// Медиа разговоров по Call-ID — и исходящих, и входящих.
    /// </summary>
    ///
    /// <remarks>
    /// До 11 сентября 2026 сессию входящего не держал никто: по отбою линия
    /// только отпускала звуковую карту, а сокет RTP, порт и приём оставались
    /// жить до выхода из программы. Потокобезопасный словарь — потому что
    /// принимают вызов на потоке окна, а кончается он на потоке сигнализации.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MediaSession> _sessions = new();

    public PhoneService(
        AppSettings settings,
        PanelViewModel panel,
        CallHistoryStore history,
        IncomingCallPresenter incoming,
        Dispatcher dispatcher,
        Action<string> log)
    {
        _settings = settings;
        _panel = panel;
        _history = history;
        _incoming = incoming;
        _dispatcher = dispatcher;
        _log = log;
        _sounds = new SignalSoundPlayer(log);
        SelfTest = new AudioSelfTest(configuration => new WasapiVoiceAudioEngine(configuration), log);

        _settings.Audio.PropertyChanged += OnAudioSettingsChanged;
    }

    /// <summary>
    /// «Проверить микрофон и звук». Живёт здесь, а не в окне настроек: только
    /// телефон знает, когда начинается звонок, и снять проверку обязан он — до
    /// того, как звонок откроет устройство.
    /// </summary>
    public AudioSelfTest SelfTest { get; }

    /// <summary>
    /// Идёт ли что-нибудь, чему нужен звук: разговор, неотвеченный исходящий,
    /// входящий на экране. Проверку в это время не запускают.
    /// </summary>
    public bool IsCallActive =>
        _pendingCallId is not null
        || _incoming.IsVisible
        || !_sessions.IsEmpty
        || (_lines?.Lines.Count ?? 0) > 0;

    /// <summary>
    /// Уровни разговора для шкал. <c>null</c> — тракт свободен.
    /// </summary>
    ///
    /// <remarks>
    /// «Голос» — то, что уходит в линию, поэтому при нажатой кнопке микрофона
    /// или на удержании он ноль: в линию в это время не уходит ничего, и шкала,
    /// пляшущая под голос, обещала бы обратное.
    /// </remarks>
    public AudioLevels? TakeCallLevels()
    {
        if (_bus?.TakeLevels() is not AudioLevels levels)
        {
            return null;
        }

        var owner = _sessions.Values.FirstOrDefault(session => session.OwnsAudio);
        return owner is null || owner.IsMicrophoneMuted ? levels with { Voice = 0 } : levels;
    }

    /// <summary>Снимает проверку ради звонка. Возвращается, когда устройство отпущено.</summary>
    private void AbortSelfTestForCall(string why)
    {
        if (SelfTest.Abort())
        {
            _log($"проверка звука снята: {why}");
        }
    }

    /// <summary>Настройки звука, которые тракт применяет на ходу.</summary>
    private static readonly HashSet<string> LiveAudioSettings =
    [
        nameof(AudioSettings.InputDeviceId),
        nameof(AudioSettings.OutputDeviceId),
        nameof(AudioSettings.MicrophoneGain),
        nameof(AudioSettings.PlaybackVolume),
        nameof(AudioSettings.AutomaticGainControl),
        nameof(AudioSettings.NoiseSuppression),
        nameof(AudioSettings.ReleasesDeviceWhenIdle),
    ];

    private readonly object _audioApplyGate = new();
    private bool _audioApplyPending;
    private bool _audioApplyRunning;

    /// <summary>Правка в «Звуке» — в идущий разговор, а не «со следующего звонка».</summary>
    ///
    /// <remarks>
    /// До 11 сентября 2026 сессия получала настройки один раз, при подъёме, и
    /// ползунки громкости и переключатели в разговоре не делали ничего.
    ///
    /// Применяется с фонового потока: смена устройства пересобирает тракт, а
    /// это до полусекунды, которые окно настроек стояло бы колом. Правки
    /// склеиваются — ползунок, который тянут, шлёт десятки изменений в
    /// секунду, а применять имеет смысл последнее.
    /// </remarks>
    private void OnAudioSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
    {
        if (change.PropertyName is not string name || !LiveAudioSettings.Contains(name))
        {
            return;
        }

        // Ползунок, сдвинутый во время проверки, действует сразу и виден на
        // шкале. Применение дешёвое (громкость и усиление читаются на такте),
        // поэтому прямо здесь.
        if (SelfTest.IsRunning)
        {
            SelfTest.Apply(AudioConfiguration());
        }

        // Между звонками применять некуда: тракт возьмёт настройки при подъёме.
        if (_sessions.IsEmpty)
        {
            return;
        }

        lock (_audioApplyGate)
        {
            _audioApplyPending = true;
            if (_audioApplyRunning)
            {
                return;
            }

            _audioApplyRunning = true;
        }

        _ = Task.Run(ApplyAudioSettings);
    }

    private void ApplyAudioSettings()
    {
        while (true)
        {
            lock (_audioApplyGate)
            {
                if (!_audioApplyPending)
                {
                    _audioApplyRunning = false;
                    return;
                }

                _audioApplyPending = false;
            }

            var audio = AudioConfiguration();
            foreach (var session in _sessions.Values)
            {
                try
                {
                    session.ApplyAudio(audio);
                }
                catch (Exception failure) when (failure is VoiceAudioException
                                                    or ObjectDisposedException
                                                    or InvalidOperationException)
                {
                    // Разговор мог кончиться между правкой и применением, а
                    // устройство — отказать. Ни то ни другое не повод ронять
                    // окно настроек.
                    _log($"настройки звука не применились на ходу: {failure.Message}");
                }
            }
        }
    }

    /// <summary>Служебные звуки: их же берёт окно входящего под рингтон.</summary>
    public SignalSoundPlayer Sounds => _sounds;

    /// <summary>Поднимает регистрацию по записанным настройкам.</summary>
    ///
    /// <remarks>
    /// Всё, чего не хватает для регистрации, называется в слоте беды словами, а
    /// не молчанием: панель без адреса или пароля выглядит сломанной, и оператор
    /// в этот момент звонит в поддержку вместо того, чтобы открыть настройки.
    /// </remarks>
    public async Task ConnectAsync()
    {
        await DisconnectAsync().ConfigureAwait(true);

        var account = _settings.Account;
        var pbx = _settings.Pbx;
        var address = account.Domain.Length > 0 ? account.Domain : pbx.OfficeAddress;

        if (account.Username.Length == 0 || address.Length == 0)
        {
            Trouble("TroubleNoAccount", opensSettings: true);
            return;
        }

        var password = _settings.Credentials.Password();
        if (password is null)
        {
            // Пароль не расшифровался — файл настроек принесли с чужой машины.
            // Сказать это словами: отказ регистрации без объяснений заставляет
            // разбирать сеть там, где сломан не она.
            Trouble("TroubleNoPassword", opensSettings: true);
            return;
        }

        var transport = pbx.Transport switch
        {
            SettingsTransport.Tcp => SignalingTransport.Tcp,
            SettingsTransport.Tls => SignalingTransport.Tls,
            _ => SignalingTransport.Udp,
        };

        var sipAccount = new SipAccount
        {
            Username = account.Username,
            DisplayName = account.DisplayName,
            Domain = address,
            ServerPort = (ushort)pbx.Port,
            Transport = transport,
            RegistrationExpires = pbx.RegistrationExpirySeconds,
        };

        _transport = new SocketSipTransport(sipAccount.SignalingEndpoint, transport, TrustOf(pbx));

        // Трасса SIP — по переключателю в «Обслуживании».
        //
        // Он существовал с самого начала и не был подключён ни к чему: его
        // читали только настройки, чтобы сохранить. Разбирать по журналу, что
        // именно мы ответили на INVITE, было нечем.
        //
        // Пароль в `Authorization` и `Proxy-Authorization` затирается: трасса
        // уезжает в поддержку архивом, а пароль SIP — тот же, которым машина
        // регистрируется.
        if (_settings.Maintenance.LogsSipTrace)
        {
            _transport.Trace = (outgoing, data) => _log(
                (outgoing ? "-> " : "<- ")
                + LogRedaction.Redact(System.Text.Encoding.UTF8.GetString(data.Span)));
        }

        // Стук по портам (W11): открыть себе дорогу до АТС перед регистрацией.
        // `null` означает «стучать не надо» — офисное место или выключенный
        // стук; агент в этом случае просто не делает лишнего шага.
        _knocker = PortKnocker.ForServer(
            address,
            account.Site is SettingsSite.Remote ? KnockSite.Remote : KnockSite.Office,
            _settings.PortKnock.ToSequence(),
            _log);

        if (_knocker is not null)
        {
            _log($"стук включён: {_settings.PortKnock.Steps.Count} шагов, "
                + $"{PortKnockPolicy.Explanation(address, KnockSite.Remote)}, "
                + $"задержка перед REGISTER {_settings.PortKnock.ToSequence().EstimatedDuration.TotalSeconds:0.#} с");
        }

        _agent = new SipUserAgent(
            sipAccount,
            new DigestAuthentication.Credentials(sipAccount.EffectiveAuthUsername, password),
            _transport,
            pathOpener: _knocker);

        // Тракт поднимается на настройках оператора, а не на значениях по
        // умолчанию.
        //
        // Здесь стоял `new VoiceAudioConfiguration()` — пустая конфигурация, —
        // и это означало, что выбранные в настройках микрофон и динамик не
        // доезжали до звука вовсе: тракт всегда открывал системные устройства
        // «для связи». На машине, где системный микрофон — веб-камера или
        // отключённый вход, оператора не слышал никто, а в настройках при этом
        // была выбрана правильная гарнитура. Туда же уходили и переключатели
        // обработки голоса: они сохранялись и ни на что не влияли.
        var audio = AudioConfiguration();

        _bus = new VoiceAudioBus(
            new WasapiVoiceAudioEngine(audio),
            settings => new WasapiVoiceAudioEngine(settings));

        _lines = new LineController(new SipUserAgentSignaling(_agent), _log);

        _running = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_agent, _running.Token));

        _panel.Registration = RegistrationState.Registering;
        _panel.Trouble = null;

        await _agent.StartAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Как проверять сертификат сервера.
    /// </summary>
    ///
    /// <remarks>
    /// Порядок разбора значим: «принимать любой» перекрывает пиннинг, а не
    /// дополняет его. Иначе прописанный отпечаток создавал бы видимость защиты
    /// там, где включено «принимать любой», — а это ровно та ошибка, ради
    /// которой панель этим полем и управляет.
    ///
    /// Пустая настройка отпечатка даёт системную проверку. Это умолчание, и оно
    /// же единственный правильный режим для боя: режим, который надо не забыть
    /// включить, однажды забудут.
    /// </remarks>
    private static SipTlsTrust TrustOf(PbxSettings pbx)
    {
        if (pbx.AcceptsAnyTlsCertificate)
        {
            return new SipTlsTrust.AcceptAnyCertificateInsecurely();
        }

        var fingerprints = pbx.PinnedCertificateFingerprint
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return fingerprints.Count > 0
            ? new SipTlsTrust.PinnedCertificateSha256(fingerprints)
            : new SipTlsTrust.System();
    }

    /// <summary>Снимает регистрацию и отпускает всё, что держит.</summary>
    public async Task DisconnectAsync()
    {
        if (_agent is null)
        {
            return;
        }

        // Порядок обратный подъёму: сначала разговоры, потом регистрация,
        // потом сокет. Снятая раньше разговоров регистрация оставляет сервер с
        // диалогами, о которых он думает, что они живы.
        if (_lines is not null)
        {
            await _lines.HangUpAllAsync().ConfigureAwait(true);
        }

        if (_running is not null)
        {
            await _running.CancelAsync().ConfigureAwait(true);
        }

        await _agent.StopAsync().ConfigureAwait(true);

        if (_pump is not null)
        {
            await _pump.ConfigureAwait(true);
        }

        _agent.Dispose();
        _transport?.Dispose();
        _knocker?.Dispose();
        _bus?.Dispose();
        _running?.Dispose();

        _agent = null;
        _transport = null;
        _knocker = null;
        _bus = null;
        _lines = null;
        _running = null;
        _pump = null;

        _panel.Registration = RegistrationState.Idle;
        _panel.CanPlaceCall = false;
        SyncLines();
    }

    /// <summary>Звонит по набранному номеру.</summary>
    public async Task PlaceCallAsync(string number)
    {
        number = number.Trim();
        if (_agent is null || _bus is null || _lines is null || number.Length == 0)
        {
            return;
        }

        // До всего, что может открыть устройство: гудков, ранних медиа, ответа.
        AbortSelfTestForCall("исходящий звонок");

        // Адрес для SDP берётся у агента, а не у сокета: это тот же адрес, что
        // в Contact, то есть внешний, сообщённый сервером в received. Локальный
        // адрес за NAT даст установленный звонок без звука — самую дорогую в
        // разборе неисправность из всех возможных.
        if (_agent.MediaAddress is not string mediaAddress)
        {
            Trouble("TroubleNoMediaAddress", opensSettings: false);
            return;
        }

        // Порт — на том же локальном адресе, что и SIP: иначе маршрут для RTP
        // Windows выбирает заново, и VPN с маршрутом до АТС уводит звук мимо
        // адреса, объявленного в SDP.
        (SessionDescription offer, RtpPortReservation reservation) = MediaSession.MakeOffer(
            mediaAddress,
            bindAddress: _agent.LocalSignalingAddress);
        SipOutgoingCall call = _agent.PlaceCall(number, offer.EncodedData());

        var record = new CallRecord
        {
            CallId = call.CallId,
            Direction = CallDirection.Outgoing,
            Number = number,
            ProfileId = _settings.Account.ProfileId,
            SipLogin = _settings.Account.Username,
        };

        // Запись заводится в момент начала звонка, а не в конце: так она
        // переживает принудительное завершение приложения, а оно посреди
        // разговора вполне реально.
        _history.Begin(record);
        _records[call.CallId] = record;

        // Панель узнаёт о вызове сейчас, а не по «ответили»: до этой строки она
        // выглядела свободной всё время гудков, и «Позвонить» нажималась
        // повторно, заводя второй вызов поверх первого.
        _pendingCallId = call.CallId;
        _panel.PendingNumber = number;
        _panel.CallStatus = Strings.Get("CallStatusDialing");

        _ = Task.Run(() => FollowCallAsync(call, offer, reservation, number));
    }

    /// <summary>Показывает входящий вызов оператору.</summary>
    ///
    /// <remarks>
    /// Запись истории заводится здесь, до всякого решения: непринятый вызов —
    /// это тоже событие смены, и пропущенный лид обязан остаться в истории,
    /// даже если приложение до конца разговора не дожило.
    ///
    /// Второй вызов, пришедший поверх показанного окна, отклоняется с 486:
    /// два окна входящего на экране — это выбор, которого оператор не делал, и
    /// оба лида в нём теряются одинаково.
    /// </remarks>
    private void Offer(SipIncomingCall call)
    {
        if (_agent is null || _bus is null || _lines is null || _incoming.IsVisible)
        {
            _ = _agent?.RejectIncomingCallAsync(call.CallId);
            _log($"входящий от {call.DisplayNumber} отклонён: показывать его некуда");
            return;
        }

        var subject = IncomingCallSubject.Classify(
            call.CallerNumber,
            call.CallerName,
            call.RequestsAutoAnswer,
            _settings.Account.Username,
            QueueTitle(call.CallerNumber));

        var record = new CallRecord
        {
            CallId = call.CallId,
            Direction = CallDirection.Incoming,
            Number = call.CallerNumber,
            DisplayName = call.CallerName,
            ProfileId = _settings.Account.ProfileId,
            SipLogin = _settings.Account.Username,
            WasDistribution = subject.Kind is IncomingCallKind.Queue,
        };

        _history.Begin(record);
        _records[call.CallId] = record;

        // Гудки исходящего — до рингтона: плеер служебных звуков один, и пока
        // в нём играли гудки, рингтон входящего не начинался вовсе. Вернутся
        // они сами, когда окно уберут (сверка линий → UpdatePreAnswerAudio).
        _sounds.StopRingback();

        // Рингтон открывает устройство следующей строкой — проверка уходит до него.
        AbortSelfTestForCall("входящий звонок");

        _incoming.Show(
            subject,
            _settings.IncomingCall.ToPolicy(),
            onAnswer: () => _ = AnswerAsync(call),
            onDecline: () => _ = DeclineAsync(call));

        // Звонящий вправе передумать, пока оператор тянется к кнопке. Окно в
        // этом случае надо убрать самим: нажимать в нём уже некуда, а лид,
        // отменённый секунду назад, оператор всё равно засчитает себе в отказ.
        _ = Task.Run(() => FollowIncomingAsync(call));
    }

    /// <summary>Название очереди из словаря администратора, если номер там есть.</summary>
    ///
    /// <remarks>
    /// Словарь остаётся уточнением поверх общего заголовка: на боевом диалплане
    /// он не срабатывает вовсе (номер очереди в CallerID не приезжает), но
    /// заказчик, поправивший диалплан, не должен из-за этого чинить ещё и клиент.
    /// </remarks>
    private string? QueueTitle(string callerNumber)
        => _settings.Queues.Queues
            .FirstOrDefault(queue => queue.Number.Length > 0 && queue.Number == callerNumber)?
            .Title;

    /// <summary>Принимает вызов: 200 OK с нашим SDP и подъём медиа.</summary>
    ///
    /// <remarks>
    /// Порядок из оригинала и обязателен: порт занимается и слушает <b>до</b>
    /// 200 OK — Asterisk начинает слать RTP сразу по ответу, не дожидаясь ACK.
    /// </remarks>
    private async Task AnswerAsync(SipIncomingCall call)
    {
        if (_agent is not SipUserAgent agent || _bus is null || _lines is null)
        {
            return;
        }

        if (agent.MediaAddress is not string mediaAddress)
        {
            // Тот же случай, что на исходящем: локальный адрес за NAT даёт
            // установленный звонок без звука — самую дорогую в разборе
            // неисправность из всех возможных. Лучше вернуть лид в очередь.
            Trouble("TroubleNoMediaAddress", opensSettings: false);
            await agent.RejectIncomingCallAsync(call.CallId).ConfigureAwait(true);
            return;
        }

        SessionDescription answer;
        NegotiatedMedia negotiated;
        RtpPortReservation reservation;

        try
        {
            (answer, negotiated, reservation) = MediaSession.MakeAnswer(
                SdpParser.Parse(call.Offer.Span),
                mediaAddress,
                bindAddress: agent.LocalSignalingAddress);
        }
        catch (Exception failure) when (failure is SdpParseException or SdpNegotiationException)
        {
            // 488 «предложение не подходит» — ровно наш случай, и это не то же
            // самое, что 486: сервер по нему видит, что вызов не подошёл нам, а
            // не что оператор занят.
            _log($"не договорились о медиа на входящем: {failure.Message}");
            await agent.RejectIncomingCallAsync(call.CallId, status: 488).ConfigureAwait(true);
            return;
        }

        if (!await agent.AnswerIncomingCallAsync(call.CallId, answer.EncodedData()).ConfigureAwait(true))
        {
            reservation.Release();
            _log("ответить на входящий не удалось: звонка уже нет");
            return;
        }

        try
        {
            var session = OpenSession(call.CallId, negotiated, reservation);

            await session.StartAsync(CancellationToken.None).ConfigureAwait(true);
            await _lines.AttachAsync(call.CallId, new MediaSessionLine(session, answer), call.DisplayNumber)
                .ConfigureAwait(true);
            WatchMedia(call.CallId, session);

            if (_records.TryGetValue(call.CallId, out var record))
            {
                _history.MarkAnswered(record.Id);
            }

            SyncLines();
        }
        catch (VoiceAudioException failure)
        {
            // Звонок уже принят, а тракта нет: молчать в линию хуже, чем
            // положить трубку и сказать словами, что чинить.
            _log($"тракт не поднялся на входящем: {failure.Message}");
            await CloseMediaAsync(call.CallId).ConfigureAwait(true);
            Trouble("TroubleNoAudio", opensSettings: true);
            await agent.HangUpAsync(call.CallId).ConfigureAwait(true);
        }
    }

    /// <summary>Отклоняет вызов по кнопке оператора.</summary>
    private async Task DeclineAsync(SipIncomingCall call)
    {
        if (_agent is not SipUserAgent agent)
        {
            return;
        }

        // 486, а не 603: при раздаче лидов первое возвращает вызов в очередь
        // следующему агенту, второе завершает его совсем.
        await agent.RejectIncomingCallAsync(call.CallId).ConfigureAwait(true);

        if (_records.TryGetValue(call.CallId, out var record))
        {
            _history.Finish(record.Id, "отклонён оператором", CallOutcome.Declined);
            _records.Remove(call.CallId);
        }
    }

    /// <summary>Ведёт входящий до конца: отмена звонящим, отбой, завершение.</summary>
    private async Task FollowIncomingAsync(SipIncomingCall call)
    {
        try
        {
            await foreach (var value in call.Events.ConfigureAwait(false))
            {
                switch (value)
                {
                    case SipCallEvent.Failed failure:
                        await CloseIncoming(
                            call.CallId,
                            SipCallErrors.DescribeCallFailure(failure.Status, failure.Reason),
                            CallOutcomes.ForFailure(failure.Status));
                        break;

                    case SipCallEvent.Ended ended:
                        // Пропущенным считается только тот вызов, на который не
                        // успели ответить: у отвеченного исход — сам разговор.
                        await CloseIncoming(
                            call.CallId,
                            ended.Reason,
                            _records.TryGetValue(call.CallId, out var record) && !record.IsAnswered
                                ? CallOutcome.Missed
                                : null);
                        break;

                    default:
                        break;
                }
            }
        }
        finally
        {
            // Отбой звучит только у состоявшегося разговора: непринятый
            // входящий кончается вместе со звонком, и второй звук поверх
            // смолкшего рингтона был бы лишним.
            var hadMedia = _sessions.ContainsKey(call.CallId);

            await CloseMediaAsync(call.CallId).ConfigureAwait(false);
            if (hadMedia)
            {
                _sounds.PlayHangUp(_settings.Audio.OutputDeviceId);
            }

            await _dispatcher.InvokeAsync(SyncLines);
        }
    }

    /// <summary>Убирает окно входящего и закрывает запись истории.</summary>
    private Task CloseIncoming(string callId, string reason, CallOutcome? outcome)
        => _dispatcher.InvokeAsync(() =>
        {
            _incoming.Hide();

            if (_records.Remove(callId, out var record))
            {
                _history.Finish(record.Id, reason, outcome);
            }

            SyncLines();
        }).Task;

    /// <summary>Кладёт трубку на активной линии.</summary>
    public async Task HangUpAsync()
    {
        if (_agent is null)
        {
            return;
        }

        // Сперва неотвеченный исходящий: линии у него ещё нет, и по
        // `ActiveCallId` его не найти — а «Завершить» во время гудков нажимают
        // чаще, чем в разговоре.
        if (_pendingCallId is string pending)
        {
            await _agent.HangUpAsync(pending).ConfigureAwait(true);
            return;
        }

        if (_lines?.ActiveCallId is string callId)
        {
            await _agent.HangUpAsync(callId).ConfigureAwait(true);
        }
    }

    public async Task ToggleHoldAsync()
    {
        if (_lines?.ActiveCallId is not string callId)
        {
            return;
        }

        try
        {
            await _lines.HoldAsync(callId, !_panel.IsOnHold).ConfigureAwait(true);
        }
        finally
        {
            // И при отказе: кнопка уже переключилась от щелчка, и вернуть её
            // может только сверка с линиями.
            SyncLines();
        }
    }

    /// <summary>Настройки звука оператора в том виде, в каком их понимает тракт.</summary>
    ///
    /// <remarks>
    /// Читается заново на каждый подъём тракта: устройства меняют между
    /// звонками, и запомненная конфигурация означала бы, что выбор гарнитуры
    /// вступает в силу только после перезапуска программы.
    /// </remarks>
    public VoiceAudioConfiguration AudioConfiguration() => new()
    {
        InputDeviceId = _settings.Audio.InputDeviceId,
        OutputDeviceId = _settings.Audio.OutputDeviceId,
        AutomaticGainControl = _settings.Audio.AutomaticGainControl,
        NoiseSuppression = _settings.Audio.NoiseSuppression,
        ReleasesDeviceWhenIdle = _settings.Audio.ReleasesDeviceWhenIdle,
        MicrophoneGain = (float)_settings.Audio.MicrophoneGain,
        PlaybackVolume = (float)_settings.Audio.PlaybackVolume,
    };

    public void ToggleMicrophone()
    {
        if (_lines is null)
        {
            return;
        }

        _lines.IsMicrophoneMuted = !_lines.IsMicrophoneMuted;
        _panel.IsMicrophoneMuted = _lines.IsMicrophoneMuted;
    }

    /// <summary>Отправляет тоны макроса в линию.</summary>
    public async Task SendMacroAsync(MacroViewModel macro)
    {
        if (_lines is null || macro.Tones.Length == 0)
        {
            return;
        }

        macro.IsBusy = true;
        try
        {
            var outcome = await _lines.SendDtmfAsync(new DtmfSequence(macro.Tones)).ConfigureAwait(true);
            if (outcome is not DtmfOutcome.Sent)
            {
                _log($"макрос «{macro.Title}» не ушёл: {outcome}");
            }
        }
        finally
        {
            macro.IsBusy = false;
        }
    }

    /// <summary>Собирает конференцию серверным кодом.</summary>
    ///
    /// <remarks>
    /// Кодом из настроек, а не догадкой: у каждой установки Asterisk он свой.
    /// Пустой код означает, что кнопка не работает, — и это честнее, чем
    /// послать наугад то, что сервер поймёт как что-то другое.
    /// </remarks>
    public async Task StartConferenceAsync()
    {
        var code = _settings.Pbx.ConferenceFeatureCode;
        if (_lines is null || code.Length == 0)
        {
            _log("конференция: код не задан в «Управлении»");
            return;
        }

        var outcome = await _lines.SendConferenceCodeAsync(code).ConfigureAwait(true);
        _panel.IsConferenceStarted = outcome is DtmfOutcome.Sent;
    }

    /// <summary>Переводит активную линию на другой номер.</summary>
    public async Task TransferAsync(string target)
    {
        if (_lines?.ActiveCallId is not string callId || target.Trim().Length == 0)
        {
            return;
        }

        _panel.IsTransferring = true;
        try
        {
            var result = await _lines.TransferAsync(callId, target.Trim()).ConfigureAwait(true);
            if (result.Succeeded)
            {
                _panel.CancelTransferEntry();

                if (_records.TryGetValue(callId, out var record))
                {
                    _history.MarkTransferred(record.Id);
                }
            }
            else
            {
                _log($"перевод не удался: {result.Reason}");
            }
        }
        finally
        {
            _panel.IsTransferring = false;
            SyncLines();
        }
    }

    /// <summary>Читает события агента и переводит их в состояние панели.</summary>
    private async Task PumpAsync(SipUserAgent agent, CancellationToken token)
    {
        try
        {
            await foreach (var value in agent.Events.WithCancellation(token).ConfigureAwait(false))
            {
                switch (value)
                {
                    case SipUserAgentEvent.Registration registration:
                        await _dispatcher.InvokeAsync(() => Apply(registration.State));
                        break;

                    case SipUserAgentEvent.Log log:
                        // Отладочный уровень — только с трассой SIP. Без неё он
                        // был девятью десятыми журнала: keep-alive каждые 25 с и
                        // OPTIONS раз в минуту, круглые сутки, — а разбирать
                        // звонок в этой толще было труднее, чем без неё.
                        if (log.Level is not SipLogLevel.Debug || _settings.Maintenance.LogsSipTrace)
                        {
                            _log($"[{log.Level}] {log.Message}");
                        }

                        break;

                    case SipUserAgentEvent.IncomingCall incoming:
                        await _dispatcher.InvokeAsync(() => Offer(incoming.Call));
                        break;

                    case SipUserAgentEvent.ChannelClosed closed:
                        await _dispatcher.InvokeAsync(() => Trouble("TroubleChannelClosed", opensSettings: false));
                        _log($"канал закрыт: {closed.Reason}");
                        break;

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Обычный выход: регистрацию снимают, поток событий закрывают.
        }
    }

    /// <summary>Ведёт один исходящий звонок от набора до отбоя.</summary>
    private async Task FollowCallAsync(
        SipOutgoingCall call,
        SessionDescription offer,
        RtpPortReservation reservation,
        string number)
    {
        MediaSession? session = null;
        var answered = false;

        try
        {
            await foreach (var value in call.Events.ConfigureAwait(false))
            {
                switch (value)
                {
                    case SipCallEvent.Answered response:
                        MediaSession? early;
                        lock (_preAnswerGate)
                        {
                            // Гудки снимаются здесь, а не в `finally`: тракт
                            // разговора поднимается следующей строкой, и гудок,
                            // доигрывающий поверх первого «алло», слышен как
                            // сбой связи.
                            _ringbackWanted = false;
                            early = _earlySession;
                            _earlySession = null;
                            _earlyAudioFailed = false;
                        }

                        _sounds.StopRingback();

                        // Ранний поток продолжается, а не пересоздаётся: на
                        // границе «гудки → алло» не должно пропасть ни кадра.
                        session = early is not null
                            ? await ContinueEarlyMediaAsync(early, response, offer, call.CallId, number)
                                .ConfigureAwait(false)
                            : await OpenMediaAsync(response, offer, reservation, call.CallId, number)
                                .ConfigureAwait(false);

                        if (session is null)
                        {
                            // Ранний поток, который не удалось продолжить, всё
                            // равно надо снять: `finally` смотрит на `session`.
                            session = early;
                            await _agent!.HangUpAsync(call.CallId).ConfigureAwait(false);
                            break;
                        }

                        answered = true;
                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (_records.TryGetValue(call.CallId, out var record))
                            {
                                _history.MarkAnswered(record.Id);
                            }

                            // Ответили — вызов перестал быть ожидаемым и стал
                            // линией; дальше состояние держит `SyncLines`.
                            ClearPending(call.CallId);
                            SyncLines();
                        });

                        break;

                    // «Звонит». Прежде это событие попадало в `default` и
                    // терялось, а оно единственное, по которому видно, что
                    // вызов пошёл. Звука оно само не решает: что слышно,
                    // говорят два события ниже.
                    case SipCallEvent.State { Value: SipCallState.Ringing }:
                        await _dispatcher.InvokeAsync(
                            () => _panel.CallStatus = Strings.Get("CallStatusRinging"));
                        break;

                    // 180 без SDP: станция гудков не даст, играем сами.
                    // Тишина после «Позвонить» читается как несостоявшийся
                    // звонок, и оператор кладёт трубку раньше, чем на той
                    // стороне подойдут.
                    case SipCallEvent.LocalRingback:
                        lock (_preAnswerGate)
                        {
                            _ringbackWanted = true;
                        }

                        await _dispatcher.InvokeAsync(UpdatePreAnswerAudio);
                        break;

                    // 18x с SDP: станция играет сама — гудки, IVR,
                    // «абонент недоступен». Поток поднимается сразу, с
                    // заглушённым микрофоном.
                    case SipCallEvent.EarlyMedia progress:
                        session = await FollowEarlyMediaAsync(progress, offer, reservation, call.CallId, session)
                            .ConfigureAwait(false);

                        await _dispatcher.InvokeAsync(UpdatePreAnswerAudio);
                        break;

                    case SipCallEvent.Failed failure:
                        await Finish(
                            call.CallId,
                            SipCallErrors.DescribeCallFailure(failure.Status, failure.Reason),
                            CallOutcomes.ForFailure(failure.Status));
                        break;

                    case SipCallEvent.Ended ended:
                        await Finish(call.CallId, ended.Reason, outcome: null);
                        break;

                    default:
                        break;
                }
            }
        }
        finally
        {
            // Второй раз за тот же вызов — намеренно: сюда приходят и отбой, и
            // отказ, и обрыв потока событий, и после любого из них гудки
            // обязаны замолчать. Снятие тишины ничего не стоит.
            lock (_preAnswerGate)
            {
                _ringbackWanted = false;
                _earlySession = null;
                _earlyAudioFailed = false;
            }

            _sounds.StopRingback();

            if (session is not null)
            {
                await CloseMediaAsync(call.CallId).ConfigureAwait(false);
            }
            else if (!answered)
            {
                reservation.Release();
            }

            // Звук отбоя — после снятия тракта, а не до: иначе он лёг бы
            // поверх последнего слова собеседника.
            _sounds.PlayHangUp(_settings.Audio.OutputDeviceId);

            await _dispatcher.InvokeAsync(SyncLines);
        }
    }

    /// <summary>Поднимает медиа по ответу сервера и заводит линию.</summary>
    private async Task<MediaSession?> OpenMediaAsync(
        SipCallEvent.Answered response,
        SessionDescription offer,
        RtpPortReservation reservation,
        string callId,
        string number)
    {
        try
        {
            var answer = SdpParser.Parse(response.Body.Span);
            var negotiated = SdpNegotiator.ResolveAnswer(answer, offer);

            var session = OpenSession(callId, negotiated, reservation);

            await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await _lines!.AttachAsync(callId, new MediaSessionLine(session, offer), number).ConfigureAwait(false);

            // Станция может ответить сразу на удержании (a=sendonly): тогда
            // микрофон молчит, даже если кнопку никто не нажимал.
            _lines.ApplyRemoteMedia(callId, negotiated);

            WatchMedia(callId, session);
            return session;
        }
        catch (Exception failure) when (failure is SdpParseException or SdpNegotiationException)
        {
            _log($"не договорились о медиа: {failure.Message}");
        }
        catch (VoiceAudioException failure)
        {
            // Звуковая карта — единственное, чего у машины может не быть вовсе.
            // Сигнализация при этом жива, поэтому звонок надо положить, а не
            // бросить: иначе линия останется занятой до таймаута сервера.
            _log($"тракт не поднялся: {failure.Message}");
            await CloseMediaAsync(callId).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => Trouble("TroubleNoAudio", opensSettings: true));
        }

        return null;
    }

    /// <summary>
    /// Поднимает поток ранних медиа или уточняет уже поднятый.
    /// </summary>
    ///
    /// <returns>Сессию раннего потока — прежнюю или новую; <c>null</c>, если её нет.</returns>
    ///
    /// <remarks>
    /// Повтор 183 с тем же телом сюда не доходит — его отсеивает SipCore. Сюда
    /// приходит либо первое ранее медиа, либо другое тело: станция сменила адрес,
    /// ключ или кодек. Во втором случае поток перенастраивается на том же
    /// локальном порту, объявленном в нашем предложении, а не пересоздаётся.
    ///
    /// Ничего из этого не роняет вызов. Не разобрался SDP или не поднялся
    /// тракт — оператор останется без гудков станции, но звонок дойдёт до
    /// ответа, и там будет вторая попытка с окончательным SDP.
    /// </remarks>
    private async Task<MediaSession?> FollowEarlyMediaAsync(
        SipCallEvent.EarlyMedia early,
        SessionDescription offer,
        RtpPortReservation reservation,
        string callId,
        MediaSession? current)
    {
        NegotiatedMedia negotiated;
        try
        {
            negotiated = SdpNegotiator.ResolveAnswer(SdpParser.Parse(early.Body.Span), offer);
        }
        catch (Exception failure) when (failure is SdpParseException or SdpNegotiationException)
        {
            _log($"ранние медиа не приняты: {failure.Message}");
            return current;
        }

        if (current is not null)
        {
            try
            {
                var outcome = current.Adopt(negotiated);
                _log($"ранние медиа уточнены ({early.Status}): {outcome}");
            }
            catch (InvalidOperationException failure)
            {
                _log($"ранние медиа не уточнились: {failure.Message}");
            }

            return current;
        }

        var session = OpenSession(callId, negotiated, reservation);

        // Звук сейчас у другой линии — оператор говорит, пока исходящий
        // звонит. Тракт у разговора не отбираем: поток принимается, а в ухо
        // пойдёт, когда та линия кончится (см. UpdatePreAnswerAudio).
        var claimAudio = _lines?.ActiveCallId is null;

        var audioFailed = false;
        try
        {
            await session.StartListeningAsync(claimAudio).ConfigureAwait(false);
        }
        catch (VoiceAudioException failure)
        {
            // Поток остаётся: порт объявлен в нашем предложении, и ответ станции
            // придёт на него же. Без звука до ответа звонок жив; на ответе
            // слой линий попробует тракт ещё раз и, если снова откажет,
            // звонок положат со словами, что чинить.
            _log($"тракт не поднялся на ранних медиа: {failure.Message}");
            audioFailed = true;
        }

        lock (_preAnswerGate)
        {
            _earlySession = session;
            _earlyAudioFailed = audioFailed;
        }

        _log($"ранние медиа ({early.Status}): поток поднят, микрофон заглушён"
            + (claimAudio ? string.Empty : ", звук у другой линии"));
        return session;
    }

    /// <summary>
    /// Продолжает ранний поток как разговор: 200 OK пришёл после 183 с SDP.
    /// </summary>
    ///
    /// <remarks>
    /// Поток не пересоздаётся. Ответ с тем же SDP не меняет в нём ничего, другой
    /// адрес или ключи перенастраивают его на том же порту, другой кодек
    /// пересобирает формат — всё это <see cref="MediaSession.Adopt"/>. Пустое
    /// тело при поднятом раннем потоке значит «всё как было».
    ///
    /// Микрофон открывает слой линий, а не этот код: при заведении линии он
    /// сводит кнопку, своё удержание и серверное — о кнопке сессия не знает, и
    /// нажатую во время гудков она бы проигнорировала.
    /// </remarks>
    private async Task<MediaSession?> ContinueEarlyMediaAsync(
        MediaSession early,
        SipCallEvent.Answered response,
        SessionDescription offer,
        string callId,
        string number)
    {
        try
        {
            var negotiated = early.Negotiated;

            if (!response.Body.IsEmpty)
            {
                negotiated = SdpNegotiator.ResolveAnswer(SdpParser.Parse(response.Body.Span), offer);
                var outcome = early.Adopt(negotiated);
                _log($"ответ продолжает ранний поток: {outcome}");
            }
            else
            {
                _log("ответ без SDP продолжает ранний поток как есть");
            }

            await _lines!.AttachAsync(callId, new MediaSessionLine(early, offer), number).ConfigureAwait(false);

            if (negotiated is not null)
            {
                _lines.ApplyRemoteMedia(callId, negotiated);
            }

            WatchMedia(callId, early);
            return early;
        }
        catch (Exception failure) when (failure is SdpParseException
                                            or SdpNegotiationException
                                            or InvalidOperationException)
        {
            _log($"не договорились о медиа в ответе: {failure.Message}");
        }
        catch (VoiceAudioException failure)
        {
            _log($"тракт не поднялся: {failure.Message}");
            await _dispatcher.InvokeAsync(() => Trouble("TroubleNoAudio", opensSettings: true));
        }

        return null;
    }

    /// <summary>
    /// Сводит, что должно быть слышно до ответа: поток станции, наши гудки или
    /// тишина.
    /// </summary>
    ///
    /// <remarks>
    /// Зовётся на потоке окна — на каждое событие вызова и на каждую сверку
    /// линий. Правило одно на все поводы:
    ///
    /// <list type="bullet">
    ///   <item><description>звук у другой линии (оператор принял входящий, пока
    ///   звонит исходящий) или на экране входящий с рингтоном — молчим;</description></item>
    ///   <item><description>есть ранние медиа — слышен поток станции, наших гудков
    ///   нет;</description></item>
    ///   <item><description>станция прислала 180 без SDP — наши гудки в устройство
    ///   разговора;</description></item>
    ///   <item><description>иначе тишина.</description></item>
    /// </list>
    ///
    /// Настройка звуков клавиш сюда не входит намеренно: гудки — не подсказка
    /// нажатия, а единственный признак того, что вызов идёт.
    /// </remarks>
    private void UpdatePreAnswerAudio()
    {
        bool ringback;
        bool audioFailed;
        MediaSession? early;

        lock (_preAnswerGate)
        {
            ringback = _ringbackWanted;
            early = _earlySession;
            audioFailed = _earlyAudioFailed;
        }

        var otherLineActive = _lines?.ActiveCallId is not null;

        if (early is not null || !ringback || otherLineActive || _incoming.IsVisible)
        {
            _sounds.StopRingback();
        }
        else
        {
            _sounds.StartRingback(_settings.Audio.OutputDeviceId);
        }

        // Другая линия кончилась, а исходящий всё ещё на ранних медиа — вернуть
        // ему звук. Микрофон при этом остаётся заглушённым. Подъём тракта стоит
        // сотни миллисекунд, поэтому в фоне, а не на потоке окна.
        if (early is not null && !audioFailed && !otherLineActive && !early.OwnsAudio)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    lock (_preAnswerGate)
                    {
                        if (!ReferenceEquals(_earlySession, early))
                        {
                            return;
                        }
                    }

                    early.ResumeListening();
                }
                catch (Exception failure) when (failure is VoiceAudioException or ObjectDisposedException)
                {
                    _log($"ранним медиа звук не вернулся: {failure.Message}");
                }
            });
        }
    }

    /// <summary>Заводит медиа разговора на настройках оператора.</summary>
    ///
    /// <remarks>
    /// Настройки звука передаются сессии, а не только тракту при подъёме
    /// регистрации. До 11 сентября 2026 сессия получала пустую конфигурацию:
    /// шина при каждом захвате перестраивает тракт под конфигурацию
    /// захватившего, и выбранные гарнитура, АРУ, усиление и громкость
    /// затирались умолчаниями на первом же звонке.
    /// </remarks>
    private MediaSession OpenSession(string callId, NegotiatedMedia negotiated, RtpPortReservation reservation)
    {
        var session = new MediaSession(negotiated, reservation, _bus!, AudioConfiguration())
        {
            OnDiagnostic = message => _log($"медиа: {message}"),
            OnTransportFailure = reason => _log($"транспорт медиа: {reason}"),

            // События тракта: подъём, перезапуск, поломка.
            //
            // Не были подписаны ни к чему, и это стоило выпуска 0.1.1: тракт
            // падал на каждом звонке, докладывал о поломке — и доклад уходил в
            // пустоту. В журнале оставался разговор без единой строки о звуке,
            // а у собеседника тишина без объяснения.
            OnAudioEvent = value => _log($"звук: {value}"),
        };

        _sessions[callId] = session;
        return session;
    }

    /// <summary>Срез звука через пять секунд разговора.</summary>
    ///
    /// <remarks>
    /// Итог при отбое есть и так, но жалоба «не слышно собеседника» приходит
    /// посреди разговора, и оператор кладёт трубку по-разному. Пять секунд —
    /// достаточно, чтобы пакеты пошли и тракт разогрелся; «принято 0» в этой
    /// строке значит, что звук собеседника до машины не дошёл вовсе.
    /// </remarks>
    private void WatchMedia(string callId, MediaSession session)
        => _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (_sessions.TryGetValue(callId, out var current) && ReferenceEquals(current, session))
            {
                _log($"медиа через 5 с: {session.Summary()}");
            }
        });

    /// <summary>Снимает медиа разговора: итог в журнал, линия, сокет, порт.</summary>
    ///
    /// <remarks>
    /// Итог пишется до снятия линии: сняв её, сессия отпускает тракт, и уровни
    /// звука спросить уже не у кого.
    /// </remarks>
    private async Task CloseMediaAsync(string callId)
    {
        _sessions.TryRemove(callId, out var session);

        if (session is not null)
        {
            _log($"медиа, итог разговора: {session.Summary()}");
        }

        if (_lines is not null)
        {
            await _lines.DetachAsync(callId).ConfigureAwait(false);
        }

        if (session is not null)
        {
            await session.StopAsync().ConfigureAwait(false);
            session.Dispose();
        }
    }

    private Task Finish(string callId, string reason, CallOutcome? outcome)
        => _dispatcher.InvokeAsync(() =>
        {
            if (_records.Remove(callId, out var record))
            {
                _history.Finish(record.Id, reason, outcome);
            }

            ClearPending(callId);
            SyncLines();
        }).Task;

    /// <summary>Снимает отметку о неотвеченном исходящем, если она про этот вызов.</summary>
    ///
    /// <remarks>
    /// Со сверкой по номеру вызова, а не безусловно: пока идут гудки одного,
    /// завершиться может другой — например, тот, что оператор только что
    /// положил, — и безусловная очистка стёрла бы состояние живого вызова.
    /// </remarks>
    private void ClearPending(string callId)
    {
        if (_pendingCallId == callId)
        {
            _pendingCallId = null;
            _panel.PendingNumber = null;
        }
    }

    /// <summary>Переводит состояние регистрации в то, что видно на панели.</summary>
    private void Apply(SipRegistrationState state)
    {
        switch (state)
        {
            case SipRegistrationState.Registered:
                _panel.Registration = RegistrationState.Registered;
                _panel.CanPlaceCall = true;
                _panel.Trouble = null;
                break;

            case SipRegistrationState.Registering:
            case SipRegistrationState.Unregistering:
                _panel.Registration = RegistrationState.Registering;
                break;

            case SipRegistrationState.Failed failed:
                _panel.Registration = RegistrationState.Failed;
                _panel.CanPlaceCall = false;

                // Причина отказа — та, что разобрал SipCore: «неверный пароль»
                // и «сервер не отвечает» чинятся разным, и слот беды обязан их
                // различать. Кода ответа здесь нет намеренно — он в журнале.
                _panel.Trouble = new Trouble(
                    failed.Reason,
                    "exclamationmark.triangle",
                    IsFailure: true,
                    OpensSettings: true);
                break;

            default:
                _panel.Registration = RegistrationState.Idle;
                _panel.CanPlaceCall = false;
                break;
        }

        _panel.StatusTitle = _settings.Account.Username.Length > 0
            ? _settings.Account.Username
            : "—";

        _panel.StatusLabel = ProfileLabel(_settings);
    }

    /// <summary>Подпись профиля рядом с добавочным.</summary>
    ///
    /// <remarks>
    /// Подпись из ключа — поле «сотрудник» пакета активации, которое ложится в
    /// <c>Account.DisplayName</c>, — а не название предустановки. В 0.1.42–0.1.49
    /// здесь стояло имя предустановки («Менеджер»): оно одно на весь отдел и
    /// не отвечает на вопрос «чей это телефон».
    /// </remarks>
    internal static string? ProfileLabel(AppSettings settings)
    {
        var name = settings.Account.DisplayName.Trim();
        return name.Length > 0 && name != settings.Account.Username ? name : null;
    }

    /// <summary>Переносит линии из слоя линий в панель.</summary>
    private void SyncLines()
    {
        _panel.Lines.Clear();

        if (_lines is null)
        {
            _panel.ActiveLine = null;
            _panel.CanSendDtmf = false;
            _panel.IsOnHold = false;
            _panel.ResyncToggles();
            return;
        }

        CallLineViewModel? active = null;
        foreach (var line in _lines.Lines)
        {
            var view = new CallLineViewModel
            {
                Title = line.Peer,
                SecondaryNumber = line.Peer,
                IsActive = line.IsActive,
                IsOnHold = line.IsHeldByOperator || line.IsHeldByServer,
                ConnectedAt = _records.Values
                    .FirstOrDefault(record => record.CallId == line.CallId)?.AnsweredAt,
            };

            view.Status = view.IsOnHold
                ? Strings.Get("PanelResume")
                : Strings.Get("CallStatusTalking");

            _panel.Lines.Add(view);
            if (line.IsActive)
            {
                active = view;
            }
        }

        _panel.ActiveLine = active;
        _panel.CallStatus = active?.Status ?? string.Empty;
        _panel.IsOnHold = active?.IsOnHold ?? false;
        _panel.CanSendDtmf = active is not null;
        _panel.IsMicrophoneMuted = _lines.IsMicrophoneMuted;
        _panel.ResyncToggles();

        // Линии сменились — могла стать активной другая, пока исходящий ещё
        // звонит, или кончиться та, что забирала у него звук.
        UpdatePreAnswerAudio();
    }

    private void Trouble(string key, bool opensSettings)
        => _panel.Trouble = new Trouble(
            Strings.Get(key),
            "exclamationmark.triangle",
            IsFailure: true,
            opensSettings);

    public void Dispose()
    {
        // Синхронно: приложение закрывается, и ждать снятия регистрации дольше
        // мгновения нельзя — иначе выход выглядит зависанием.
        _settings.Audio.PropertyChanged -= OnAudioSettingsChanged;
        SelfTest.Dispose();
        _lines = null;
        _running?.Cancel();
        _agent?.Dispose();
        _transport?.Dispose();
        _bus?.Dispose();
        _sounds.Dispose();
        _running?.Dispose();
    }
}
