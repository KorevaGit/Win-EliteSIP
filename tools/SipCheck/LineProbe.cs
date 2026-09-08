using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using EliteSIP.Audio;
using EliteSIP.Lines;
using EliteSIP.MediaCore;
using EliteSIP.SipCore;

namespace EliteSIP.Tools.SipCheck;

/// <summary>
/// Приёмка W6: несколько линий, удержание, приём входящего, тоны, конференция и
/// консультационный перевод — на живом Asterisk.
///
/// Всё, что здесь проверяется, модульными тестами не проверяется в принципе:
/// тесты знают, в каком порядке слой линий обязан ходить в сеть, но не знают,
/// что об этом думает chan_sip. Стенд знает.
///
/// Звуковая карта нужна не всем режимам: линий три, а микрофон один, и в режиме
/// <c>--lines</c> тракт нужен ровно для того, чтобы проверить, что он достаётся
/// одной линии за раз.
/// </summary>
internal sealed class LineProbe(SipUserAgent agent, VoiceAudioBus bus) : IDisposable
{
    private readonly ConcurrentDictionary<string, MediaSessionLine> _media = new(StringComparer.Ordinal);

    private readonly SipUserAgent _agent = agent;

    private readonly VoiceAudioBus _bus = bus;

    private readonly CancellationTokenSource _pumps = new();

    private LineController? _controller;

    private LineController Controller => _controller ??= new LineController(
        new SipUserAgentSignaling(_agent),
        message => Console.WriteLine($"   линии: {message}"));

    /// <summary>Три линии подряд, переключения между ними и отбой по одной.</summary>
    public static async Task<bool> RunLinesAsync(
        SipUserAgent agent,
        IReadOnlyList<string> targets,
        double talkSeconds,
        CancellationToken cancellationToken)
    {
        using VoiceAudioBus bus = MakeBus();
        using var probe = new LineProbe(agent, bus);
        probe.ArmRenegotiator();

        var established = new List<string>();
        foreach (string target in targets)
        {
            if (await probe.DialAsync(target, cancellationToken).ConfigureAwait(false) is not string callId)
            {
                Console.WriteLine($"[x] линия на {target} не поднялась");
                await probe.HangUpAllAsync().ConfigureAwait(false);
                return false;
            }

            established.Add(callId);
            probe.PrintLines();
        }

        // Четвёртая линия обязана получить отказ от самой сигнализации: потолок
        // общий у неё и у слоя линий, и расходиться им нельзя.
        if (agent.HasFreeLine && established.Count >= SipUserAgent.MaximumLines)
        {
            Console.WriteLine("[x] сигнализация считает, что место под четвёртую линию есть");
            await probe.HangUpAllAsync().ConfigureAwait(false);
            return false;
        }

        // Возврат к первой линии: она стоит на удержании с того момента, как
        // оператор завёл вторую, и обязана вернуться в разговор без
        // пересогласования порта.
        Console.WriteLine($"-> возвращаемся на первую линию {established[0]}");
        await probe.Controller.SwitchToAsync(established[0]).ConfigureAwait(false);
        probe.PrintLines();

        await Task.Delay(TimeSpan.FromSeconds(talkSeconds), cancellationToken).ConfigureAwait(false);

        // Отбой по одной линии: остальные обязаны выжить, а активная —
        // вернуться в разговор сама.
        foreach (string callId in established)
        {
            Console.WriteLine($"-> кладём трубку на {callId}");
            await agent.HangUpAsync(callId).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            probe.PrintLines();
        }

        return true;
    }

