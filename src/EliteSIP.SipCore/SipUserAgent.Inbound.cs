using System.Globalization;
using System.Threading.Channels;

namespace EliteSIP.SipCore;

/// <summary>Входящая сторона: чужие запросы, входящий звонок и ответы на них.</summary>
public sealed partial class SipUserAgent
{
    /// <summary>
    /// Принимает входящий INVITE и поднимает шум наверх.
    ///
    /// Быстрый порядок ответов важен: 100 гасит ретрансмиссии INVITE (на UDP они
    /// начинаются через полсекунды), 180 включает гудки у звонящего. Всё, что
    /// дольше, — уже решение оператора, и торопить его нечем.
    /// </summary>
    private async Task HandleInviteAsync(SipRequest request)
    {
        // Повторный INVITE внутри установленного диалога — это удержание или
        // смена медиа. Искать надо по всем линиям: пересогласовать сервер может
        // ту, которая стоит на удержании, а не ту, где идёт разговор.
        if (MatchingCall(request) is ActiveCall existing)
        {
            await HandleReinviteAsync(request, existing).ConfigureAwait(false);
            return;
        }

        // To-tag означает запрос внутри уже существующего диалога. Если тройка
        // идентификаторов не совпала ни с одной линией, это не новый звонок и не
        // «занято», а неизвестный диалог.
        if (request.To?.Tag is not null)
        {
            await RespondToInviteQuietlyAsync(request, MakeSimpleResponse(request, 481)).ConfigureAwait(false);
            Log(SipLogLevel.Warning, "<- повторный INVITE с чужими тегами, ответили 481");
            return;
        }

        // Свободные линии заводит только оператор — консультацией или третьим
        // участником конференции. Входящий вызов занятому оператору по-прежнему
        // получает «занято»: у раздачи лидов иначе нет повода отдать вызов
        // следующему агенту, а оператор с чужим лидом в ухе разговаривает с
        // текущим клиентом.
        bool busy;
        lock (_gate)
        {
            busy = _calls.Count > 0;
        }
        if (busy)
        {
            await RespondToInviteQuietlyAsync(request, MakeSimpleResponse(request, 486)).ConfigureAwait(false);
            Log(SipLogLevel.Info, "<- INVITE, ответили 486: линия занята");
            return;
        }

        if (request.CallId is not string callId || request.TopVia?.Branch is not string branch)
        {
            await RespondToInviteQuietlyAsync(request, MakeSimpleResponse(request, 400)).ConfigureAwait(false);
            return;
        }

        // Без Contact диалог не собрать — значит и отвечать не на что: BYE
        // отправлять будет некуда. Отказ здесь честнее, чем разговор, который
        // невозможно завершить.
        if (request.Contacts.Count == 0)
        {
            SipResponse badRequest = MakeSimpleResponse(request, 400, "Missing Contact");
            await RespondToInviteQuietlyAsync(request, badRequest).ConfigureAwait(false);
            Log(SipLogLevel.Warning, "<- INVITE без Contact, отклонён");
            return;
        }

        string callTag = SipToken.Tag();
        Channel<SipCallEvent> events = SipCallEventChannel.Create();
        NameAddress? from = request.From;

        Add(new ActiveCall
        {
            Role = CallRole.Callee,
            CallId = callId,
            Peer = from?.Uri.User ?? string.Empty,
            LocalTag = callTag,
            Branch = branch,
            InviteSequence = request.CSeq?.Number ?? 1,
            Writer = events.Writer,
            State = new SipCallState.Incoming(),
            InviteRequest = request,
        });

        var trying = new SipResponse(100, headers: ResponseHeaders(request, callTag));
        await RespondToInviteQuietlyAsync(request, trying).ConfigureAwait(false);

        var ringing = new SipResponse(180, headers: ResponseHeaders(request, callTag));
        ringing.Headers.Append(SipHeaderName.Allow, AllowedMethods);
        ringing.Headers.Append(SipHeaderName.Supported, SupportedOptionTags);
        ringing.Headers.Append(SipHeaderName.UserAgent, _userAgentName);
        await RespondToInviteQuietlyAsync(request, ringing).ConfigureAwait(false);

        var call = new SipIncomingCall
        {
            CallId = callId,
            CallerNumber = from?.Uri.User ?? string.Empty,
            CallerName = from?.DisplayName,
            RequestsAutoAnswer = RequestsAutoAnswer(request),
            AsksForAutoAnswer = AsksForAutoAnswer(request),
            CalledNumber = request.To?.Uri.User ?? _account.Username,
            Offer = request.Body,
            OfferContentType = request.ContentType,
            Events = events.Reader.ReadAllAsync(),
        };

        Log(SipLogLevel.Info, $"<- INVITE от {call.DisplayNumber}, ответили 180");
        events.Writer.TryWrite(new SipCallEvent.State(new SipCallState.Incoming()));
        _events.Writer.TryWrite(new SipUserAgentEvent.IncomingCall(call));
    }

