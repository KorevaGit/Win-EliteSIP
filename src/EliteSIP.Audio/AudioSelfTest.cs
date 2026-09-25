using System.Collections.Concurrent;

namespace EliteSIP.Audio;

/// <summary>Чем кончилась самопроверка звука.</summary>
/// <param name="IsSuccess">Записалось и проигралось.</param>
/// <param name="Summary">Строка для оператора: что именно вышло.</param>
public readonly record struct AudioSelfTestResult(bool IsSuccess, string Summary);

/// <summary>
/// «Проверить микрофон и звук»: пять секунд с микрофона и сразу обратно в уши.
///
/// <b>Зачем.</b> Вопрос «меня слышно?» оператор задаёт коллеге, занимая двоих
/// и получая ответ «вроде да». Здесь он проверяется без второго человека — и
/// проверяется тем же трактом, что и разговор: то же устройство, та же
/// обработка голоса, то же усиление, тот же кодек. Запись мимо тракта отвечала
/// бы на другой вопрос — «слышит ли микрофон», — а ломается обычно не он, а то,
/// что стоит после него.
///
/// <b>Кадры хранятся кодированными и только в памяти.</b> Не из экономии:
/// разговор уходит в линию именно такими, и если голос портит кодер — а с ним и
/// G.711 на восьми килогерцах, — оператор обязан услышать ровно это, а не
/// чистую запись, по которой всё хорошо. На диск не попадает ничего: голос
/// оператора — не то, что стоит оставлять в каталоге настроек.
///
/// <b>Устройство занято только на время проверки.</b> Первая попытка на macOS
/// держала микрофон открытым всё время, пока открыт раздел настроек, — ради
/// живой шкалы. Bluetooth-гарнитура при открытом микрофоне сидит в режиме
/// гарнитуры (HFP), и звук всей системы становился моно и глухим. Здесь тракт
/// поднимается по кнопке и отпускается сразу — по концу, по «Остановить», по
/// закрытию окна или раздела и по первому признаку звонка
/// (<see cref="Abort"/>).
/// </summary>
public sealed class AudioSelfTest : IDisposable
{
    /// <summary>Пять секунд: короче — не успеть сказать фразу, длиннее — не дослушают.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Сколько ждать, пока снятая проверка отпустит устройство.
    /// </summary>
    ///
    /// <remarks>
    /// Снятие ждёт отпускания, а не только просит о нём: звонок, ради которого
    /// проверку снимают, открывает то же устройство следующей строкой, и два
    /// захвата на одной гарнитуре — это отказ тракта разговора. Секунды хватает
    /// с запасом: проверка смотрит на отмену каждые двадцать миллисекунд, а
    /// остановка тракта — десятки.
    /// </remarks>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(1);

    private readonly Func<VoiceAudioConfiguration, IVoiceAudioEngine> _open;
    private readonly Action<string>? _log;
    private readonly TimeSpan _duration;
    private readonly Lock _gate = new();

    /// <summary>Отпущено ли устройство. Взводится в начале, опускается в самом конце.</summary>
    private readonly ManualResetEventSlim _released = new(initialState: true);

    private CancellationTokenSource? _cancellation;
    private IVoiceAudioEngine? _engine;
    private bool _isRunning;

    /// <param name="open">Как поднять тракт. Тот же способ, что и у разговора.</param>
    /// <param name="log">Журнал приложения.</param>
    /// <param name="duration">Длительность записи. Не по умолчанию — только в проверках.</param>
    public AudioSelfTest(
        Func<VoiceAudioConfiguration, IVoiceAudioEngine> open,
        Action<string>? log = null,
        TimeSpan? duration = null)
    {
        _open = open;
        _log = log;
        _duration = duration ?? DefaultDuration;
    }

    /// <summary>Идёт ли проверка прямо сейчас.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    /// <summary>Что происходит сейчас: строка для окна настроек.</summary>
    public event Action<string>? Progress;

