using System.Globalization;
using System.Threading.Channels;

namespace EliteSIP.SipCore;

/// <summary>
/// Звонки: исходящие, входящие, пересогласование, перевод и завершение.
///
/// Отдельным файлом от регистрации, потому что это единственная граница, которую
/// в оригинале держал порядок методов в двухтысячестрочном файле: у него сверху
/// была регистрация, снизу — звонки, и между ними не было ничего общего, кроме
/// транспорта.
/// </summary>
public sealed partial class SipUserAgent
{
    // Исходящий звонок

    /// <summary>
    /// Звонит по номеру и отдаёт поток событий звонка.
    ///
    /// <paramref name="offer"/> — готовое тело SDP. Слой сигнализации его не
    /// разбирает: медиа согласовывает вызывающий, и благодаря этому SipCore не
    /// зависит ни от кодеков, ни от аудио, и тестируется без звуковой карты.
    ///
    /// Возвращается не только поток событий, но и Call-ID: им линия адресуется
    /// дальше — на удержание, перевод и завершение.
    /// </summary>
    public SipOutgoingCall PlaceCall(
        string target,
        ReadOnlyMemory<byte> offer,
        string contentType = "application/sdp")
    {
        ArgumentNullException.ThrowIfNull(target);

        string callId = SipToken.CallId();
        Channel<SipCallEvent> events = SipCallEventChannel.Create();

        SipOutgoingCall Reject(string reason)
        {
            events.Writer.TryWrite(new SipCallEvent.Failed(0, reason));
            events.Writer.TryComplete();
            return new SipOutgoingCall(callId, events.Reader.ReadAllAsync());
        }

        string number = target.Trim();

        lock (_gate)
        {
            if (_calls.Count + _reservedLines.Count >= MaximumLines)
            {
                return Reject(SipCallErrors.TooManyLines(MaximumLines));
            }
            if (number.Length == 0)
            {
                return Reject(SipCallErrors.EmptyTarget);
            }

            // Место под линию занимается здесь, в синхронной части: дальше идёт
            // ожидание транспорта, и до него словарь линий про эту ещё не знает.
            _reservedLines.Add(callId);
        }

        _ = Task.Run(() => RunCallAsync(callId, number, offer, contentType, events.Writer), CancellationToken.None);
        return new SipOutgoingCall(callId, events.Reader.ReadAllAsync());
    }

    private async Task RunCallAsync(
        string callId,
        string target,
        ReadOnlyMemory<byte> offer,
        string contentType,
        ChannelWriter<SipCallEvent> writer)
    {
        string localTag = SipToken.Tag();
        int sequence = 0;

        /// Порог сервера из ответа 422. null — сервер про него ещё не говорил.
        int? serverMinimumExpires = null;
        int authenticationAttempts = 0;

        // Три попытки, а не две: к вызову авторизации (chan_sip требует её не
        // только на REGISTER, но и на INVITE) добавился отказ 422 со слишком
        // коротким сроком сессии. Прийти могут оба подряд, и тогда третий
        // запрос — первый, у которого есть шанс.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            SipEndpoint local;
            try
            {
                local = await _transactions.WaitUntilReadyAsync().ConfigureAwait(false);
            }
            catch (SipTransactionException error)
            {
                FinishCall(callId, new SipCallEvent.Failed(0, Describe(error)), writer);
                return;
            }

            sequence++;

            SipRequest request = MakeInvite(
                target,
                callId,
                localTag,
                sequence,
                offer,
                contentType,
                local,
                serverMinimumExpires);

            if (request.TopVia?.Branch is not string branch)
            {
                FinishCall(callId, new SipCallEvent.Failed(0, "не удалось собрать запрос"), writer);
                return;
            }

            Add(new ActiveCall
            {
                Role = CallRole.Caller,
                CallId = callId,
                Peer = target,
                LocalTag = localTag,
                Branch = branch,
                InviteSequence = sequence,
                Writer = writer,
                State = new SipCallState.Dialing(),
                LocalSdp = offer,
            });

            EmitCallState(new SipCallState.Dialing(), callId);
            Log(SipLogLevel.Info, $"-> INVITE {target}");

            bool needsRetry = false;
            bool finished = false;

            // Выходим из цикла на первом же финальном событии, а не по закрытию
            // потока: после отказа транзакция ещё живёт таймером D тридцать две
            // секунды, и ждать их незачем.
            await foreach (SipInviteEvent value in _transactions.SendInvite(request).ConfigureAwait(false))
            {
                if (value is SipInviteEvent.Provisional provisional)
                {
                    Log(
                        SipLogLevel.Debug,
                        $"<- {provisional.Response.StatusCode.ToString(CultureInfo.InvariantCulture)} {provisional.Response.ReasonPhrase}");

                    if (provisional.Response.StatusCode >= 180)
                    {
                        EmitCallState(new SipCallState.Ringing(), callId);
                        ArmRingingLimit(callId);
                    }
                    continue;
                }

                if (value is SipInviteEvent.Success success)
                {
                    await HandleCallAnsweredAsync(callId, request, success.Response, local).ConfigureAwait(false);
                    return;
                }

                if (value is SipInviteEvent.Failure failure)
                {
                    SipResponse response = failure.Response;

                    if (response.IsAuthenticationRequired
                        && authenticationAttempts == 0
                        && response.AuthenticationChallenges() is { Count: > 0 } challenges)
                    {
                        // Без этой строки в журнале оставались два подряд
                        // «-> INVITE» без всякой причины между ними, и выглядело
                        // это дефектом набора. Причина обычная: chan_sip вызывает
                        // на авторизацию и INVITE тоже.
                        Log(
                            SipLogLevel.Debug,
                            $"<- {response.StatusCode.ToString(CultureInfo.InvariantCulture)} на INVITE, отвечаем на вызов");

                        lock (_gate)
                        {
                            _cachedChallenge = challenges[0];
                            _nonceCount = 0;
                        }
                        authenticationAttempts++;
                        needsRetry = true;
                        break;
                    }

                    // 422 Session Interval Too Small — не отказ по существу:
                    // сервер называет свой порог и ждёт того же запроса с ним.
                    // Повторяем только если порог назван и он вырос, иначе
                    // получился бы вечный круг одинаковых запросов.
                    if (response.StatusCode == 422
                        && response.Headers.Number(SipSessionTimerHeader.MinSE) is int minimum
                        && minimum > (serverMinimumExpires ?? 0))
                    {
                        serverMinimumExpires = minimum;
                        Log(SipLogLevel.Debug, $"<- 422, сервер требует Min-SE {minimum.ToString(CultureInfo.InvariantCulture)} с");
                        needsRetry = true;
                        break;
                    }

                    Log(
                        SipLogLevel.Info,
                        $"<- {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}");

                    FinishCall(
                        callId,
                        new SipCallEvent.Failed(
                            response.StatusCode,
                            SipCallErrors.DescribeCallFailure(response.StatusCode, response.ReasonPhrase)),
                        writer);
                    finished = true;
                    break;
                }

                if (value is SipInviteEvent.Timeout)
                {
                    FinishCall(callId, new SipCallEvent.Failed(408, "сервер не ответил"), writer);
                    finished = true;
                    break;
                }

                if (value is SipInviteEvent.TransportFailed transportFailed)
                {
                    FinishCall(callId, new SipCallEvent.Failed(0, $"сеть: {transportFailed.Reason}"), writer);
                    finished = true;
                    break;
                }
            }

            if (finished)
            {
                return;
            }

            if (!needsRetry)
            {
                // Линии может уже не быть: отбой оператора и предел гудков
                // закрывают звонок сами, а поток транзакции завершается после
                // них — своим 487 или своим же пределом ожидания. Второй финал на
                // том же продолжении ничего не изменит, но в журнале оставит
                // «звонок прерван» поверх настоящей причины, и разбирающий жалобу
                // прочтёт именно его.
                lock (_gate)
                {
                    if (!_calls.ContainsKey(callId))
                    {
                        return;
                    }
                }

                FinishCall(callId, new SipCallEvent.Failed(0, "звонок прерван"), writer);
                return;
            }
        }