    /// <summary>
    /// Просит ли сервер снять трубку сам.
    ///
    /// Заголовок нестандартный и пишется у разных АТС по-разному, поэтому
    /// сравнение нечувствительно к регистру с обеих сторон: имя приводит к
    /// каноническому виду набор заголовков, значение — мы здесь.
    ///
    /// Всё, кроме утвердительного значения, считается отсутствием просьбы:
    /// <c>X-Autoanswer: FALSE</c> — это явное «не надо», и толковать его как
    /// признак раздачи было бы прямо наоборот смыслу.
    /// </summary>
    /// <summary>
    /// Просит ли вызов автоответа любым из принятых способов — для режима
    /// автоподъёма «по заголовку».
    /// </summary>
    ///
    /// <remarks>
    /// Шире, чем <see cref="RequestsAutoAnswer"/>: тот — признак раздачи лида
    /// на диалплане заказчика, и расширять его значило бы показывать раздачей
    /// всякий интерком. Здесь — всё, чем АТС просят снять трубку:
    /// <list type="bullet">
    /// <item><c>X-Autoanswer: true</c> — диалплан заказчика;</item>
    /// <item><c>Call-Info: …;answer-after=N</c> — Asterisk/FreePBX, Polycom;</item>
    /// <item><c>Alert-Info</c> с <c>auto-answer</c>, <c>autoanswer</c> или
    /// <c>Ring Answer</c> — Asterisk-интерком, Yealink, Snom;</item>
    /// <item><c>Answer-Mode</c>/<c>Priv-Answer-Mode: Auto</c> — RFC 5373.</item>
    /// </list>
    /// </remarks>
    internal static bool AsksForAutoAnswer(SipRequest request)
    {
        if (RequestsAutoAnswer(request))
        {
            return true;
        }

        foreach (var value in request.Headers.Values("Call-Info"))
        {
            if (value.Contains("answer-after", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var value in request.Headers.Values("Alert-Info"))
        {
            var squeezed = value.Replace("-", string.Empty, StringComparison.Ordinal)
                                .Replace(" ", string.Empty, StringComparison.Ordinal);
            if (squeezed.Contains("autoanswer", StringComparison.OrdinalIgnoreCase)
                || squeezed.Contains("ringanswer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var name in new[] { "Answer-Mode", "Priv-Answer-Mode" })
        {
            if (request.Headers.First(name) is { } mode
                && mode.Trim().StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RequestsAutoAnswer(SipRequest request)
    {
        if (request.Headers.First("X-Autoanswer") is not string value)
        {
            return false;
        }

        string normalized = value.Trim().ToLowerInvariant();
        return normalized is "true" or "yes" or "1";
    }

    /// <summary>
    /// Отвечает на чужой повторный INVITE.
    ///
    /// Это удержание с той стороны, снятие с удержания, смена кодека или переброс
    /// медиа на другой адрес. Отвечать прежним SDP нельзя: на серверном удержании
    /// мы продолжали бы отправлять голос оператора в линию, где его слушает
    /// музыка ожидания.
    /// </summary>
    private async Task HandleReinviteAsync(SipRequest request, ActiveCall call)
    {
        // 100 сразу: разбор предложения и пересборка медиа занимают время, а
        // ретрансмиссии INVITE на UDP начинаются через полсекунды.
        var trying = new SipResponse(100, headers: ResponseHeaders(request, call.LocalTag));
        await RespondToInviteQuietlyAsync(request, trying).ConfigureAwait(false);

        // Встречное предложение. Ждать «своей очереди» здесь нельзя: пока мы
        // ждём, наш собственный INVITE ждёт ответа от собеседника — и это
        // взаимная блокировка, ради разрыва которой 491 и придуман.
        bool renegotiating;
        SipMediaRenegotiator? renegotiator;
        lock (_gate)
        {
            renegotiating = call.IsRenegotiating;
            renegotiator = _mediaRenegotiator;
        }

        if (renegotiating)
        {
            var pending = new SipResponse(491, headers: ResponseHeaders(request, call.LocalTag));
            pending.Headers.Append(SipHeaderName.UserAgent, _userAgentName);
            await RespondToInviteQuietlyAsync(request, pending).ConfigureAwait(false);
            Log(SipLogLevel.Info, "<- повторный INVITE встретился с нашим, ответили 491");
            return;
        }

        // Обновление цели: собеседник мог сменить Contact.
        lock (_gate)
        {
            if (request.Contacts.Count > 0
                && _calls.TryGetValue(call.CallId, out ActiveCall? current)
                && current.Dialog is SipDialog dialog)
            {
                current.Dialog = new SipDialog(
                    dialog.CallId,
                    dialog.LocalTag,
                    dialog.RemoteTag,
                    dialog.LocalAddress,
                    dialog.RemoteAddress,
                    request.Contacts[0].Uri,
                    dialog.LocalSequence,
                    dialog.IsInitiator,
                    dialog.RouteSet);
            }
        }

        ReadOnlyMemory<byte>? answer;
        if (request.Body.IsEmpty)
        {
            // INVITE без тела означает «объяви заново, чем располагаешь». Ответ на
            // него — наше прежнее описание, и это не заглушка.
            answer = call.LocalSdp;
            Log(SipLogLevel.Debug, "<- повторный INVITE без предложения, отвечаем прежним описанием");
        }
        else if (renegotiator is not null)
        {
            // Линия называется явно: у оператора их до трёх, и ответить описанием
            // активной на предложение по удержанной значит переехать звуком не туда.
            answer = await renegotiator(call.CallId, request.Body).ConfigureAwait(false);
        }
        else
        {
            // Пересогласователь не задан — значит медиа никто не держит. Так бывает
            // только в проверках сигнализации; в приложении и в стенде он есть
            // всегда, и умолчание тут сказано вслух, а не спрятано.
            answer = call.LocalSdp;
            Log(SipLogLevel.Warning, "<- повторный INVITE подтверждён прежним SDP: пересогласователь не задан");
        }

        // Пока пересогласователь работал, звонок мог завершиться.
        ActiveCall? still;
        lock (_gate)
        {
            _calls.TryGetValue(call.CallId, out still);
        }
        if (still is null || !MatchesDialog(request, still))
        {
            Log(SipLogLevel.Debug, "пересогласование закончилось позже звонка");
            return;
        }

        if (answer is not ReadOnlyMemory<byte> body)
        {
            var unacceptable = new SipResponse(488, headers: ResponseHeaders(request, still.LocalTag));
            unacceptable.Headers.Append(SipHeaderName.UserAgent, _userAgentName);
            await RespondToInviteQuietlyAsync(request, unacceptable).ConfigureAwait(false);
            Log(SipLogLevel.Warning, "<- повторный INVITE отклонён 488: предложение не подходит");
            return;
        }

        var response = new SipResponse(200, headers: ResponseHeaders(request, still.LocalTag), body: body);
        response.Headers.Append(SipHeaderName.ContentType, "application/sdp");
        response.Headers.Append(SipHeaderName.Allow, AllowedMethods);
        response.Headers.Append(SipHeaderName.Supported, SupportedOptionTags);
        response.Headers.Append(SipHeaderName.UserAgent, _userAgentName);

        // Подтверждаем прежнюю договорённость, а не пересматриваем её: роль
        // обновляющего закреплена за стороной на весь диалог (RFC 4028 §7.4), и
        // менять её посреди разговора значит развести стороны в разные стороны.
        SipSessionTimer? timer;
        lock (_gate)
        {
            timer = still.SessionTimer;
            still.LocalSdp = body;
        }
        if (timer is SipSessionTimer agreed)
        {
            response.Headers.Append(SipSessionTimerHeader.SessionExpires, agreed.HeaderValue);
        }

        await RespondToInviteQuietlyAsync(request, response).ConfigureAwait(false);

        // Чужой повторный INVITE обновил сессию — отсчёт начинается заново. Делаем
        // это после ответа: до него обновление ещё не состоялось.
        RestartSessionTimer(still.CallId);

        Log(SipLogLevel.Info, "<- повторный INVITE пересогласован");
    }

    /// <summary>
    /// Отвечает на входящий звонок: 200 OK с нашим SDP.
    ///
    /// Медиа надо поднимать сразу после возврата, не дожидаясь ACK: Asterisk
    /// начинает слать RTP по 200 OK, и первые полсекунды разговора иначе уходят в
    /// никуда.
    /// </summary>
    public async Task<bool> AnswerIncomingCallAsync(
        string? callId = null,
        ReadOnlyMemory<byte> answer = default,
        string contentType = "application/sdp")
    {
        ActiveCall? call;
        lock (_gate)
        {
            call = callId is not null ? _calls.GetValueOrDefault(callId) : IncomingCall();
            if (call is null
                || call.Role != CallRole.Callee
                || call.State is not SipCallState.Incoming
                || call.InviteRequest is null)
            {
                return false;
            }
        }

        SipRequest invite = call.InviteRequest;

        if (SipDialog.FromResponder(invite, call.LocalTag) is not SipDialog dialog)
        {
            Log(SipLogLevel.Error, "во входящем INVITE нет Contact — диалог собрать невозможно");
            await RejectIncomingCallAsync(call.CallId, 400).ConfigureAwait(false);
            return false;
        }

        var response = new SipResponse(200, headers: ResponseHeaders(invite, call.LocalTag), body: answer);
        response.Headers.Append(SipHeaderName.ContentType, contentType);
        response.Headers.Append(SipHeaderName.Allow, AllowedMethods);
        response.Headers.Append(SipHeaderName.Supported, SupportedOptionTags);
        response.Headers.Append(SipHeaderName.UserAgent, _userAgentName);

        // Про таймер сессии отвечаем только тому, кто о нём заговорил: наш
        // Session-Expires в ответ на INVITE без него звонящий вправе не понять, а
        // следить за сроком в одиночку — способ положить трубку самому себе.
        SipSessionTimer? negotiated = _sessionTimerPolicy.NegotiatedForIncoming(invite.Headers);
        if (negotiated is SipSessionTimer agreed)
        {
            response.Headers.Append(SipSessionTimerHeader.SessionExpires, agreed.HeaderValue);

            // Require, а не Supported: сторона, назначенная обновлять, обязана
            // понимать RFC 4028, иначе договорённость односторонняя.
            response.Headers.Append(SipHeaderName.Require, SipSessionTimerHeader.OptionTag);
        }

        lock (_gate)
        {
            call.Dialog = dialog;
            call.State = new SipCallState.Answered();
            call.LocalSdp = answer;
        }

        try
        {
            await _transactions.RespondToInviteAsync(invite, response).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log(SipLogLevel.Error, $"не удалось отправить 200 OK: {Describe(error)}");
            FinishCall(call.CallId, new SipCallEvent.Failed(0, "ответ не ушёл"), call.Writer);
            return false;
        }

        ArmSessionTimer(call.CallId, negotiated);

        Log(SipLogLevel.Info, "-> 200 OK, звонок принят");
        call.Writer.TryWrite(new SipCallEvent.State(new SipCallState.Answered()));
        return true;
    }

    /// <summary>
    /// Отклоняет входящий звонок.
    ///
    /// 486 «занято» по умолчанию, а не 603 «отклонён»: при раздаче лидов первое
    /// возвращает вызов в очередь следующему агенту, второе завершает его совсем.
    /// </summary>
    public async Task RejectIncomingCallAsync(string? callId = null, int status = 486)
    {
        ActiveCall? call;
        lock (_gate)
        {
            call = callId is not null ? _calls.GetValueOrDefault(callId) : IncomingCall();
            if (call is null
                || call.Role != CallRole.Callee
                || call.State is not SipCallState.Incoming
                || call.InviteRequest is null)
            {
                return;
            }
        }

        var response = new SipResponse(status, headers: ResponseHeaders(call.InviteRequest, call.LocalTag));
        response.Headers.Append(SipHeaderName.UserAgent, _userAgentName);
        await RespondToInviteQuietlyAsync(call.InviteRequest, response).ConfigureAwait(false);

        Log(SipLogLevel.Info, $"-> {status.ToString(CultureInfo.InvariantCulture)}, звонок отклонён");
        FinishCall(call.CallId, new SipCallEvent.Ended("отклонён"), call.Writer);
    }

    /// <summary>Судьба нашего 200 OK на входящий звонок.</summary>
    private async Task HandleServerInviteAsync(SipServerInviteEvent value)
    {
        if (value is SipServerInviteEvent.Acknowledged acknowledged)
        {
            lock (_gate)
            {
                if (!_calls.ContainsKey(acknowledged.CallId))
                {
                    return;
                }
            }
            Log(SipLogLevel.Debug, "<- ACK, разговор подтверждён");
            return;
        }

        if (value is SipServerInviteEvent.NotAcknowledged notAcknowledged)
        {
            lock (_gate)
            {
                if (!_calls.TryGetValue(notAcknowledged.CallId, out ActiveCall? call) || call.Dialog is null)
                {
                    return;
                }
            }

            // Диалог создан нашим 200, но подтверждения нет. RFC 3261 §13.3.1.4
            // требует закрыть его через BYE: иначе на нашей стороне разговор, о
            // котором собеседник не знает, а оператор говорит в пустоту.
            Log(SipLogLevel.Warning, "ACK не пришёл за 32 с — закрываем звонок");
            await HangUpAsync(notAcknowledged.CallId).ConfigureAwait(false);
        }
    }

    // Входящие запросы

    private async Task HandleInboundAsync(SipRequest request)
    {
        switch (request.Method)
        {
            case SipMethod.Options:
                await HandleOptionsAsync(request).ConfigureAwait(false);
                break;

            case SipMethod.Bye:
                await HandleByeAsync(request).ConfigureAwait(false);
                break;

            case SipMethod.Ack:
                // ACK ответа не требует по определению: это подтверждение, а не
                // запрос. Отвечать на него 405 было бы протокольной ошибкой.
                break;

            case SipMethod.Invite:
                await HandleInviteAsync(request).ConfigureAwait(false);
                break;

            case SipMethod.Cancel:
                await HandleCancelAsync(request).ConfigureAwait(false);
                break;

            case SipMethod.Notify:
                await HandleNotifyAsync(request).ConfigureAwait(false);
                break;

            case SipMethod.Refer:
                {
                    // Перевод инициирует оператор. Управлять нашим разговором
                    // удалённой стороне не разрешаем: это отдельная политика, а не
                    // обязательная часть поддержки исходящего REFER.
                    var response = MakeSimpleResponse(request, 501);
                    response.Headers.Append(SipHeaderName.Allow, AllowedMethods);
                    await RespondQuietlyAsync(request, response).ConfigureAwait(false);
                    _events.Writer.TryWrite(new SipUserAgentEvent.UnsupportedRequest(request.Method));
                    break;
                }

            default:
                {
                    var response = MakeSimpleResponse(request, 405);
                    response.Headers.Append(SipHeaderName.Allow, AllowedMethods);
                    await RespondQuietlyAsync(request, response).ConfigureAwait(false);
                    _events.Writer.TryWrite(new SipUserAgentEvent.UnsupportedRequest(request.Method));
                    break;
                }
        }
    }

    private async Task HandleOptionsAsync(SipRequest request)
    {
        // Дошедший до нас опрос — единственное доказательство обратной дороги,
        // какое у клиента есть. Свои пакеты доходят всегда: они эту дорогу и
        // открывают.
        _qualifyWatch.NoteQualify(Now);

        // Ответ на OPTIONS — это то, что держит пир в состоянии OK.
        var response = new SipResponse(200, headers: ResponseHeaders(request));
        response.Headers.Append(SipHeaderName.Allow, AllowedMethods);
        response.Headers.Append(SipHeaderName.Supported, SupportedOptionTags);
        response.Headers.Append(SipHeaderName.UserAgent, _userAgentName);

        await RespondQuietlyAsync(request, response).ConfigureAwait(false);
        Log(SipLogLevel.Debug, "<- OPTIONS, ответили 200");
    }

    private async Task HandleByeAsync(SipRequest request)
    {
        if (MatchingCall(request) is not ActiveCall call)
        {
            await RespondQuietlyAsync(request, MakeSimpleResponse(request, 481)).ConfigureAwait(false);
            Log(SipLogLevel.Warning, "<- BYE вне активного диалога, ответили 481");
            return;
        }

        // Собеседник положил трубку. Ответить обязаны, иначе Asterisk будет
        // повторять BYE и держать диалог открытым.
        await RespondQuietlyAsync(request, MakeSimpleResponse(request, 200)).ConfigureAwait(false);
        Log(SipLogLevel.Info, "<- BYE, собеседник завершил звонок");
        FinishCall(call.CallId, new SipCallEvent.Ended("собеседник завершил звонок"), call.Writer);
    }

    private async Task HandleCancelAsync(SipRequest request)
    {
        // CANCEL отменяет ещё не отвеченный INVITE. Отвечать надо дважды: 200 на
        // сам CANCEL и 487 на отменённый INVITE — иначе Asterisk считает вызов
        // живым и держит канал до таймаута.
        await RespondQuietlyAsync(request, MakeSimpleResponse(request, 200)).ConfigureAwait(false);

        ActiveCall? call;
        lock (_gate)
        {
            call = request.CallId is string callId ? _calls.GetValueOrDefault(callId) : null;
            if (call is null || call.Role != CallRole.Callee || call.Dialog is not null || call.InviteRequest is null)
            {
                call = null;
            }
        }

        if (call is null)
        {
            Log(SipLogLevel.Debug, "<- CANCEL, отменять нечего");
            return;
        }

        var terminated = new SipResponse(487, headers: ResponseHeaders(call.InviteRequest!, call.LocalTag));
        terminated.Headers.Append(SipHeaderName.UserAgent, _userAgentName);
        await RespondToInviteQuietlyAsync(call.InviteRequest!, terminated).ConfigureAwait(false);

        Log(SipLogLevel.Info, "<- CANCEL, вызов отменён до ответа");
        FinishCall(call.CallId, new SipCallEvent.Ended("отменён вызывающим"), call.Writer);
    }

    private async Task HandleNotifyAsync(SipRequest request)
    {
        // REFER создаёт неявную подписку. Результат нового INVITE сервер сообщает
        // телом message/sipfrag. Чужой диалог подтверждать нельзя: такой NOTIFY не
        // относится к созданной нами подписке.
        ActiveCall? call = MatchingCall(request);
        bool isRefer = request.Headers.First(SipHeaderName.Event)?.StartsWith("refer", StringComparison.OrdinalIgnoreCase) == true;

        bool hasSubscription = false;
        if (call is not null)
        {
            lock (_gate)
            {
                hasSubscription = _transferSubscriptions.ContainsKey(call.CallId);
            }
        }

        if (call is null || !isRefer || !hasSubscription)
        {
            await RespondQuietlyAsync(request, MakeSimpleResponse(request, 481)).ConfigureAwait(false);
            Log(SipLogLevel.Debug, "<- NOTIFY без активного REFER-диалога, ответили 481");
            return;
        }

        await RespondQuietlyAsync(request, MakeSimpleResponse(request, 200)).ConfigureAwait(false);

        (int Status, string Reason)? fragment = SipFragment.ParseStatus(request.Body);
        bool isTerminated = request.Headers.First(SipHeaderName.SubscriptionState)?
            .StartsWith("terminated", StringComparison.OrdinalIgnoreCase) == true;

        if (fragment is { } result && result.Status is >= 200 and < 300)
        {
            Log(SipLogLevel.Info, "<- NOTIFY: перевод завершён");
            FinishTransfer(call.CallId, new SipTransferEvent.Succeeded());
            return;
        }

        if (fragment is { } failure && failure.Status >= 300)
        {
            Log(
                SipLogLevel.Warning,
                $"<- NOTIFY: перевод не состоялся, {failure.Status.ToString(CultureInfo.InvariantCulture)} {failure.Reason}");

            FinishTransfer(
                call.CallId,
                new SipTransferEvent.Failed(
                    failure.Status,
                    SipCallErrors.DescribeCallFailure(failure.Status, failure.Reason)));
            return;
        }

        if (isTerminated)
        {
            // Подписка закрыта, а судьба созданного INVITE не названа: тела нет
            // вовсе либо в нём остался промежуточный код. Ждать после этого нечего —
            // NOTIFY больше не придёт, и без явного отказа оператор просидел бы
            // минуту до таймаута с заблокированной кнопкой перевода.
            Log(SipLogLevel.Warning, "<- NOTIFY: подписка закрыта без результата перевода");
            FinishTransfer(
                call.CallId,
                new SipTransferEvent.Failed(500, "сервер завершил перевод без результата"));
        }
    }

    // Сопоставление и заголовки

    /// <summary>
    /// Находит линию, которой принадлежит входящий запрос.
    ///
    /// Ищется по всей тройке идентификаторов, а не по одному Call-ID: у линий он
    /// разный, но полагаться на это нельзя — сервер вправе прислать в чужом
    /// Call-ID что угодно, а перепутанная линия означает завершённый не тот
    /// разговор.
    /// </summary>
    private ActiveCall? MatchingCall(SipRequest request)
    {
        lock (_gate)
        {
            if (request.CallId is not string callId || !_calls.TryGetValue(callId, out ActiveCall? call))
            {
                return null;
            }
            return MatchesDialog(request, call) ? call : null;
        }
    }

    /// <summary>
    /// Проверяет полную тройку идентификаторов диалога.
    ///
    /// В запросе с удалённой стороны наш тег находится в To, удалённый — во From.
    /// Один Call-ID недостаточен для BYE, повторного INVITE и NOTIFY о переводе.
    /// </summary>
    private static bool MatchesDialog(SipRequest request, ActiveCall call) =>
        call.Dialog is SipDialog dialog
        && request.CallId is string callId
        && dialog.Matches(callId, request.To?.Tag, request.From?.Tag);

    private SipResponse MakeSimpleResponse(SipRequest request, int status, string? reasonPhrase = null) =>
        new(status, reasonPhrase, ResponseHeaders(request));

    /// <summary>
    /// Заголовки ответа, скопированные из запроса по RFC 3261 §8.2.6.2.
    ///
    /// Тег по умолчанию — регистрационный: им подписаны ответы на OPTIONS, и он
    /// постоянен, пока агент жив. У звонка тег свой: диалог опознаётся по тройке
    /// Call-ID и двух тегов, и подписывать разные звонки одним тегом значит
    /// склеивать их при перезвоне с тем же Call-ID.
    /// </summary>
    private SipHeaders ResponseHeaders(SipRequest request, string? localTag = null)
    {
        string tag = localTag ?? _localTag;
        var headers = new SipHeaders();

        // Весь стек Via в исходном порядке — по нему ответ находит дорогу обратно.
        foreach (string via in request.Headers.Values(SipHeaderName.Via))
        {
            headers.Append(SipHeaderName.Via, via);
        }

        if (request.Headers.First(SipHeaderName.From) is string from)
        {
            headers.Append(SipHeaderName.From, from);
        }

        // В To добавляем свой тег, если его там не было.
        if (request.To is NameAddress to)
        {
            NameAddress copy = to.Clone();
            copy.Tag ??= tag;
            headers.Append(SipHeaderName.To, copy.ToString());
        }

        if (request.Headers.First(SipHeaderName.CallId) is string callId)
        {
            headers.Append(SipHeaderName.CallId, callId);
        }
        if (request.Headers.First(SipHeaderName.CSeq) is string cseq)
        {
            headers.Append(SipHeaderName.CSeq, cseq);
        }

        SipEndpoint? contact;
        lock (_gate)
        {
            contact = _contactEndpoint;
        }
        if (contact is SipEndpoint endpoint)
        {
            var uri = new SipUri(endpoint.Host, user: _account.Username, port: endpoint.Port);
            if (_account.Transport != SipTransport.Udp)
            {
                uri.SetParameter("transport", _account.Transport.ProtocolName().ToLowerInvariant());
            }
            headers.Append(SipHeaderName.Contact, new NameAddress(uri).ToString());
        }

        return headers;
    }

    private async Task RespondQuietlyAsync(SipRequest request, SipResponse response)
    {
        try
        {
            await _transactions.RespondAsync(request, response).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log(SipLogLevel.Debug, $"ответ не ушёл: {error.Message}");
        }
    }

    private async Task RespondToInviteQuietlyAsync(SipRequest request, SipResponse response)
    {
        try
        {
            await _transactions.RespondToInviteAsync(request, response).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log(SipLogLevel.Debug, $"ответ на INVITE не ушёл: {error.Message}");
        }
    }
}
