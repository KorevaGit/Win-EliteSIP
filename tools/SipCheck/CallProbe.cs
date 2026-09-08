using System.Globalization;
using EliteSIP.Audio;
using EliteSIP.MediaCore;
using EliteSIP.SipCore;

namespace EliteSIP.Tools.SipCheck;

/// <summary>
/// Сквозной звонок: сигнализация, медиа и звук вместе.
///
/// Это и есть приёмка этапа W5. До него каждая половина проверялась отдельно —
/// SipCore на живом Asterisk без звука (W2), тракт на живом железе без сети
/// (W4), — а здесь они встречаются, и вскрывается ровно то, чего не видно ни в
/// одном модульном тесте: порядок SDP, тайминги, поведение при отбое с обеих
/// сторон.
///
/// Стенд намеренно без интерфейса: на экране виден весь обмен целиком, и когда
/// звук идёт в одну сторону, видно, на каком шаге он потерялся.
/// </summary>
internal static class CallProbe
{
    /// <summary>Как часто печатать сводку разговора.</summary>
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Звонит указанное число раз подряд и держит каждый разговор заданное время.
    ///
    /// <b>Тракт на все звонки один.</b> Это не экономия строк, а пункт приёмки
    /// этапа: «звонок сразу после отбоя поднимается без пересоздания
    /// устройства». Общая шина отдаёт устройство не сразу, а с отсрочкой
    /// (<see cref="VoiceAudioBus.DefaultRetirementDelay"/>), и второй звонок
    /// обязан успеть в это окно — иначе оператор платит за каждый набор
    /// открытием устройства, а Bluetooth-гарнитура ещё и переключением режима.
    /// </summary>
    public static async Task<bool> RunAsync(
        SipUserAgent agent,
        string target,
        double talkSeconds,
        string? dtmf,
        int calls,
        bool measureLatency,
        CancellationToken cancellationToken)
    {
        using VoiceAudioBus bus = MakeBus();
        bool all = true;

        for (int attempt = 1; attempt <= Math.Max(calls, 1); attempt++)
        {
            if (calls > 1)
            {
                Console.WriteLine($"=== звонок {attempt} из {calls} ===");
            }

            if (!await PlaceAsync(agent, target, talkSeconds, dtmf, bus, measureLatency, cancellationToken)
                .ConfigureAwait(false))
            {
                all = false;
                break;
            }
        }

        return all;
    }

    /// <summary>Общий тракт разговора — тот же, что будет у приложения.</summary>
    private static VoiceAudioBus MakeBus()
    {
        VoiceAudioConfiguration configuration = new();
        return new VoiceAudioBus(
            new WasapiVoiceAudioEngine(configuration),
            settings => new WasapiVoiceAudioEngine(settings));
    }