    /// <summary>
    /// Пишет и проигрывает. Возвращается, когда всё отзвучало или проверку сняли.
    /// </summary>
    ///
    /// <remarks>
    /// Тракт поднимается свой и на время проверки. Проверять звук посреди
    /// разговора нельзя — это отнятый у собеседника микрофон, — и запрещает это
    /// вызывающий: у проверки нет способа знать о звонках.
    /// </remarks>
    public async Task<AudioSelfTestResult> RunAsync(
        VoiceAudioConfiguration configuration,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_isRunning)
            {
                return new AudioSelfTestResult(false, "проверка уже идёт");
            }

            _isRunning = true;
            _released.Reset();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            _cancellation = cancellation;
        }

        try
        {
            return await Task.Run(() => Run(configuration, cancellation.Token), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _isRunning = false;
                _cancellation = null;
            }

            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Снимает идущую проверку и ждёт, пока она отпустит устройство.
    /// </summary>
    ///
    /// <returns><c>true</c>, если проверка шла и её сняли.</returns>
    ///
    /// <remarks>
    /// Зовётся кнопкой «Остановить», закрытием окна или раздела и — главное —
    /// первым признаком звонка, до того как звонок откроет устройство.
    /// Возвращается, только когда тракт проверки остановлен и разобран (или
    /// вышел срок <see cref="ReleaseTimeout"/>, о чём скажет журнал).
    /// </remarks>
    public bool Abort()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (!_isRunning)
            {
                return false;
            }

            cancellation = _cancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Проверка кончилась сама между проверкой и отменой.
        }

        if (!_released.Wait(ReleaseTimeout))
        {
            _log?.Invoke("проверка звука не отпустила устройство за секунду");
        }

        return true;
    }

    /// <summary>
    /// Применяет настройки к идущей проверке: ползунки усиления и громкости,
    /// обработку, устройство.
    /// </summary>
    ///
    /// <remarks>
    /// Ползунок, сдвинутый во время проверки, обязан подействовать сразу и
    /// быть виден на шкале: иначе проверка отвечает на вопрос о настройках,
    /// которых уже нет. Кодек остаётся тем, на котором проверка поднята.
    /// </remarks>
    public void Apply(VoiceAudioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IVoiceAudioEngine? engine;
        lock (_gate)
        {
            engine = _engine;
        }

        try
        {
            engine?.Apply(configuration);
        }
        catch (Exception error) when (error is ObjectDisposedException
                                          or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
        {
            // Проверка как раз уходит — применять некуда.
        }
    }

    /// <summary>Уровни идущей проверки для шкал. <c>null</c> — проверки нет.</summary>
    public AudioLevels? TakeLevels()
    {
        IVoiceAudioEngine? engine;
        lock (_gate)
        {
            engine = _engine;
        }

        return engine?.TakeLevels();
    }

    private AudioSelfTestResult Run(VoiceAudioConfiguration configuration, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            return Record(configuration, token);
        }
        catch (OperationCanceledException)
        {
            return new AudioSelfTestResult(false, "проверка остановлена");
        }
        finally
        {
            _released.Set();
        }
    }

    private AudioSelfTestResult Record(VoiceAudioConfiguration configuration, CancellationToken token)
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

        lock (_gate)
        {
            _engine = engine;
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
            Wait(_duration, token);

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
            Wait(_duration + TimeSpan.FromSeconds(1), token);

            var seconds = frames * (configuration.PacketTimeMilliseconds / 1000.0);

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
            lock (_gate)
            {
                _engine = null;
            }

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

    /// <summary>Снимает идущую проверку и отпускает своё.</summary>
    public void Dispose()
    {
        Abort();
        _released.Dispose();
    }

    private static void Wait(TimeSpan span, CancellationToken token)
    {
        // Отмена проверяется часто, а не ждётся целиком: снимают проверку ради
        // звонка, и каждая миллисекунда здесь — задержка звонка.
        if (token.WaitHandle.WaitOne(span))
        {
            token.ThrowIfCancellationRequested();
        }
    }
}