    /// <summary>Консультационный перевод целиком: удержание, вторая линия, REFER с Replaces.</summary>
    public static async Task<bool> RunConsultAsync(
        SipUserAgent agent,
        string target,
        string consultTarget,
        double talkSeconds,
        CancellationToken cancellationToken)
    {
        using VoiceAudioBus bus = MakeBus();
        using var probe = new LineProbe(agent, bus);
        probe.ArmRenegotiator();

        if (await probe.DialAsync(target, cancellationToken).ConfigureAwait(false) is not string primary)
        {
            return false;
        }

        await Task.Delay(TimeSpan.FromSeconds(talkSeconds), cancellationToken).ConfigureAwait(false);

        // «Консультация»: вторая линия становится активной, первая уезжает на
        // удержание — клиент слушает музыку ожидания сервера.
        Console.WriteLine($"-> консультация на {consultTarget}");
        if (await probe.DialAsync(consultTarget, cancellationToken).ConfigureAwait(false) is not string consult)
        {
            Console.WriteLine("[x] консультация не состоялась, исходный разговор остаётся");
            await probe.HangUpAllAsync().ConfigureAwait(false);
            return false;
        }

        probe.PrintLines();
        await Task.Delay(TimeSpan.FromSeconds(talkSeconds), cancellationToken).ConfigureAwait(false);

        Console.WriteLine("-> «Соединить»: REFER по исходной линии с Replaces консультационной");
        LineTransferResult result = await probe.Controller
            .TransferAsync(primary, consultTarget, consult, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine(result.Succeeded
            ? "[v] перевод удался: собеседники остались вдвоём, обе наши ноги положены"
            : $"[x] перевод не удался: {result.Reason}");

        if (!result.Succeeded)
        {
            await probe.HangUpAllAsync().ConfigureAwait(false);
        }

        return result.Succeeded;
    }

    /// <summary>
    /// Одна линия, удержание посреди разговора и возврат.
    ///
    /// Проверяется не то, что мы отправили повторный INVITE, а то, что после
    /// возврата звук идёт: удержание, из которого нельзя выйти, ничем не лучше
    /// оборванного разговора.
    /// </summary>
    public static async Task<bool> RunHoldAsync(
        SipUserAgent agent,
        string target,
        double talkSeconds,
        double holdAfterSeconds,
        CancellationToken cancellationToken)
    {
        using VoiceAudioBus bus = MakeBus();
        using var probe = new LineProbe(agent, bus);
        probe.ArmRenegotiator();

        if (await probe.DialAsync(target, cancellationToken).ConfigureAwait(false) is not string callId)
        {
            return false;
        }

        await Task.Delay(TimeSpan.FromSeconds(holdAfterSeconds), cancellationToken).ConfigureAwait(false);

        Console.WriteLine("-> удержание");
        bool held = await probe.Controller.HoldAsync(callId, true).ConfigureAwait(false);
        probe.PrintLines();

        await Task.Delay(TimeSpan.FromSeconds(holdAfterSeconds), cancellationToken).ConfigureAwait(false);

        Console.WriteLine("-> возврат в разговор");
        bool resumed = await probe.Controller.HoldAsync(callId, false).ConfigureAwait(false);
        probe.PrintLines();

        double rest = Math.Max(talkSeconds - (2 * holdAfterSeconds), 0);
        await Task.Delay(TimeSpan.FromSeconds(rest), cancellationToken).ConfigureAwait(false);

        await agent.HangUpAsync(callId).ConfigureAwait(false);
        return held && resumed;
    }

    /// <summary>
    /// Звонит и отправляет код тонами по активной линии — тот же путь, которым
    /// уходит команда конференции.
    ///
    /// Проверяется он на добавочном 603 (<c>Read</c> + <c>SayDigits</c>): что
    /// именно принял сервер, видно в его журнале, а не в нашем. Момент отправки
    /// важен — меню принимает цифры не с первой секунды, и тон, посланный
    /// раньше приглашения, просто пропадает.
    /// </summary>
    public static async Task<bool> RunConferenceAsync(
        SipUserAgent agent,
        string target,
        string code,
        double talkSeconds,
        double sendAfterSeconds,
        CancellationToken cancellationToken)
    {
        using VoiceAudioBus bus = MakeBus();
        using var probe = new LineProbe(agent, bus);
        probe.ArmRenegotiator();

        if (await probe.DialAsync(target, cancellationToken).ConfigureAwait(false) is not string callId)
        {
            return false;
        }

        await Task.Delay(TimeSpan.FromSeconds(sendAfterSeconds), cancellationToken).ConfigureAwait(false);

        DtmfOutcome outcome = await probe.Controller.SendConferenceCodeAsync(code).ConfigureAwait(false);
        Console.WriteLine($"   код {code}: {Describe(outcome)}");

        // Повтор обязан быть заблокирован до конца звонка: подтвердить
        // конференцию нечем, а второе нажатие — это второй набор в линию.
        DtmfOutcome again = await probe.Controller.SendConferenceCodeAsync(code).ConfigureAwait(false);
        Console.WriteLine($"   повтор: {Describe(again)}");

        double rest = Math.Max(talkSeconds - sendAfterSeconds, 0);
        await Task.Delay(TimeSpan.FromSeconds(rest), cancellationToken).ConfigureAwait(false);
        await agent.HangUpAsync(callId).ConfigureAwait(false);

        return outcome == DtmfOutcome.Sent && again == DtmfOutcome.AlreadySent;
    }

    /// <summary>Ждёт входящий звонок, отвечает на него и говорит заданное время.</summary>
    public static async Task<bool> RunAnswerAsync(
        SipUserAgent agent,
        ChannelReader<SipIncomingCall> waiting,
        double waitSeconds,
        double talkSeconds,
        bool reject,
        string? conferenceCode,
        CancellationToken cancellationToken)
    {
        using VoiceAudioBus bus = MakeBus();
        using var probe = new LineProbe(agent, bus);
        probe.ArmRenegotiator();

        Console.WriteLine($"-> ждём входящий {waitSeconds.ToString(CultureInfo.InvariantCulture)} с");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(waitSeconds));

        SipIncomingCall? incoming = null;
        try
        {
            incoming = await waiting.ReadAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ждали столько, сколько велено.
        }

        if (incoming is null)
        {
            Console.WriteLine("[x] входящего не было");
            return false;
        }

        Console.WriteLine($"<- входящий от {incoming.DisplayNumber} на {incoming.CalledNumber}");

        if (reject)
        {
            // 486, а не 603: при раздаче лидов первое возвращает вызов в очередь
            // следующему агенту, второе завершает его совсем.
            await agent.RejectIncomingCallAsync(incoming.CallId).ConfigureAwait(false);
            Console.WriteLine("[v] отклонили: 486");
            return true;
        }

        if (agent.MediaAddress is not string mediaAddress)
        {
            Console.WriteLine("[x] сервер не сообщил наш адрес");
            await agent.RejectIncomingCallAsync(incoming.CallId).ConfigureAwait(false);
            return false;
        }

        // Порядок из оригинала: порт занимается и слушает ДО 200 OK — Asterisk
        // начинает слать RTP сразу по ответу, не дожидаясь ACK.
        (SessionDescription answer, NegotiatedMedia media, RtpPortReservation reservation) =
            MediaSession.MakeAnswer(SdpParser.Parse(incoming.Offer.Span), mediaAddress);

        if (!await agent.AnswerIncomingCallAsync(incoming.CallId, answer.EncodedData()).ConfigureAwait(false))
        {
            Console.WriteLine("[x] ответить не удалось");
            reservation.Release();
            return false;
        }

        await probe.OpenAsync(incoming.CallId, media, answer, reservation, incoming.DisplayNumber, cancellationToken)
            .ConfigureAwait(false);
        probe.PumpCall(incoming.CallId, incoming.Events);
        probe.PrintLines();

        if (conferenceCode is not null)
        {
            DtmfOutcome outcome = await probe.Controller
                .SendConferenceCodeAsync(conferenceCode)
                .ConfigureAwait(false);
            Console.WriteLine($"   конференция {conferenceCode}: {Describe(outcome)}");
        }

        await Task.Delay(TimeSpan.FromSeconds(talkSeconds), cancellationToken).ConfigureAwait(false);
        await agent.HangUpAsync(incoming.CallId).ConfigureAwait(false);
        return true;
    }

