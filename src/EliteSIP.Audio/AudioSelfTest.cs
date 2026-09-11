using System.Collections.Concurrent;

namespace EliteSIP.Audio;

/// <summary>Чем кончилась самопроверка звука.</summary>
/// <param name="IsSuccess">Записалось и проигралось.</param>
/// <param name="Summary">Строка для оператора: что именно вышло.</param>
public readonly record struct AudioSelfTestResult(bool IsSuccess, string Summary);

/// <summary>
/// «Записать и прослушать»: пять секунд с микрофона и сразу обратно в уши.
///
/// <b>Зачем.</b> Вопрос «меня слышно?» оператор задаёт коллеге, занимая двоих
/// и получая ответ «вроде да». Здесь он проверяется без второго человека — и
/// проверяется тем же трактом, что и разговор: то же устройство, та же
/// обработка голоса, тот же кодек. Запись мимо тракта отвечала бы на другой
/// вопрос — «слышит ли микрофон», — а ломается обычно не он, а то, что стоит
/// после него.
///
/// <b>Кадры хранятся кодированными.</b> Не из экономии: разговор уходит в
/// линию именно такими, и если голос портит кодер — а с ним и G.711 на
/// восьми килогерцах, — оператор обязан услышать ровно это, а не чистую
/// запись, по которой всё хорошо.
/// </summary>
public sealed class AudioSelfTest
{
    /// <summary>Пять секунд: короче — не успеть сказать фразу, длиннее — не дослушают.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    private readonly Func<VoiceAudioConfiguration, IVoiceAudioEngine> _open;
    private readonly Action<string>? _log;

    /// <param name="open">Как поднять тракт. Тот же способ, что и у разговора.</param>
    /// <param name="log">Журнал приложения.</param>
    public AudioSelfTest(
        Func<VoiceAudioConfiguration, IVoiceAudioEngine> open,
        Action<string>? log = null)
    {
        _open = open;
        _log = log;
    }

    /// <summary>Идёт ли проверка прямо сейчас.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Что происходит сейчас: строка для окна настроек.</summary>
    public event Action<string>? Progress;

    /// <summary>
    /// Пишет и проигрывает. Возвращается, когда всё отзвучало.
    /// </summary>
    ///
    /// <remarks>
    /// Тракт поднимается свой и на время проверки: общий занят разговором, а
    /// проверять звук посреди разговора нельзя — это отнятый у собеседника
    /// микрофон.
    /// </remarks>
    public async Task<AudioSelfTestResult> RunAsync(
        VoiceAudioConfiguration configuration,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (IsRunning)
        {
            return new AudioSelfTestResult(false, "проверка уже идёт");
        }

        IsRunning = true;
        try
        {
            return await Task.Run(() => Run(configuration, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AudioSelfTestResult(false, "проверка прервана");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private AudioSelfTestResult Run(VoiceAudioConfiguration configuration, CancellationToken token)
    {
        ConcurrentQueue<ReadOnlyMemory<byte>> recorded = new();

        IVoiceAudioEngine engine;
        try
        {
            engine = _open(configuration);
        }
        catch (Exception error) when (error is InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
        {
            // Устройства нет или его забрали в монопольном режиме. Это и есть
            // ответ на вопрос «почему меня не слышно», и он ценнее отказа.
            return new AudioSelfTestResult(false, $"тракт не поднялся: {error.Message}");
        }

        try
        {
            engine.Handlers = new VoiceAudioHandlers
            {
                Diagnostic = value => _log?.Invoke($"проверка звука: {value}"),
                EncodedFrame = frame =>
                {
                    // Копия обязательна: буфер за спиной переиспользуется
                    // потоком захвата, и сложенная в очередь ссылка к
                    // воспроизведению означала бы уже другой звук.
                    recorded.Enqueue(frame.ToArray());
                },
            };

            engine.Start();

            Progress?.Invoke("говорите");
            Wait(Duration, token);

            if (recorded.IsEmpty)
            {
                // Тракт поднялся, а кадров нет: микрофон отключён, заглушен в
                // системе или у программы нет разрешения на него.
                return new AudioSelfTestResult(
                    false, "с микрофона не пришло ни одного кадра — проверьте устройство и его громкость");
            }

            int frames = recorded.Count;

            Progress?.Invoke("слушайте");
            engine.Handlers = new VoiceAudioHandlers
            {
                Diagnostic = value => _log?.Invoke($"проверка звука: {value}"),

                // Микрофон на воспроизведении не глушится намеренно: тракт
                // остаётся тем же, каким был, — включая эхоподавитель, который
                // на записи и работал.
                NeedsFrame = () => recorded.TryDequeue(out var frame)
                    ? new PlaybackFrame(frame, IsConcealment: false)
                    : null,
            };

            // Ждём столько же плюс запас: кадры уходят в том же темпе, в каком
            // пришли, а последний ещё лежит в кольце устройства.
            Wait(Duration + TimeSpan.FromSeconds(1), token);

            var seconds = frames * 0.02;

            return new AudioSelfTestResult(
                true,
                $"записано {seconds:0.#} с и проиграно обратно. Не услышали себя — "
                + "дело в устройстве вывода; услышали тихо или с хрипом — в микрофоне.");
        }
        catch (Exception error) when (error is InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
        {
            return new AudioSelfTestResult(false, $"проверка сорвалась: {error.Message}");
        }
        finally
        {
            engine.Handlers = VoiceAudioHandlers.None;

            try
            {
                engine.Stop();
            }
            catch (Exception error) when (error is InvalidOperationException
                                              or System.Runtime.InteropServices.COMException)
            {
                // Тракт и так уходит: устройство отпустит `Dispose`.
            }

            engine.Dispose();
        }
    }

    private static void Wait(TimeSpan span, CancellationToken token)
    {
        // Отмена проверяется часто, а не ждётся целиком: оператор закрывает
        // настройки посреди проверки, и держать окно пять секунд нельзя.
        var until = DateTime.UtcNow + span;

        while (DateTime.UtcNow < until)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(50);
        }
    }
}