        FinishCall(callId, new SipCallEvent.Failed(401, "сервер не принял авторизацию"), writer);
    }

    private async Task HandleCallAnsweredAsync(
        string callId,
        SipRequest request,
        SipResponse response,
        SipEndpoint local)
    {
        ActiveCall? call;
        lock (_gate)
        {
            _calls.TryGetValue(callId, out call);
        }

        // Линии уже нет: 200 OK разошёлся в сети с нашим CANCEL или с пределом
        // гудков. Промолчать нельзя — на той стороне это состоявшийся разговор.
        // Без ACK сервер повторяет ответ по таймерам, а потом остаётся с
        // диалогом, о котором мы не знаем: у вызываемого снята трубка, в
        // наушниках тишина, и кладёт её в итоге только таймаут сессии.
        if (call is null)
        {
            await CloseStrayAnswerAsync(request, response, local).ConfigureAwait(false);
            return;
        }

        if (SipDialog.FromInitiator(request, response) is not SipDialog dialog)
        {
            Log(SipLogLevel.Error, "в 200 OK нет Contact — диалог собрать невозможно");
            FinishCall(callId, new SipCallEvent.Failed(0, "ответ без Contact"), call.Writer);
            return;
        }

        CancellationTokenSource? ringing;
        lock (_gate)
        {
            call.Dialog = dialog;
            call.State = new SipCallState.Answered();

            // Гудки кончились ответом — предел на них больше не нужен. Сам по
            // себе он и не сработал бы (проверяет отсутствие диалога), но задача
            // висела бы до своего срока на каждом состоявшемся разговоре.
            ringing = call.RingingTimeoutCts;
            call.RingingTimeoutCts = null;
        }
        ringing?.Cancel();

        ArmSessionTimer(callId, _sessionTimerPolicy.NegotiatedFromResponse(response.Headers));

        // ACK на 2xx идёт ВНЕ транзакции, по маршруту диалога и с тем же номером
        // CSeq, что у INVITE.
        var via = new SipVia(_account.Transport, local.Host, local.Port) { Branch = SipToken.Branch() };
        via.RequestRport();

        SipRequest ack = dialog.MakeRequest(
            SipMethod.Ack,
            call.InviteSequence,
            via,
            LocalContact(local),
            _userAgentName);

        await SendQuietlyAsync(ack).ConfigureAwait(false);

        Log(SipLogLevel.Info, "<- 200 OK, отправлен ACK");
        call.Writer.TryWrite(new SipCallEvent.State(new SipCallState.Answered()));
        call.Writer.TryWrite(new SipCallEvent.Answered(response.Body, response.ContentType));
    }

    /// <summary>
    /// Подтверждает и тут же закрывает разговор, которого у нас уже нет.
    ///
    /// Порядок обязателен: сначала ACK, потом BYE. BYE без ACK сервер имеет
    /// полное право не принять — диалог для него ещё не подтверждён.
    /// </summary>
    private async Task CloseStrayAnswerAsync(SipRequest request, SipResponse response, SipEndpoint local)
    {
        if (SipDialog.FromInitiator(request, response) is not SipDialog dialog
            || request.CSeq is not (int Number, SipMethod) cseq)
        {
            Log(SipLogLevel.Error, "200 OK на снятую линию, а диалог собрать нечем — закрыть её нечем тоже");
            return;
        }

        Log(SipLogLevel.Warning, "200 OK пришёл на уже снятую линию — подтверждаем и кладём трубку");

        var via = new SipVia(_account.Transport, local.Host, local.Port) { Branch = SipToken.Branch() };
        via.RequestRport();

        SipRequest ack = dialog.MakeRequest(SipMethod.Ack, cseq.Number, via, LocalContact(local), _userAgentName);
        await SendQuietlyAsync(ack).ConfigureAwait(false);

        (SipDialog updated, int byeSequence) = dialog.NextSequence();
        await SendByeAsync(updated, byeSequence, local).ConfigureAwait(false);
    }

    // Таймер сессии (RFC 4028)

    /// <summary>
    /// Заводит таймер по состоявшейся договорённости.
    ///
    /// <see langword="null"/> снимает таймер и не заводит нового — так
    /// обрабатывается сервер, промолчавший про Session-Expires. Промолчал значит
    /// не участвует, и заводить слежение в одиночку нельзя: обновлять будет
    /// некому, а трубку в срок положим мы, посреди работающего разговора.
    /// </summary>
    private void ArmSessionTimer(string callId, SipSessionTimer? negotiated)
    {
        CancellationTokenSource? previous;
        bool weRefresh;
        SipSessionTimer timer;

        lock (_gate)
        {
            if (!_calls.TryGetValue(callId, out ActiveCall? call))
            {
                return;
            }

            previous = call.SessionTimerCts;
            call.SessionTimerCts = null;
            call.SessionTimer = negotiated;

            if (negotiated is not SipSessionTimer value)
            {
                previous?.Cancel();
                return;
            }

            timer = value;

            // Чья роль в диалоге — вопрос не о том, кто сейчас шлёт запрос, а о
            // том, кто позвонил. Uac в договорённости означает звонящего, и для
            // нас это «мы» только на исходящем звонке.
            weRefresh = (timer.Refresher == SipRefresher.Uac) == (call.Role == CallRole.Caller);

            var cts = new CancellationTokenSource();
            call.SessionTimerCts = cts;

            if (weRefresh)
            {
                _ = Task.Run(() => RunSessionRefreshAsync(callId, timer, cts.Token), CancellationToken.None);
            }
            else
            {
                _ = Task.Run(() => RunSessionExpiryAsync(callId, timer, cts.Token), CancellationToken.None);
            }
        }

        previous?.Cancel();
        Log(
            SipLogLevel.Debug,
            $"таймер сессии {timer.Expires.ToString(CultureInfo.InvariantCulture)} с, "
                + (weRefresh ? "обновляем мы" : "обновляет собеседник"));
    }

    /// <summary>
    /// Наша сторона обновляет сессию: повторный INVITE на середине срока.
    ///
    /// Тело берём то же, о котором уже договорились: обновление сессии — про
    /// срок, а не про медиа, и менять в нём описание потока значило бы
    /// пересогласовывать звук на ровном месте.
    /// </summary>
    private async Task RunSessionRefreshAsync(string callId, SipSessionTimer timer, CancellationToken token)
    {
        try
        {
            await Task.Delay(timer.RefreshAfter, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ReadOnlyMemory<byte>? body;
        lock (_gate)
        {
            if (_isStopping
                || !_calls.TryGetValue(callId, out ActiveCall? call)
                || call.State is not SipCallState.Answered)
            {
                return;
            }
            body = call.LocalSdp;
        }

        if (body is not ReadOnlyMemory<byte> offer)
        {
            Log(SipLogLevel.Warning, "нечем обновить сессию: своего SDP нет");
            return;
        }

        try
        {
            await ReinviteAsync(callId, offer).ConfigureAwait(false);
            Log(SipLogLevel.Debug, "сессия обновлена");
        }
        catch (SipRenegotiationException error) when (error.Kind == SipRenegotiationErrorKind.RequestPending)
        {
            // Собеседник успел первым со своим повторным INVITE. Его запрос и
            // обновит сессию — заново заводить таймер не нужно, это сделает
            // обработчик входящего.
            Log(SipLogLevel.Debug, "обновление сессии разошлось со встречным — считаем обновлённой");
        }
        catch (SipRenegotiationException error)
        {
            // Не кладём трубку: до истечения срока остаётся ещё половина, и за
            // это время собеседник может обновить сессию сам. Если не обновит,
            // разговор закончит его собственный таймер.
            Log(SipLogLevel.Warning, $"обновить сессию не удалось: {error.Message}");
        }
    }

    /// <summary>
    /// Собеседник обновляет — мы следим и кладём трубку, если не обновил.
    ///
    /// Это единственное место, где клиент завершает разговор по своему решению, а
    /// не по команде оператора или собеседника. Поэтому срабатывает оно только по
    /// состоявшейся договорённости и только на полном сроке: к этому моменту
    /// собеседник пропустил и обновление на середине, и весь остаток срока.
    /// </summary>
    private async Task RunSessionExpiryAsync(string callId, SipSessionTimer timer, CancellationToken token)
    {
        try
        {
            await Task.Delay(timer.ExpireAfter, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (_isStopping
                || !_calls.TryGetValue(callId, out ActiveCall? call)
                || call.State is not SipCallState.Answered)
            {
                return;
            }
        }

        Log(
            SipLogLevel.Warning,
            $"сессия не обновлена за {timer.Expires.ToString(CultureInfo.InvariantCulture)} с — кладём трубку");
        await HangUpAsync(callId).ConfigureAwait(false);
    }

    /// <summary>
    /// Перезаводит таймер после того, как сессию обновили.
    ///
    /// Вызывается на обе стороны обновления: и когда обновили нас чужим повторным
    /// INVITE, и когда обновили мы своим. Договорённость при этом не
    /// пересматривается — меняется только точка отсчёта.
    /// </summary>
    private void RestartSessionTimer(string callId)
    {
        SipSessionTimer? timer;
        lock (_gate)
        {
            timer = _calls.TryGetValue(callId, out ActiveCall? call) ? call.SessionTimer : null;
        }
        if (timer is not null)
        {
            ArmSessionTimer(callId, timer);
        }
    }

    // Пересогласование медиа

    /// <summary>
    /// Пересогласовывает медиа внутри идущего разговора — наш повторный INVITE.
    ///
    /// Так делается удержание: отдельной команды «hold» в SIP нет, есть повторный
    /// INVITE со сменой направления в SDP. В консультационном переводе им же
    /// исходная линия ставится на удержание перед вторым звонком; сам перевод
    /// затем выполняется REFER с Replaces.
    ///
    /// Возвращает тело ответа — новый SDP собеседника. Разбирает его вызывающий:
    /// граница слоёв та же, что у обычного звонка.
    /// </summary>
    public async Task<ReadOnlyMemory<byte>> ReinviteAsync(
        string? callId = null,
        ReadOnlyMemory<byte> offer = default,
        string contentType = "application/sdp")
    {
        string resolved;
        lock (_gate)
        {
            ActiveCall? existing = Resolve(callId);
            if (existing is null || existing.Dialog is null || existing.State is not SipCallState.Answered)
            {
                throw new SipRenegotiationException(SipRenegotiationErrorKind.NoActiveCall);
            }
            if (existing.IsRenegotiating)
            {
                throw new SipRenegotiationException(SipRenegotiationErrorKind.AlreadyRenegotiating);
            }

            resolved = existing.CallId;
            existing.IsRenegotiating = true;
        }

        try
        {
            return await RunReinviteAsync(resolved, offer, contentType).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_calls.TryGetValue(resolved, out ActiveCall? call))
                {
                    call.IsRenegotiating = false;
                }
            }
        }
    }

    private async Task<ReadOnlyMemory<byte>> RunReinviteAsync(
        string callId,
        ReadOnlyMemory<byte> offer,
        string contentType)
    {
        // Две попытки по той же причине, что и у первого INVITE: chan_sip требует
        // авторизацию и на повторный.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            SipEndpoint local;
            try
            {
                local = await _transactions.WaitUntilReadyAsync().ConfigureAwait(false);
            }
            catch (SipTransactionException error)
            {
                throw new SipRenegotiationException(SipRenegotiationErrorKind.TransportFailed, 0, Describe(error));
            }

            SipDialog updated;
            int sequence;
            SipSessionTimer? timer;
            (DigestChallenge Challenge, string ResponseHeader)? cached;

            lock (_gate)
            {
                if (!_calls.TryGetValue(callId, out ActiveCall? call) || call.Dialog is not SipDialog dialog)
                {
                    throw new SipRenegotiationException(SipRenegotiationErrorKind.NoActiveCall);
                }

                (updated, sequence) = dialog.NextSequence();
                call.Dialog = updated;
                timer = call.SessionTimer;
                cached = _cachedChallenge;
            }

            var via = new SipVia(_account.Transport, local.Host, local.Port) { Branch = SipToken.Branch() };
            via.RequestRport();

            SipRequest request = updated.MakeRequest(
                SipMethod.Invite,
                sequence,
                via,
                LocalContact(local),
                _userAgentName);

            request.Body = offer;
            request.Headers.Append(SipHeaderName.ContentType, contentType);
            request.Headers.Append(SipHeaderName.Allow, AllowedMethods);
            request.Headers.Append(SipHeaderName.Supported, SupportedOptionTags);

            // Повторный INVITE внутри диалога с таймером обязан нести срок
            // (RFC 4028 §7.4): без него собеседник читает запрос как отказ от
            // договорённости и снимает свой таймер. Роль не пересматриваем —
            // повторяем ту, о которой договорились при установлении.
            if (timer is SipSessionTimer agreed)
            {
                request.Headers.Append(SipSessionTimerHeader.SessionExpires, agreed.HeaderValue);
                request.Headers.Append(
                    SipSessionTimerHeader.MinSE,
                    _sessionTimerPolicy.MinimumExpires.ToString(CultureInfo.InvariantCulture));
            }

            if (cached is { } challenge)
            {
                request.Headers.Append(
                    challenge.ResponseHeader,
                    Authorization(SipMethod.Invite, request.Uri, challenge.Challenge));
            }

            Log(SipLogLevel.Debug, $"-> повторный INVITE cseq={sequence.ToString(CultureInfo.InvariantCulture)}");

            bool needsRetryWithAuth = false;

            await foreach (SipInviteEvent value in _transactions.SendInvite(request).ConfigureAwait(false))
            {
                if (value is SipInviteEvent.Provisional)
                {
                    continue;
                }

                if (value is SipInviteEvent.Success success)
                {
                    await AcknowledgeReinviteAsync(callId, success.Response, sequence, local, offer)
                        .ConfigureAwait(false);

                    // Любой удавшийся повторный INVITE обновляет сессию, а не
                    // только тот, который затевался ради неё: для RFC 4028
                    // удержание и обновление — один и тот же запрос.
                    RestartSessionTimer(callId);
                    Log(SipLogLevel.Info, "<- 200 OK на повторный INVITE");
                    return success.Response.Body;
                }

                if (value is SipInviteEvent.Failure failure)
                {
                    SipResponse response = failure.Response;

                    if (response.IsAuthenticationRequired
                        && attempt == 0
                        && response.AuthenticationChallenges() is { Count: > 0 } challenges)
                    {
                        lock (_gate)
                        {
                            _cachedChallenge = challenges[0];
                            _nonceCount = 0;
                        }
                        needsRetryWithAuth = true;
                        break;
                    }

                    // 491 — не ошибка сети и не отказ по существу: собеседник
                    // просто успел первым. Решать, ждать ли и повторять, должен
                    // вызывающий — он один знает, зачем звал.
                    if (response.StatusCode == 491)
                    {
                        throw new SipRenegotiationException(SipRenegotiationErrorKind.RequestPending);
                    }

                    throw new SipRenegotiationException(
                        SipRenegotiationErrorKind.Rejected,
                        response.StatusCode,
                        response.ReasonPhrase);
                }

                if (value is SipInviteEvent.Timeout)
                {
                    throw new SipRenegotiationException(SipRenegotiationErrorKind.Timeout);
                }

                if (value is SipInviteEvent.TransportFailed transportFailed)
                {
                    throw new SipRenegotiationException(
                        SipRenegotiationErrorKind.TransportFailed,
                        0,
                        transportFailed.Reason);
                }
            }

            if (!needsRetryWithAuth)
            {
                throw new SipRenegotiationException(SipRenegotiationErrorKind.Timeout);
            }
        }

        throw new SipRenegotiationException(
            SipRenegotiationErrorKind.Rejected,
            401,
            "сервер не принял авторизацию");
    }

    /// <summary>Подтверждает 200 OK на наш повторный INVITE и запоминает новые параметры.</summary>
    private async Task AcknowledgeReinviteAsync(
        string callId,
        SipResponse response,
        int sequence,
        SipEndpoint local,
        ReadOnlyMemory<byte> offer)
    {
        SipDialog dialog;
        lock (_gate)
        {
            if (!_calls.TryGetValue(callId, out ActiveCall? call) || call.Dialog is not SipDialog current)
            {
                return;
            }

            // Contact в ответе может смениться: RFC 3261 §12.2.1.2 называет это
            // обновлением цели, и пропустить его значит слать следующий BYE туда,
            // где собеседника уже нет.
            dialog = response.Contacts.Count > 0
                ? new SipDialog(
                    current.CallId,
                    current.LocalTag,
                    current.RemoteTag,
                    current.LocalAddress,
                    current.RemoteAddress,
                    response.Contacts[0].Uri,
                    current.LocalSequence,
                    current.IsInitiator,
                    current.RouteSet)
                : current;

            call.Dialog = dialog;
            call.LocalSdp = offer;
        }

        var via = new SipVia(_account.Transport, local.Host, local.Port) { Branch = SipToken.Branch() };
        via.RequestRport();

        SipRequest ack = dialog.MakeRequest(SipMethod.Ack, sequence, via, LocalContact(local), _userAgentName);
        await SendQuietlyAsync(ack).ConfigureAwait(false);
    }

    // Перевод

    /// <summary>
    /// Переводит текущий разговор на номер через REFER (RFC 3515).
    ///
    /// Без <paramref name="replacing"/> это слепой перевод. С ним в Refer-To
    /// добавляется URI-header Replaces — адресат нового INVITE заменит уже идущий
    /// консультационный разговор (RFC 3891).
    ///
    /// <paramref name="callId"/> — линия, ПО КОТОРОЙ уходит REFER, то есть
    /// исходный разговор. Консультационная линия при этом называется в
    /// <paramref name="replacing"/>, и завершать её после успеха — дело
    /// вызывающего.
    /// </summary>
    public IAsyncEnumerable<SipTransferEvent> Transfer(
        string? callId = null,
        string target = "",
        SipDialogIdentifier? replacing = null)
    {
        Channel<SipTransferEvent> events = Channel.CreateBounded<SipTransferEvent>(
            new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

        IAsyncEnumerable<SipTransferEvent> Reject(int status, string reason)
        {
            events.Writer.TryWrite(new SipTransferEvent.Failed(status, reason));
            events.Writer.TryComplete();
            return events.Reader.ReadAllAsync();
        }

        string number = target.Trim();
        if (number.Length == 0)
        {
            return Reject(0, SipTransferErrors.EmptyTarget);
        }
        foreach (char character in number)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return Reject(0, SipTransferErrors.InvalidTarget);
            }
        }

        string resolved;
        lock (_gate)
        {
            ActiveCall? call = Resolve(callId);
            if (call is null || call.Dialog is null || call.State is not SipCallState.Answered)
            {
                return Reject(0, SipTransferErrors.NoActiveCall);
            }
            if (call.IsTransferring)
            {
                return Reject(0, SipTransferErrors.AlreadyTransferring);
            }

            resolved = call.CallId;
            call.IsTransferring = true;
            _transferSubscriptions[resolved] = events.Writer;
        }

        _ = Task.Run(() => RunTransferAsync(resolved, number, replacing, events.Writer), CancellationToken.None);
        return events.Reader.ReadAllAsync();
    }

    private async Task RunTransferAsync(
        string callId,
        string target,
        SipDialogIdentifier? replacing,
        ChannelWriter<SipTransferEvent> writer)
    {
        try
        {
            // REFER, как INVITE и BYE, chan_sip может сначала вызвать на
            // авторизацию. Повторяем один раз со свежим вызовом.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                SipEndpoint local;
                try
                {
                    local = await _transactions.WaitUntilReadyAsync().ConfigureAwait(false);
                }
                catch (SipTransactionException error)
                {
                    FinishTransfer(callId, new SipTransferEvent.Failed(0, SipTransferErrors.TransportFailed(Describe(error))));
                    return;
                }

                SipDialog updated;
                int sequence;
                (DigestChallenge Challenge, string ResponseHeader)? cached;

                lock (_gate)
                {
                    if (!_calls.TryGetValue(callId, out ActiveCall? call) || call.Dialog is not SipDialog dialog)
                    {
                        FinishTransfer(callId, new SipTransferEvent.Failed(0, SipTransferErrors.NoActiveCall));
                        return;
                    }

                    (updated, sequence) = dialog.NextSequence();
                    call.Dialog = updated;
                    cached = _cachedChallenge;
                }

                var via = new SipVia(_account.Transport, local.Host, local.Port) { Branch = SipToken.Branch() };
                via.RequestRport();

                SipRequest request = updated.MakeRequest(
                    SipMethod.Refer,
                    sequence,
                    via,
                    LocalContact(local),
                    _userAgentName);

                request.Headers.Append(SipHeaderName.ReferTo, ReferTo(target, replacing));
                request.Headers.Append(
                    SipHeaderName.ReferredBy,
                    new NameAddress(_account.AddressOfRecord).ToString());
                request.Headers.Append(SipHeaderName.Event, "refer");
                request.Headers.Append(SipHeaderName.Allow, AllowedMethods);

                if (cached is { } challenge)
                {
                    request.Headers.Append(
                        challenge.ResponseHeader,
                        Authorization(SipMethod.Refer, request.Uri, challenge.Challenge));
                }

                Log(SipLogLevel.Info, replacing is null ? $"-> REFER {target}" : $"-> REFER {target} с Replaces");

                SipResponse response;
                try
                {
                    response = await _transactions.SendAsync(request).ConfigureAwait(false);
                }
                catch (SipTransactionException error)
                {
                    string reason = error.Kind switch
                    {
                        SipTransactionErrorKind.Timeout => SipTransferErrors.Timeout,
                        SipTransactionErrorKind.TransportFailed => SipTransferErrors.TransportFailed(error.Detail),
                        SipTransactionErrorKind.Cancelled => SipTransferErrors.TransportFailed("соединение закрыто"),
                        SipTransactionErrorKind.NotReady => SipTransferErrors.TransportFailed("транспорт не готов"),
                        _ => SipTransferErrors.TransportFailed("транзакция потеряна"),
                    };
                    FinishTransfer(callId, new SipTransferEvent.Failed(0, reason));
                    return;
                }

                if (response.IsAuthenticationRequired
                    && attempt == 0
                    && response.AuthenticationChallenges() is { Count: > 0 } challenges)
                {
                    lock (_gate)
                    {
                        _cachedChallenge = challenges[0];
                        _nonceCount = 0;
                    }
                    continue;
                }

                if (!response.IsSuccess)
                {
                    FinishTransfer(
                        callId,
                        new SipTransferEvent.Failed(
                            response.StatusCode,
                            SipTransferErrors.Rejected(response.StatusCode, response.ReasonPhrase)));
                    return;
                }

                // На быстром сервере финальный NOTIFY теоретически может обогнать
                // 202 в транспортном потоке. Тогда подписка уже закрыта, и заново
                // заводить ей таймер нельзя.
                lock (_gate)
                {
                    if (!_transferSubscriptions.ContainsKey(callId))
                    {
                        return;
                    }
                }

                writer.TryWrite(new SipTransferEvent.Accepted());

                var timeout = new CancellationTokenSource();
                lock (_gate)
                {
                    if (_transferTimeouts.Remove(callId, out CancellationTokenSource? previous))
                    {
                        previous.Cancel();
                    }
                    _transferTimeouts[callId] = timeout;
                }

                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await Task.Delay(_transferResultTimeout, timeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        FinishTransfer(callId, new SipTransferEvent.Failed(408, SipTransferErrors.NoResult));
                    },
                    CancellationToken.None);

                Log(
                    SipLogLevel.Info,
                    $"<- {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase} на REFER");
                return;
            }

            FinishTransfer(
                callId,
                new SipTransferEvent.Failed(401, SipTransferErrors.Rejected(401, "сервер не принял авторизацию")));
        }
        finally
        {
            // После 202 флаг останется до NOTIFY; при ранней ошибке подписка уже
            // снята FinishTransfer, и здесь его можно отпустить.
            lock (_gate)
            {
                if (!_transferSubscriptions.ContainsKey(callId)
                    && _calls.TryGetValue(callId, out ActiveCall? call))
                {
                    call.IsTransferring = false;
                }
            }
        }
    }

    private string ReferTo(string target, SipDialogIdentifier? replacing)
    {
        // Номер — user-часть SIP URI, не готовая строка заголовка. Кодируем
        // разделители и тем самым одновременно сохраняем их смысл и исключаем
        // подстановку второго заголовка.
        string encoded = SipPercentEncoding.Encode(target, SipPercentEncoding.UserPart);
        string value = $"sip:{encoded}@{_account.Domain}";

        if (replacing is SipDialogIdentifier identifier)
        {
            value += $"?Replaces={identifier.PercentEncodedHeaderValue}";
        }
        return $"<{value}>";
    }

    private void FinishTransfer(string callId, SipTransferEvent value)
    {
        ChannelWriter<SipTransferEvent>? writer;
        lock (_gate)
        {
            if (_transferTimeouts.Remove(callId, out CancellationTokenSource? timeout))
            {
                timeout.Cancel();
            }
            if (!_transferSubscriptions.Remove(callId, out writer))
            {
                return;
            }
            if (_calls.TryGetValue(callId, out ActiveCall? call))
            {
                call.IsTransferring = false;
            }
        }

        writer.TryWrite(value);
        writer.TryComplete();
    }

    // Завершение

    /// <summary>
    /// Завершает звонок со своей стороны.
    ///
    /// Без Call-ID кладёт трубку на всех линиях сразу. Это не удобство, а
    /// требование выхода: остановка обязана закрыть каждый диалог, пока транспорт
    /// ещё жив, иначе сервер продолжит держать разговоры, о которых мы уже забыли.
    /// </summary>
    public async Task HangUpAsync(string? callId = null)
    {
        if (callId is null)
        {
            List<string> lines;
            lock (_gate)
            {
                lines = [.. _lineOrder];
            }
            foreach (string line in lines)
            {
                await HangUpAsync(line).ConfigureAwait(false);
            }
            return;
        }

        ActiveCall? call;
        lock (_gate)
        {
            _calls.TryGetValue(callId, out call);
        }
        if (call is null)
        {
            return;
        }

        // Входящий, на который мы ещё не ответили, завершается отказом, а не
        // CANCEL: CANCEL отменяет СВОЙ запрос, а этот запрос не наш.
        if (call.Role == CallRole.Callee && call.Dialog is null)
        {
            await RejectIncomingCallAsync(callId).ConfigureAwait(false);
            return;
        }

        if (call.Dialog is SipDialog dialog)
        {
            EmitCallState(new SipCallState.Ending(), callId);

            (SipDialog updated, int sequence) = dialog.NextSequence();
            lock (_gate)
            {
                call.Dialog = updated;
            }

            try
            {
                SipEndpoint local = await _transactions.WaitUntilReadyAsync().ConfigureAwait(false);
                await SendByeAsync(updated, sequence, local).ConfigureAwait(false);
            }
            catch (SipTransactionException)
            {
                // Транспорт не готов: BYE не уйдёт, но линию закрыть всё равно надо.
            }

            FinishCall(callId, new SipCallEvent.Ended("завершён"), call.Writer);
            return;
        }

        // Диалога ещё нет: собеседник не ответил, значит отменяем INVITE.
        EmitCallState(new SipCallState.Ending(), callId);
        await _transactions.CancelInviteAsync(call.Branch).ConfigureAwait(false);
        Log(SipLogLevel.Info, "-> CANCEL");
        FinishCall(callId, new SipCallEvent.Ended("отменён"), call.Writer);
    }

    private async Task SendByeAsync(SipDialog dialog, int sequence, SipEndpoint local)
    {
        var via = new SipVia(_account.Transport, local.Host, local.Port) { Branch = SipToken.Branch() };
        via.RequestRport();

        (DigestChallenge Challenge, string ResponseHeader)? cached;
        lock (_gate)
        {
            cached = _cachedChallenge;
        }

        SipRequest bye = dialog.MakeRequest(SipMethod.Bye, sequence, via, userAgent: _userAgentName);
        if (cached is { } challenge)
        {
            bye.Headers.Append(challenge.ResponseHeader, Authorization(SipMethod.Bye, bye.Uri, challenge.Challenge));
        }

        SipResponse? response = null;
        try
        {
            response = await _transactions.SendAsync(bye).ConfigureAwait(false);
        }
        catch (SipTransactionException)
        {
        }

        Log(
            SipLogLevel.Debug,
            $"-> BYE, ответ {(response is null ? "нет" : response.StatusCode.ToString(CultureInfo.InvariantCulture))}");

        // chan_sip может потребовать авторизацию и на BYE. Один повтор со свежим
        // вызовом: без него диалог на сервере остаётся висеть.
        if (response is null
            || !response.IsAuthenticationRequired
            || response.AuthenticationChallenges() is not { Count: > 0 } challenges)
        {
            return;
        }

        lock (_gate)
        {
            _cachedChallenge = challenges[0];
            _nonceCount = 0;
        }

        var retryVia = new SipVia(_account.Transport, local.Host, local.Port) { Branch = SipToken.Branch() };
        retryVia.RequestRport();

        SipRequest retry = dialog.MakeRequest(SipMethod.Bye, sequence + 1, retryVia, userAgent: _userAgentName);
        retry.Headers.Append(
            challenges[0].ResponseHeader,
            Authorization(SipMethod.Bye, retry.Uri, challenges[0].Challenge));

        try
        {
            await _transactions.SendAsync(retry).ConfigureAwait(false);
        }
        catch (SipTransactionException)
        {
        }
    }

    // Сборка исходящего INVITE

    private SipRequest MakeInvite(
        string target,
        string callId,
        string localTag,
        int sequence,
        ReadOnlyMemory<byte> offer,
        string contentType,
        SipEndpoint local,
        int? minimumExpires)
    {
        var targetUri = new SipUri(_account.Domain, user: target);
        var request = new SipRequest(SipMethod.Invite, targetUri, body: offer);

        var via = new SipVia(_account.Transport, local.Host, local.Port) { Branch = SipToken.Branch() };
        via.RequestRport();
        request.Headers.Append(SipHeaderName.Via, via.ToString());
        request.Headers.Append(SipHeaderName.MaxForwards, "70");

        var from = new NameAddress(_account.AddressOfRecord, _account.EffectiveDisplayName) { Tag = localTag };
        request.Headers.Append(SipHeaderName.From, from.ToString());
        request.Headers.Append(SipHeaderName.To, new NameAddress(targetUri).ToString());
        request.Headers.Append(SipHeaderName.CallId, callId);
        request.Headers.Append(
            SipHeaderName.CSeq,
            $"{sequence.ToString(CultureInfo.InvariantCulture)} {SipMethod.Invite.Name()}");
        request.Headers.Append(SipHeaderName.Contact, LocalContact(local).ToString());
        request.Headers.Append(SipHeaderName.Allow, AllowedMethods);
        request.Headers.Append(SipHeaderName.Supported, SupportedOptionTags);
        request.Headers.Append(SipHeaderName.UserAgent, _userAgentName);
        request.Headers.Append(SipHeaderName.ContentType, contentType);

        AppendSessionTimerOffer(request, minimumExpires);

        (DigestChallenge Challenge, string ResponseHeader)? cached;
        lock (_gate)
        {
            cached = _cachedChallenge;
        }
        if (cached is { } challenge)
        {
            request.Headers.Append(
                challenge.ResponseHeader,
                Authorization(SipMethod.Invite, targetUri, challenge.Challenge));
        }

        return request;
    }

    /// <summary>
    /// Дописывает предложение таймера сессии в исходящий INVITE.
    ///
    /// <paramref name="minimumExpires"/> отличается от значения политики после
    /// ответа 422: сервер сообщает в нём свой порог, и повторять запрос с прежним,
    /// заведомо отклонённым значением бессмысленно.
    /// </summary>
    private void AppendSessionTimerOffer(SipRequest request, int? minimumExpires)
    {
        if (!_sessionTimerPolicy.IsEnabled)
        {
            return;
        }

        int floor = Math.Max(minimumExpires ?? _sessionTimerPolicy.MinimumExpires, 1);
        int expires = Math.Max(_sessionTimerPolicy.Expires, floor);

        // Роль указываем сразу: без параметра выбор остаётся за сервером, а нам
        // нужен предсказуемый — обновляет он.
        var offer = new SipSessionTimer(expires, SipSessionTimerPolicy.PreferredPeerRefresher);
        request.Headers.Append(SipSessionTimerHeader.SessionExpires, offer.HeaderValue);
        request.Headers.Append(SipSessionTimerHeader.MinSE, floor.ToString(CultureInfo.InvariantCulture));
    }

    private NameAddress LocalContact(SipEndpoint local)
    {
        SipEndpoint endpoint;
        lock (_gate)
        {
            endpoint = _contactEndpoint ?? local;
        }

        var uri = new SipUri(endpoint.Host, user: _account.Username, port: endpoint.Port);
        if (_account.Transport != SipTransport.Udp)
        {
            uri.SetParameter("transport", _account.Transport.ProtocolName().ToLowerInvariant());
        }
        return new NameAddress(uri);
    }

    private async Task SendQuietlyAsync(SipRequest request)
    {
        try
        {
            await _transactions.SendWithoutTransactionAsync(request).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log(SipLogLevel.Debug, $"запрос вне транзакции не ушёл: {error.Message}");
        }
    }

    // Предел гудков

    /// <summary>
    /// Заводит предел гудков. Повторный вызов ничего не меняет.
    ///
    /// Именно здесь, а не в слое транзакций: там знают про запрос и ответы, а
    /// «сколько уместно ждать человека у телефона» — вопрос звонка, и ответ на
    /// него один на все три линии. Взводится на первом 1xx, потому что до него
    /// звонок закрывает таймер B, и два предела подряд означали бы два разных
    /// ответа на «почему сняло».
    /// </summary>
    private void ArmRingingLimit(string callId)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (!_calls.TryGetValue(callId, out ActiveCall? call) || call.RingingTimeoutCts is not null)
            {
                return;
            }
            cts = new CancellationTokenSource();
            call.RingingTimeoutCts = cts;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(_ringingLimit, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Отменённая задача обязана замолчать, иначе снятие линии само
                    // же и объявит её просроченной.
                    return;
                }
                await ExpireRingingAsync(callId).ConfigureAwait(false);
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Гудки идут дольше отпущенного — снимаем звонок.
    ///
    /// CANCEL уходит обязательно и раньше, чем закрывается линия у нас: молча
    /// забыть про INVITE значит оставить его звонить у вызываемого, а на
    /// сервере — висеть до его собственного таймаута.
    /// </summary>
    private async Task ExpireRingingAsync(string callId)
    {
        ActiveCall? call;
        lock (_gate)
        {
            // Диалог означает, что 200 OK всё-таки пришёл, пока мы просыпались:
            // отменять уже нечего, и это разговор, а не гудки.
            if (!_calls.TryGetValue(callId, out call) || call.Role != CallRole.Caller || call.Dialog is not null)
            {
                return;
            }
        }

        Log(SipLogLevel.Warning, $"гудки без ответа дольше {_ringingLimit} — снимаем звонок");
        await _transactions.CancelInviteAsync(call.Branch).ConfigureAwait(false);

        FinishCall(callId, new SipCallEvent.Failed(408, "никто не ответил"), call.Writer);
    }
}