    private static async Task<bool> PlaceAsync(
        SipUserAgent agent,
        string target,
        double talkSeconds,
        string? dtmf,
        VoiceAudioBus bus,
        bool measureLatency,
        CancellationToken cancellationToken)
    {
        // Адрес для SDP берётся у агента, а не у сокета: это тот же адрес, что
        // в Contact, то есть внешний, сообщённый сервером в received. Локальный
        // адрес за NAT даст установленный звонок без звука — самую дорогую в
        // разборе неисправность из всех возможных.
        if (agent.MediaAddress is not string mediaAddress)
        {
            Console.WriteLine("[x] сервер не сообщил наш адрес: звонить без него значит остаться без звука");
            return false;
        }

        (SessionDescription offer, RtpPortReservation reservation) = MediaSession.MakeOffer(mediaAddress);
        Console.WriteLine(
            $"-> звоним на {target}, RTP на {mediaAddress}:"
                + reservation.RtpPort.ToString(CultureInfo.InvariantCulture));

        MediaSession? session = null;
        LatencyProbe? latency = null;
        bool answered = false;
        bool endedCleanly = false;

        try
        {
            SipOutgoingCall call = agent.PlaceCall(target, offer.EncodedData());

            // Повторный INVITE сервера обслуживается всё время разговора:
            // Asterisk шлёт его на удержании, на переводе и просто перестроив
            // своё плечо. Без ответа на него разговор рвётся на ровном месте.
            agent.SetMediaRenegotiator((callId, remoteOffer) =>
                Task.FromResult(Renegotiate(session, mediaAddress, remoteOffer)));

            using CancellationTokenSource talking = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);

            await foreach (SipCallEvent value in call.Events.WithCancellation(cancellationToken))
            {
                switch (value)
                {
                    case SipCallEvent.State state:
                        Console.WriteLine($"   состояние: {Describe(state.Value)}");
                        break;

                    case SipCallEvent.Answered response:
                        (session, latency) = await OpenMediaAsync(
                                response, offer, reservation, bus, measureLatency, cancellationToken)
                            .ConfigureAwait(false);
                        if (session is null)
                        {
                            await agent.HangUpAsync(call.CallId).ConfigureAwait(false);
                            break;
                        }

                        answered = true;

                        // Отбой планируется отсюда, а не ожиданием в цикле:
                        // события звонка приходят всё это время, и пропустить
                        // отбой собеседника, стоя в ожидании своего, — значит
                        // не проверить ровно половину приёмки.
                        _ = Task.Run(
                            () => TalkAsync(agent, call.CallId, session, talkSeconds, dtmf, talking.Token),
                            CancellationToken.None);
                        break;

                    case SipCallEvent.Failed failure:
                        Console.WriteLine(
                            $"[x] звонок не состоялся: {SipCallErrors.DescribeCallFailure(failure.Status, failure.Reason)}");
                        break;

                    case SipCallEvent.Ended ended:
                        Console.WriteLine($"   разговор кончился: {ended.Reason}");
                        endedCleanly = answered;
                        break;

                    default:
                        break;
                }
            }

            await talking.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            agent.SetMediaRenegotiator(null);

            if (session is not null)
            {
                // Сводка снимается до остановки: счётчики буфера обнуляются
                // вместе с ним, и спросить их после отбоя будет уже не у кого.
                Console.WriteLine($"   итог: {session.Summary()}");

                if (latency is not null)
                {
                    AudioDelayEstimate? measured = latency.Measure();
                    LatencyProbe.Report(measured, session.Latency, latency.Levels);

                    if (measured is null || !measured.IsConfident)
                    {
                        // Данные для разбора: «не нашлась» без них — приговор
                        // без улик, а восстановить огибающие потом неоткуда.
                        Console.WriteLine($"   огибающие сложены в {latency.Dump(Path.GetTempPath())}");
                    }
                }

                await session.StopAsync().ConfigureAwait(false);
                session.Dispose();
            }
            else
            {
                reservation.Release();
            }
        }

