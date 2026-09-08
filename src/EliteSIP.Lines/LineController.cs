using EliteSIP.Audio;
using EliteSIP.MediaCore;
using EliteSIP.SipCore;

namespace EliteSIP.Lines;

/// <summary>Чем кончилась попытка отправить тоны.</summary>
public enum DtmfOutcome
{
    /// <summary>Тоны вышли в поток.</summary>
    Sent,

    /// <summary>Линии нет: отправлять некуда.</summary>
    NoLine,

    /// <summary>Собеседник не подтвердил telephone-event — отправить нечем.</summary>
    NotSupported,

    /// <summary>Команда уже уходит или уже ушла по этой линии.</summary>
    AlreadySent,
}

/// <summary>Чем кончился перевод.</summary>
/// <param name="Succeeded">Пришёл финальный NOTIFY с 2xx — и только он считается успехом.</param>
/// <param name="Reason">Человеческая причина отказа. У успеха пустая.</param>
public readonly record struct LineTransferResult(bool Succeeded, string Reason)
{
    public static LineTransferResult Ok { get; } = new(true, string.Empty);

    public static LineTransferResult Failed(string reason) => new(false, reason);
}

/// <summary>
/// До трёх разговоров, один аудиотракт.
///
/// Это тот слой, который в оригинале лежал в <c>AppModel</c>: сигнализация про
/// звук ничего не знает, звук ничего не знает про диалоги, а решение «кому
/// сейчас звуковая карта» принимается ровно здесь. Перенесено по смыслу вместе
/// с порядками, каждый из которых там стоил отдельной ошибки:
///
/// <list type="bullet">
/// <item>сначала фоновые линии отпускают устройство, и только потом активная его
/// берёт — иначе две линии одновременно держат один захват;</item>
/// <item>при переключении звук переезжает сразу, а повторные INVITE уходят
/// после — ждать ответа сервера значит оставить оператора без обеих линий;</item>
/// <item>второе переключение во время первого заблокировано: два встречных
/// повторных INVITE по одной линии — это 491 и застрявшее удержание.</item>
/// </list>
///
/// Линии адресуются Call-ID. Умолчания «единственная линия» здесь нет нигде,
/// кроме команд, у которых адресат очевиден по смыслу (тоны и конференция уходят
/// по активной): догадка означала бы положить трубку не тому.
/// </summary>
public sealed class LineController(ILineSignaling signaling, Action<string>? onDiagnostic = null)
{
    /// <summary>
    /// Потолок — три: исходный разговор, консультация и третий участник
    /// конференции. Значение общее с сигнализацией: расходиться им нельзя, иначе
    /// одна половина заведёт линию, которую другая уже считает лишней.
    /// </summary>
    public const int MaximumLines = SipUserAgent.MaximumLines;

    private readonly ILineSignaling _signaling = signaling
        ?? throw new ArgumentNullException(nameof(signaling));

    private readonly Action<string>? _onDiagnostic = onDiagnostic;

    private readonly Lock _gate = new();

    private readonly List<CallLine> _lines = [];

    private bool _isSwitchingLines;

    private bool _microphoneMuted;