    public void Dispose()
    {
        _pumps.Cancel();
        _pumps.Dispose();

        foreach (MediaSessionLine line in _media.Values)
        {
            line.Session.Dispose();
        }
    }

    private static VoiceAudioBus MakeBus()
    {
        VoiceAudioConfiguration configuration = new();
        return new VoiceAudioBus(
            new WasapiVoiceAudioEngine(configuration),
            settings => new WasapiVoiceAudioEngine(settings));
    }

    private static string Describe(DtmfOutcome outcome) => outcome switch
    {
        DtmfOutcome.Sent => "команда вышла в поток",
        DtmfOutcome.NoLine => "разговора нет",
        DtmfOutcome.NotSupported => "собеседник не подтвердил telephone-event",
        DtmfOutcome.AlreadySent => "уже отправлена по этой линии",
        _ => outcome.ToString(),
    };

    /// <summary>
    /// Обслуживание чужих повторных INVITE на всё время работы стенда.
    ///
    /// Один пересогласователь на все линии: адресуется он Call-ID, и заводить по
    /// одному на линию значило бы завести три обработчика, каждый из которых
    /// молча отвечает 488 на чужой звонок.
    /// </summary>
    private void ArmRenegotiator() =>
        _agent.SetMediaRenegotiator((callId, offer) => Task.FromResult(Renegotiate(callId, offer)));