        return endedCleanly;
    }

    /// <summary>Поднимает медиа по ответу сервера.</summary>
    private static async Task<(MediaSession? Session, LatencyProbe? Latency)> OpenMediaAsync(
        SipCallEvent.Answered response,
        SessionDescription offer,
        RtpPortReservation reservation,
        VoiceAudioBus bus,
        bool measureLatency,
        CancellationToken cancellationToken)
    {
        try
        {
            SessionDescription answer = SdpParser.Parse(response.Body.Span);
            NegotiatedMedia negotiated = SdpNegotiator.ResolveAnswer(answer, offer);

            Console.WriteLine(
                $"[v] ответили: {negotiated.Codec.SdpName()} на {negotiated.RemoteAddress}:"
                    + $"{negotiated.RemotePort.ToString(CultureInfo.InvariantCulture)}, "
                    + $"{negotiated.PacketTimeMilliseconds.ToString(CultureInfo.InvariantCulture)} мс на пакет, "
                    + $"направление {negotiated.Direction}");

            MediaSession session = new(negotiated, reservation, bus);
            session.OnDiagnostic = message => Console.WriteLine($"   медиа: {message}");
            session.OnTransportFailure = reason => Console.WriteLine($"   [x] транспорт медиа: {reason}");
            session.OnAudioEvent = value => Console.WriteLine($"   тракт: {value}");
            session.OnRemoteView = view => Console.WriteLine($"   у собеседника: {view.Summary}");

            // Проба заводится здесь, когда кодек уже согласован: огибающая
            // считается по частоте кодека, и заводить её раньше значит
            // угадывать. Обе половины замера снимаются с одной сессии —
            // отправленное и принятое обязаны быть с одних часов.
            LatencyProbe? latency = measureLatency ? new LatencyProbe(negotiated.Codec) : null;
            if (latency is not null)
            {
                session.OnSentFrame = latency.NoteSent;
                session.OnDecodedSamples = latency.NoteReceived;

                // В линию идёт проверочный сигнал, а не микрофон: в тихой
                // комнате отправлять нечего, и сравнивать вернувшееся будет не
                // с чем. Заодно замер перестаёт зависеть от того, говорит ли
                // кто-то рядом со стендом.
                session.OutgoingTestSignal = new AudioTestSignal(
                    negotiated.Codec,
                    negotiated.PacketTimeMilliseconds);

                Console.WriteLine("   замер задержки: в линию идёт проверочный сигнал вместо микрофона");
            }

            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine("[v] звук пошёл");
            return (session, latency);
        }
        catch (SdpParseException failure)
        {
            Console.WriteLine($"[x] ответ SDP не разобран: {failure.Message}");
        }
        catch (SdpNegotiationException failure)
        {
            Console.WriteLine($"[x] не договорились о медиа: {failure.Message}");
        }
        catch (VoiceAudioException failure)
        {
            // Звуковая карта — единственное, чего у стенда может не быть вовсе.
            // Сигнализация при этом жива, поэтому звонок надо положить, а не
            // бросить: иначе линия останется занятой до таймаута сервера.
            Console.WriteLine($"[x] тракт не поднялся: {failure.Message}");
        }

        return (null, null);
    }

    /// <summary>Держит разговор, печатает сводку и кладёт трубку в срок.</summary>
    private static async Task TalkAsync(
        SipUserAgent agent,
        string callId,
        MediaSession session,
        double talkSeconds,
        string? dtmf,
        CancellationToken cancellationToken)
    {
        try
        {
            if (dtmf is { Length: > 0 })
            {
                // Тон отправляется с ожиданием: «поставлено в очередь» здесь
                // ничего не проверяет, а вот «вышло в поток» — проверяет.
                bool sent = await session
                    .SendDtmfAndWaitAsync(new DtmfSequence(dtmf))
                    .ConfigureAwait(false);
                Console.WriteLine(sent
                    ? $"   DTMF {dtmf} ушёл в линию"
                    : "   [x] DTMF отправить нечем: собеседник не подтвердил telephone-event");
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(talkSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(SummaryInterval, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"   {session.Summary()}");
            }

            Console.WriteLine("-> кладём трубку");
            await agent.HangUpAsync(callId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Собеседник положил трубку раньше нас — это половина приёмки, а не
            // ошибка.
        }
    }

    /// <summary>Отвечает на повторное предложение сервера, не трогая порт.</summary>
    private static ReadOnlyMemory<byte>? Renegotiate(
        MediaSession? session,
        string mediaAddress,
        ReadOnlyMemory<byte> remoteOffer)
    {
        if (session is null)
        {
            return null;
        }

        try
        {
            SessionDescription incoming = SdpParser.Parse(remoteOffer.Span);

            // Порт в ответе — наш прежний: он объявлен в первом предложении и
            // смене посреди диалога не подлежит.
            (SessionDescription answer, NegotiatedMedia media) = SdpNegotiator.MakeAnswer(
                incoming,
                mediaAddress,
                session.LocalPort);

            MediaRenegotiation outcome = session.Renegotiate(media);
            Console.WriteLine($"   пересогласование: {outcome}, направление {media.Direction}");
            return answer.EncodedData();
        }
        catch (SdpParseException failure)
        {
            Console.WriteLine($"   [x] повторное предложение не разобрано: {failure.Message}");
        }
        catch (SdpNegotiationException failure)
        {
            Console.WriteLine($"   [x] повторное предложение не принято: {failure.Message}");
        }
        catch (MediaCodecChangedException failure)
        {
            Console.WriteLine($"   [x] {failure.Message}");
        }

        return null;
    }

    private static string Describe(SipCallState state) => state switch
    {
        SipCallState.Dialing => "набираем",
        SipCallState.Ringing => "звонит",
        SipCallState.Answered => "разговор",
        SipCallState.Ending => "кладём трубку",
        SipCallState.Ended ended => $"кончился: {ended.Reason}",
        _ => state.ToString() ?? "неизвестно",
    };
}