    /// <summary>Линии в порядке появления — том же, в котором они стоят в полосе.</summary>
    public IReadOnlyList<LineView> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines.Select(line => line.View)];
            }
        }
    }

    /// <summary>Линия, у которой сейчас звук. Её может не быть вовсе.</summary>
    public string? ActiveCallId
    {
        get
        {
            lock (_gate)
            {
                return _lines.FirstOrDefault(line => line.IsActive)?.CallId;
            }
        }
    }

    /// <summary>Есть ли место под ещё один разговор.</summary>
    public bool HasFreeLine
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count < MaximumLines;
            }
        }
    }

    /// <summary>
    /// Кнопка микрофона. Складывается с обоими удержаниями, а не заменяет их:
    /// снятая кнопка не возвращает в разговор линию, которую держит сервер.
    /// </summary>
    public bool IsMicrophoneMuted
    {
        get
        {
            lock (_gate)
            {
                return _microphoneMuted;
            }
        }
        set
        {
            lock (_gate)
            {
                _microphoneMuted = value;
                ApplyAudioStateLocked();
            }
        }
    }

    /// <summary>
    /// Заводит линию по уже отвеченному звонку.
    ///
    /// Медиа сюда приходит поднятым: собрать его может только тот, кто держит
    /// SDP и адрес, — стенд сегодня, приложение на W8. Слой линий владеет не
    /// созданием сессии, а тем, кому из них принадлежит звук.
    /// </summary>
    public async Task<bool> AttachAsync(
        string callId,
        ILineMedia media,
        string peer = "",
        bool makeActive = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(media);

        lock (_gate)
        {
            if (_lines.Count >= MaximumLines)
            {
                Diagnose($"линия {callId} не заведена: занято все {MaximumLines}");
                return false;
            }

            if (Find(callId) is not null)
            {
                return false;
            }

            _lines.Add(new CallLine(callId, media, peer));

            if (!makeActive)
            {
                // Линия заведена фоновой: звук ей не отдаётся, но и удержание по
                // ней не объявляется — сервер о нас ничего нового не узнал.
                ApplyOwnershipLocked();
                ApplyAudioStateLocked();
                return true;
            }
        }

        await SwitchToAsync(callId).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Убирает завершившуюся линию.
    ///
    /// Если ушла активная, а другая осталась, клиент возвращает её в разговор
    /// сам: снимает удержание и отдаёт ей звук. На той линии ждёт живой человек,
    /// и помнить о нём должен не оператор.
    /// </summary>
    /// <param name="returnRemaining">
    /// Возвращать ли оставшуюся линию в разговор. Ложь нужна ровно одному
    /// случаю — удавшемуся переводу: там кончаются обе линии сразу, и
    /// возвращать вторую значит снимать удержание с разговора, которого уже нет.
    /// На стенде это было видно как «удержание не сработало (разговора нет)»
    /// плюс лишний подъём тракта на полсекунды.
    /// </param>
    public async Task DetachAsync(string callId, bool returnRemaining = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);

        CallLine? removed;
        CallLine? successor = null;

        lock (_gate)
        {
            removed = Find(callId);
            if (removed is null)
            {
                return;
            }

            _lines.Remove(removed);

            // Ушедшая линия отпускает устройство раньше, чем его возьмёт
            // оставшаяся, — тот же порядок, что и при переключении. Медиа её
            // останавливает тот, кто её заводил, но между отбоем и остановкой
            // проходит время, и всё это время тракт был бы занят покойником.
            removed.Media.SuspendAudio();

            if (removed.IsActive && returnRemaining)
            {
                successor = _lines.LastOrDefault();
                if (successor is not null)
                {
                    successor.IsActive = true;
                    successor.IsHeldByOperator = false;
                }
            }

            ApplyOwnershipLocked();
            ApplyAudioStateLocked();
        }

        if (successor is not null)
        {
            Diagnose($"линия {removed.CallId} кончилась, возвращаем {successor.CallId}");
            await SetHoldAsync(successor, false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Делает линию активной: звук переезжает сразу, повторные INVITE уходят
    /// следом.
    ///
    /// Второе нажатие, пока идёт первое, отклоняется. Ждать «своей очереди»
    /// здесь нельзя: встречные повторные INVITE по одной линии — это 491 и
    /// клиент, оставшийся в музыке ожидания при надписи «Разговор» на экране.
    /// </summary>
    public async Task<bool> SwitchToAsync(string callId)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);

        CallLine target;
        CallLine? previous;
        bool needsUnhold;

        lock (_gate)
        {
            if (_isSwitchingLines)
            {
                Diagnose("переключение уже идёт");
                return false;
            }

            if (Find(callId) is not CallLine found)
            {
                return false;
            }

            previous = _lines.FirstOrDefault(line => line.IsActive && line != found);
            target = found;

            // Возврат с удержания объявляется серверу только если удержание
            // было. Первая линия разговора и линия, только что снявшая трубку,
            // ни на каком удержании не стоят, и повторный INVITE по ним —
            // лишний обмен на ровном месте, а на chan_sip ещё и лишний повод
            // потребовать авторизацию посреди разговора.
            needsUnhold = target.IsHeldByOperator;
            _isSwitchingLines = true;

            // Звук переезжает до всякой сети: обмен с сервером занимает круговое
            // время, и всё это время оператор был бы без обеих линий.
            foreach (CallLine line in _lines)
            {
                line.IsActive = line == target;
            }

            target.IsHeldByOperator = false;
            if (previous is not null)
            {
                previous.IsHeldByOperator = true;
            }

            ApplyOwnershipLocked();
            ApplyAudioStateLocked();
        }

        try
        {
            if (previous is not null)
            {
                await SetHoldAsync(previous, true).ConfigureAwait(false);
            }

            if (needsUnhold)
            {
                await SetHoldAsync(target, false).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _isSwitchingLines = false;
            }
        }

        return true;
    }

    /// <summary>
    /// Ставит линию на удержание или снимает с него.
    ///
    /// Отказ на повторный INVITE разговор не рвёт (RFC 3261 §14.1): звонок
    /// остаётся на прежних параметрах, и для оператора это «удержание не
    /// сработало», а не «связь упала». Ровно так и возвращается.
    /// </summary>
    public async Task<bool> HoldAsync(string callId, bool held)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);

        CallLine line;
        lock (_gate)
        {
            if (Find(callId) is not CallLine found)
            {
                return false;
            }

            line = found;
            line.IsHeldByOperator = held;
            ApplyAudioStateLocked();
        }

        return await SetHoldAsync(line, held).ConfigureAwait(false);
    }

    /// <summary>
    /// Применяет то, о чём договорился сервер своим повторным INVITE.
    ///
    /// Серверное удержание — не наше: снимать его кнопкой нельзя, а молчать при
    /// нём обязательно, даже если кнопку никто не нажимал.
    /// </summary>
    public void ApplyRemoteMedia(string callId, NegotiatedMedia media)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(media);

        lock (_gate)
        {
            if (Find(callId) is not CallLine line)
            {
                return;
            }

            // Признак берётся готовый: одного направления мало. Старая запись
            // удержания chan_sip — c=0.0.0.0 при нетронутом a=sendrecv, и
            // клиент, который смотрит только на направление, продолжает
            // отправлять голос оператора в никуда.
            line.IsHeldByServer = media.IsHeld;
            ApplyAudioStateLocked();
        }
    }

    /// <summary>Отправляет тоны по активной линии.</summary>
    public async Task<DtmfOutcome> SendDtmfAsync(DtmfSequence sequence, string? callId = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        CallLine line;
        lock (_gate)
        {
            CallLine? found = callId is null
                ? _lines.FirstOrDefault(candidate => candidate.IsActive)
                : Find(callId);

            if (found is null)
            {
                return DtmfOutcome.NoLine;
            }

            line = found;
        }

        // Молча проглотить нажатие нельзя: оператор будет думать, что попал в
        // меню, а на той стороне не произошло ничего.
        if (!line.Media.SupportsTelephoneEvents)
        {
            return DtmfOutcome.NotSupported;
        }

        return await line.Media.SendDtmfAndWaitAsync(sequence).ConfigureAwait(false)
            ? DtmfOutcome.Sent
            : DtmfOutcome.NotSupported;
    }

    /// <summary>
    /// Отправляет код конференции по активной линии.
    ///
    /// Подтверждается только то, что команда целиком вышла из очереди в поток:
    /// Asterisk 13 выполняет dynamic feature внутри <c>Dial</c> и отдельного
    /// SIP-события «канал вошёл в ConfBridge» не присылает. Объявить
    /// «конференция собрана» без серверной телеметрии значило бы показывать успех
    /// и при неверном боевом коде.
    ///
    /// Пока код уходит, повтор заблокирован; после успеха — до конца звонка.
    /// Третий участник входит в ту же комнату тем же способом: оператор
    /// переключается на его линию и отправляет код ещё раз.
    /// </summary>
    public async Task<DtmfOutcome> SendConferenceCodeAsync(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        var sequence = new DtmfSequence(code);
        if (!sequence.HasTones)
        {
            return DtmfOutcome.NotSupported;
        }

        CallLine line;
        lock (_gate)
        {
            if (_lines.FirstOrDefault(candidate => candidate.IsActive) is not CallLine active)
            {
                return DtmfOutcome.NoLine;
            }

            if (active.Conference != ConferenceState.Idle)
            {
                return DtmfOutcome.AlreadySent;
            }

            if (!active.Media.SupportsTelephoneEvents)
            {
                return DtmfOutcome.NotSupported;
            }

            line = active;
            line.Conference = ConferenceState.Sending;
        }

        bool sent;
        try
        {
            sent = await line.Media.SendDtmfAndWaitAsync(sequence).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                line.Conference = ConferenceState.Idle;
            }

            throw;
        }

        lock (_gate)
        {
            // Не вышло — состояние сбрасывается: ложное подтверждение здесь хуже,
            // чем кнопка, которую пришлось нажать второй раз.
            line.Conference = sent ? ConferenceState.Sent : ConferenceState.Idle;
        }

        return sent ? DtmfOutcome.Sent : DtmfOutcome.NotSupported;
    }

    /// <summary>
    /// Перевод: слепой без <paramref name="consultCallId"/> и консультационный с
    /// ним.
    ///
    /// REFER уходит по исходной линии, а консультационная называется в
    /// <c>Replaces</c>. Успехом считается финальный NOTIFY с 2xx, а не 202 на сам
    /// REFER: 202 означает только, что сервер взялся за дело. После успеха
    /// клиент кладёт трубку на обеих своих ногах — они больше не нужны.
    ///
    /// Отказ разбирается тем же кодом: причина возвращается строкой, исходный
    /// разговор остаётся.
    /// </summary>
    public async Task<LineTransferResult> TransferAsync(
        string callId,
        string target,
        string? consultCallId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);

        SipDialogIdentifier? replacing = null;
        if (consultCallId is not null)
        {
            replacing = _signaling.DialogIdentifierOf(consultCallId);
            if (replacing is null)
            {
                return LineTransferResult.Failed(SipTransferErrors.NoActiveCall);
            }
        }

        string outcome = SipTransferErrors.NoResult;
        bool succeeded = false;

        await foreach (SipTransferEvent value in _signaling
            .Transfer(callId, target, replacing)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            switch (value)
            {
                case SipTransferEvent.Accepted:
                    Diagnose("сервер принял REFER и начал перевод");
                    break;

                case SipTransferEvent.Succeeded:
                    succeeded = true;
                    break;

                case SipTransferEvent.Failed failure:
                    outcome = failure.Status == 0
                        ? failure.Reason
                        : SipTransferErrors.Rejected(failure.Status, failure.Reason);
                    break;

                default:
                    break;
            }
        }

        if (!succeeded)
        {
            return LineTransferResult.Failed(outcome);
        }

        // Обе ноги кладутся уже после успеха: собеседники к этому времени
        // соединены сервером напрямую, и держать свои диалоги незачем.
        //
        // Линии убираются ДО отбоя и без возврата оставшейся: обе кончаются
        // разом, и общее правило «активная ушла — верни вторую» здесь сработало
        // бы против нас.
        if (consultCallId is not null)
        {
            await DetachAsync(consultCallId, returnRemaining: false).ConfigureAwait(false);
        }

        await DetachAsync(callId, returnRemaining: false).ConfigureAwait(false);

        if (consultCallId is not null)
        {
            await _signaling.HangUpAsync(consultCallId).ConfigureAwait(false);
        }

        await _signaling.HangUpAsync(callId).ConfigureAwait(false);
        return LineTransferResult.Ok;
    }

    /// <summary>Кладёт трубку на всех линиях — это выход из приложения, а не кнопка.</summary>
    public async Task HangUpAllAsync()
    {
        string[] all;
        lock (_gate)
        {
            all = [.. _lines.Select(line => line.CallId)];
        }

        foreach (string callId in all)
        {
            await _signaling.HangUpAsync(callId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Повторный INVITE со сменой направления и применение ответа к тракту.
    ///
    /// Дешёвый случай — сменилось только направление; ради него трогать звуковую
    /// карту нельзя, пересборка тракта слышна как провал.
    /// </summary>
    private async Task<bool> SetHoldAsync(CallLine line, bool held)
    {
        lock (_gate)
        {
            if (line.IsRenegotiating)
            {
                Diagnose($"по линии {line.CallId} уже идёт пересогласование");
                return false;
            }

            line.IsRenegotiating = true;
        }

        try
        {
            // sendonly, а не inactive: собеседник обязан слышать музыку ожидания
            // сервера, и по ней же понимает, что его поставили на удержание.
            MediaDirection direction = held ? MediaDirection.SendOnly : MediaDirection.SendRecv;
            ReadOnlyMemory<byte> offer = line.Media.MakeReoffer(direction);
            ReadOnlyMemory<byte> answer = await _signaling
                .ReinviteAsync(line.CallId, offer)
                .ConfigureAwait(false);

            MediaRenegotiation applied = line.Media.ApplyAnswer(answer);
            Diagnose($"линия {line.CallId}: {(held ? "удержание" : "возврат")}, {applied}");
            return true;
        }
        catch (SipRenegotiationException failure)
        {
            // Разговор остаётся на прежних параметрах, и в журнале написано
            // именно это. Состояние линии откатывается: показывать удержание,
            // которого нет, значит соврать оператору о том, слышит ли его клиент.
            lock (_gate)
            {
                line.IsHeldByOperator = !held;
                ApplyAudioStateLocked();
            }

            Diagnose($"линия {line.CallId}: удержание не сработало ({failure.Message})");
            return false;
        }
        catch (SdpParseException failure)
        {
            Diagnose($"линия {line.CallId}: ответ на повторный INVITE не разобран ({failure.Message})");
            return false;
        }
        catch (SdpNegotiationException failure)
        {
            Diagnose($"линия {line.CallId}: ответ на повторный INVITE не принят ({failure.Message})");
            return false;
        }
        finally
        {
            lock (_gate)
            {
                line.IsRenegotiating = false;
            }
        }
    }

    /// <summary>
    /// Раздаёт звуковую карту. Порядок обязателен: сначала фоновые линии
    /// отпускают устройство, и только потом активная его берёт.
    /// </summary>
    private void ApplyOwnershipLocked()
    {
        foreach (CallLine line in _lines.Where(line => !line.IsActive))
        {
            line.Media.SuspendAudio();
        }

        foreach (CallLine line in _lines.Where(line => line.IsActive))
        {
            line.Media.ResumeAudio();
        }
    }

    private void ApplyAudioStateLocked()
    {
        foreach (CallLine line in _lines)
        {
            line.ApplyAudioState(_microphoneMuted);
        }
    }

    private CallLine? Find(string callId) =>
        _lines.FirstOrDefault(line => string.Equals(line.CallId, callId, StringComparison.Ordinal));

    private void Diagnose(string message) => _onDiagnostic?.Invoke(message);
}