    private ReadOnlyMemory<byte>? Renegotiate(string callId, ReadOnlyMemory<byte> offer)
    {
        if (!_media.TryGetValue(callId, out MediaSessionLine? line)
            || _agent.MediaAddress is not string mediaAddress)
        {
            return null;
        }

        try
        {
            (SessionDescription answer, NegotiatedMedia media) = SdpNegotiator.MakeAnswer(
                SdpParser.Parse(offer.Span),
                mediaAddress,
                line.Session.LocalPort);

            MediaRenegotiation outcome = line.Session.Renegotiate(media);

            // Серверное удержание — не наше: снимать его кнопкой нельзя, а
            // молчать при нём обязательно.
            Controller.ApplyRemoteMedia(callId, media);
            Console.WriteLine($"   {callId}: сервер пересогласовал, {outcome}, направление {media.Direction}");
            return answer.EncodedData();
        }
        catch (Exception failure)
            when (failure is SdpParseException or SdpNegotiationException or MediaCodecChangedException)
        {
            Console.WriteLine($"   [x] повторное предложение не принято: {failure.Message}");
            return null;
        }
    }

    /// <summary>Звонит и заводит линию, когда ответили.</summary>
    private async Task<string?> DialAsync(string target, CancellationToken cancellationToken)
    {
        if (_agent.MediaAddress is not string mediaAddress)
        {
            Console.WriteLine("[x] сервер не сообщил наш адрес");
            return null;
        }

        (SessionDescription offer, RtpPortReservation reservation) = MediaSession.MakeOffer(mediaAddress);
        Console.WriteLine(
            $"-> звоним на {target}, RTP на {mediaAddress}:"
                + reservation.RtpPort.ToString(CultureInfo.InvariantCulture));

        SipOutgoingCall call = _agent.PlaceCall(target, offer.EncodedData());

        await foreach (SipCallEvent value in call.Events.WithCancellation(cancellationToken))
        {
            switch (value)
            {
                case SipCallEvent.Answered response:
                    NegotiatedMedia media = SdpNegotiator.ResolveAnswer(
                        SdpParser.Parse(response.Body.Span),
                        offer);

                    await OpenAsync(call.CallId, media, offer, reservation, target, cancellationToken)
                        .ConfigureAwait(false);
                    PumpCall(call.CallId, call.Events);
                    return call.CallId;

                case SipCallEvent.Failed failure:
                    Console.WriteLine(
                        "[x] " + SipCallErrors.DescribeCallFailure(failure.Status, failure.Reason));
                    reservation.Release();
                    return null;

                case SipCallEvent.Ended ended:
                    Console.WriteLine($"   разговор кончился до ответа: {ended.Reason}");
                    reservation.Release();
                    return null;

                default:
                    break;
            }
        }

        reservation.Release();
        return null;
    }

    /// <summary>Поднимает медиа линии и отдаёт её слою линий.</summary>
    private async Task OpenAsync(
        string callId,
        NegotiatedMedia media,
        SessionDescription local,
        RtpPortReservation reservation,
        string peer,
        CancellationToken cancellationToken)
    {
        MediaSession session = new(media, reservation, _bus);
        session.OnDiagnostic = message => Console.WriteLine($"   {callId}: {message}");
        session.OnTransportFailure = reason => Console.WriteLine($"   [x] {callId}: транспорт медиа: {reason}");

        var line = new MediaSessionLine(session, local);
        _media[callId] = line;

        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        await Controller.AttachAsync(callId, line, peer).ConfigureAwait(false);

        Console.WriteLine(
            $"[v] линия {callId} на {peer}: {media.Codec.SdpName()}, "
                + $"порт {session.LocalPort.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Следит за концом разговора и убирает линию, когда он кончился.</summary>
    private void PumpCall(string callId, IAsyncEnumerable<SipCallEvent> events) => _ = Task.Run(
        async () =>
        {
            try
            {
                await foreach (SipCallEvent value in events.WithCancellation(_pumps.Token))
                {
                    if (value is SipCallEvent.Ended ended)
                    {
                        Console.WriteLine($"   линия {callId} кончилась: {ended.Reason}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Controller.DetachAsync(callId).ConfigureAwait(false);

            if (_media.TryRemove(callId, out MediaSessionLine? line))
            {
                Console.WriteLine($"   итог линии {callId}: {line.Session.Summary()}");
                await line.StopAsync().ConfigureAwait(false);
                line.Session.Dispose();
            }

            PrintLines();
        },
        CancellationToken.None);

    private async Task HangUpAllAsync()
    {
        await Controller.HangUpAllAsync().ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }

    private void PrintLines()
    {
        IReadOnlyList<LineView> lines = Controller.Lines;
        if (lines.Count == 0)
        {
            Console.WriteLine("   линий нет");
            return;
        }

        foreach (LineView line in lines)
        {
            string state = line.IsActive ? "активна" : "фоновая";
            if (line.IsHeldByOperator)
            {
                state += ", удержание";
            }

            if (line.IsHeldByServer)
            {
                state += ", удержание сервера";
            }

            Console.WriteLine($"   [{line.Peer}] {line.CallId}: {state}");
        }
    }
}
