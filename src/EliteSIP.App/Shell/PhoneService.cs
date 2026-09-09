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
using SettingsTransport = EliteSIP.App.Settings.SipTransport;
using SignalingTransport = EliteSIP.SipCore.SipTransport;

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

    private SocketSipTransport? _transport;
    private SipUserAgent? _agent;
    private VoiceAudioBus? _bus;
    private LineController? _lines;
    private CancellationTokenSource? _running;
    private Task? _pump;

    /// <summary>Записи истории по Call-ID: их дописывают по ходу разговора.</summary>
    private readonly Dictionary<string, CallRecord> _records = [];

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
    }

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
        _agent = new SipUserAgent(
            sipAccount,
            new DigestAuthentication.Credentials(sipAccount.EffectiveAuthUsername, password),
            _transport);

        _bus = new VoiceAudioBus(
            new WasapiVoiceAudioEngine(new VoiceAudioConfiguration()),
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
        _bus?.Dispose();
        _running?.Dispose();

        _agent = null;
        _transport = null;
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

        // Адрес для SDP берётся у агента, а не у сокета: это тот же адрес, что
        // в Contact, то есть внешний, сообщённый сервером в received. Локальный
        // адрес за NAT даст установленный звонок без звука — самую дорогую в
        // разборе неисправность из всех возможных.
        if (_agent.MediaAddress is not string mediaAddress)
        {
            Trouble("TroubleNoMediaAddress", opensSettings: false);
            return;
        }

        (SessionDescription offer, RtpPortReservation reservation) = MediaSession.MakeOffer(mediaAddress);
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
                mediaAddress);
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
            var session = new MediaSession(negotiated, reservation, _bus)
            {
                OnDiagnostic = message => _log($"медиа: {message}"),
                OnTransportFailure = reason => _log($"транспорт медиа: {reason}"),
            };

            await session.StartAsync(CancellationToken.None).ConfigureAwait(true);
            await _lines.AttachAsync(call.CallId, new MediaSessionLine(session, answer), call.DisplayNumber)
                .ConfigureAwait(true);

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
            if (_lines is not null)
            {
                await _lines.DetachAsync(call.CallId).ConfigureAwait(false);
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
        if (_lines?.ActiveCallId is string callId && _agent is not null)
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

        await _lines.HoldAsync(callId, !_panel.IsOnHold).ConfigureAwait(true);
        SyncLines();
    }

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
                        _log($"[{log.Level}] {log.Message}");
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
                        session = await OpenMediaAsync(response, offer, reservation, call.CallId, number)
                            .ConfigureAwait(false);

                        if (session is null)
                        {
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

                            SyncLines();
                        });

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
            if (session is not null)
            {
                if (_lines is not null)
                {
                    await _lines.DetachAsync(call.CallId).ConfigureAwait(false);
                }

                await session.StopAsync().ConfigureAwait(false);
                session.Dispose();
            }
            else if (!answered)
            {
                reservation.Release();
            }

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

            var session = new MediaSession(negotiated, reservation, _bus!)
            {
                OnDiagnostic = message => _log($"медиа: {message}"),
                OnTransportFailure = reason => _log($"транспорт медиа: {reason}"),
            };

            await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await _lines!.AttachAsync(callId, new MediaSessionLine(session, offer), number).ConfigureAwait(false);
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
            await _dispatcher.InvokeAsync(() => Trouble("TroubleNoAudio", opensSettings: true));
        }

        return null;
    }

    private Task Finish(string callId, string reason, CallOutcome? outcome)
        => _dispatcher.InvokeAsync(() =>
        {
            if (_records.Remove(callId, out var record))
            {
                _history.Finish(record.Id, reason, outcome);
            }

            SyncLines();
        }).Task;

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
    }

    /// <summary>Переносит линии из слоя линий в панель.</summary>
    private void SyncLines()
    {
        _panel.Lines.Clear();

        if (_lines is null)
        {
            _panel.ActiveLine = null;
            _panel.CanSendDtmf = false;
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
        _lines = null;
        _running?.Cancel();
        _agent?.Dispose();
        _transport?.Dispose();
        _bus?.Dispose();
        _running?.Dispose();
    }
}
