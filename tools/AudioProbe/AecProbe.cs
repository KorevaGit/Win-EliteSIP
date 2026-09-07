using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundFlow.Extensions.WebRtc.Apm;

namespace AudioProbe;

/// <summary>
/// Замер эхоподавления. Ради этого этап W0 и затевался.
///
/// **Что заменяет.** В macOS-версии весь аудиотракт стоял на одной строке —
/// <c>setVoiceProcessingEnabled(true)</c>. Apple отдавала системный блок
/// обработки голоса: эхоподавитель, шумодав и АРУ того же качества, что в
/// FaceTime, бесплатно и одним флагом. В Windows такого переключателя нет, и
/// обработку приходится приносить свою — WebRTC APM (AEC3).
///
/// **Как меряется.** «Послушать, не слышно ли эха» — не приёмка: два человека
/// на двух гарнитурах получат два разных ответа, а через месяц никто не
/// вспомнит, было ли лучше. Поэтому считается ERLE (Echo Return Loss
/// Enhancement) — во сколько раз обработка ослабила то, что микрофон поймал из
/// динамиков. Это одно число в децибелах, воспроизводимое от прогона к прогону.
///
/// **Во время замера надо молчать.** Собственная речь — это полезный сигнал, а
/// не эхо; эхоподавитель обязан её сохранить, и в замер она войдёт как
/// «неподавленное эхо», занизив результат.
///
/// **Почему весь APM живёт на одном потоке.** Первая версия звала
/// <c>AnalyzeReverseStream</c> из потока воспроизведения, а <c>ProcessStream</c>
/// — из потока захвата, как это устроено в самом WebRTC. Эхоподавление не
/// работало вовсе: ERLE 1–3 дБ, и одинаковый на всех девяти пробах задержки от
/// нуля до 260 мс. Плоский отклик на перебор задержки означает, что опорного
/// сигнала эхоподавитель не видел совсем, — при верном опорном сигнале и
/// неверной задержке график имел бы горб.
///
/// Здесь оба конца сведены на поток захвата: он же и тактирует. Кадр дальней
/// стороны рождается, отдаётся эхоподавителю опорным и уходит в кольцо
/// воспроизведения — в одном месте и в одном порядке. Поток вывода стал
/// глупым: только забрать из кольца и отдать устройству.
/// </summary>
internal static class AecProbe
{
    /// <summary>
    /// Частота обработки. 48 кГц — родная частота устройств этой машины, и на
    /// ней в стенде нет ни одного пересчёта частоты. Пересчёт добавил бы свою
    /// задержку и своё искажение к тому, что как раз измеряется.
    /// </summary>
    private const int ProcessingRate = 48000;

    /// <summary>Кадр APM — всегда 10 мс, это заложено в WebRTC.</summary>
    private const int FrameMs = 10;

    private const int ConvergeSeconds = 5;

    /// <summary>
    /// Запас кольца воспроизведения. Кадры дальней стороны рождаются в такте
    /// микрофона, а забираются в такте динамиков; без запаса вывод голодает на
    /// каждой заминке, и в микрофон приходит рваное эхо, которое не подавит
    /// никакой AEC.
    /// </summary>
    private static double _playbackPrimeMs = 60.0;

    /// <summary>
    /// Сколько раз кольцу воспроизведения не хватило звука. Считается потому,
    /// что голодание рвёт связь между опорным сигналом и тем, что реально
    /// прозвучало: эхоподавителю показывают одно, а из динамика идёт другое, и
    /// сойтись он не может в принципе.
    /// </summary>
    private static int _playbackStarved;

    /// <summary>
    /// Заполнение буфера устройства вывода, как его видел поток вывода на
    /// последнем витке. Нужно потоку захвата, чтобы посчитать задержку.
    /// </summary>
    private static int _renderPadding;

    /// <summary>
    /// Латентность обоих потоков в отсчётах — то, что устройство держит внутри
    /// себя сверх наших буферов. Из счётчиков колец её не видно, а в задержку
    /// она входит: динамик звучит позже, чем мы отдали кадр, а микрофон отдаёт
    /// позже, чем звук до него дошёл.
    /// </summary>
    private static int _streamLatencyFrames;

    public static int Run(
        string? inputName,
        string? outputName,
        int seconds,
        int delayMs,
        bool quiet = false,
        bool suppression = true,
        int convergeSeconds = ConvergeSeconds,
        bool raw = false,
        double primeMs = 60.0,
        bool mobile = false)
    {
        if (seconds <= convergeSeconds + 2)
        {
            Console.Error.WriteLine($"Прогон короче {convergeSeconds + 3} с ничего не покажет: сходимость съест замер.");
            return 1;
        }

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice input = Devices.Pick(enumerator, DataFlow.Capture, null, inputName);
        using MMDevice output = Devices.Pick(enumerator, DataFlow.Render, null, outputName);

        if (!quiet)
        {
            Console.WriteLine($"Вход:   {input.FriendlyName}");
            Console.WriteLine($"Выход:  {output.FriendlyName}");
            Console.WriteLine();
        }

        using AudioClient captureClient = input.CreateAudioClient();
        using AudioClient renderClient = output.CreateAudioClient();
        WaveFormat captureFormat = captureClient.MixFormat;
        WaveFormat renderFormat = renderClient.MixFormat;

        if (captureFormat.SampleRate != ProcessingRate || renderFormat.SampleRate != ProcessingRate)
        {
            Console.Error.WriteLine(
                $"Стенд считает эхоподавление только на {ProcessingRate} Гц без пересчёта частоты. "
                + $"Здесь захват {captureFormat.SampleRate}, вывод {renderFormat.SampleRate}.");
            return 1;
        }

        int frame = ProcessingRate / 1000 * FrameMs;

        using var apm = new AudioProcessingModule();
        using (var config = new ApmConfig())
        {
            // Порядок важен: сначала конвейер, потом блоки. SetPipeline задаёт
            // частоту, на которой APM вообще работает.
            config.SetPipeline(ProcessingRate, false, false, DownmixMethod.AverageChannels);

            // mobileMode = false: это AEC3, полноценный. Мобильный режим —
            // упрощённый AECM для телефонов, он заметно слабее.
            config.SetEchoCanceller(true, mobile);

            // Шумодав и АРУ выключаются ключом --isolate: они тоже меняют
            // энергию выхода, и с ними ERLE перестаёт быть мерой одного лишь
            // эхоподавления. Для приговора «слышно ли эхо» нужна вся цепочка,
            // для поиска причины — только AEC.
            config.SetNoiseSuppression(suppression, NoiseSuppressionLevel.High);
            config.SetHighPassFilter(suppression);
            config.SetGainController2(suppression);

            apm.ApplyConfig(config);
        }

        apm.Initialize();

        if (!quiet)
        {
            Console.WriteLine(
                "Обработка: WebRTC APM, AEC3{0}, {1} Гц, кадр {2} мс, захват {3}",
                suppression ? " + шумодав + АРУ" : " один, без шумодава и АРУ",
                ProcessingRate,
                FrameMs,
                raw ? "СЫРОЙ, системная обработка выключена" : "обычный, с обработкой системы");
            Console.WriteLine("Задержка опорного сигнала объявлена: {0} мс", delayMs);
            Console.WriteLine();
            Console.WriteLine("СЕЙЧАС БУДЕТ ШУМ. Молчите: собственная речь занизит результат.");
            Console.WriteLine($"Первые {convergeSeconds} с — сходимость, в замер не идут.");
            Console.WriteLine();
        }

        var playback = new MonoRing(ProcessingRate * 2);
        var meter = new ErleMeter();
        Interlocked.Exchange(ref _playbackStarved, 0);

        // Сырой режим захвата.
        //
        // Улика, из-за которой он здесь появился: уровень микрофона скакал на
        // 26 дБ между прогонами при неподвижном ноутбуке и неизменном опорном
        // сигнале — от −38,6 до −64,5 дБ. Так себя ведёт не акустика, а чужая
        // обработка в тракте. «Набор микрофонов» Realtek — это массив со своим
        // эхоподавителем и своей АРУ в системном APO, и он воюет с нашим: AEC3
        // видит на входе уже обработанное и меняющееся во времени эхо, для
        // которого линейной модели не существует.
        //
        // AUDCLNT_STREAMOPTIONS_RAW выключает эту обработку. Категория
        // Communications — отдельно от неё: она сообщает системе, что это
        // разговор, и на части устройств меняет маршрутизацию.
        // Сырым должен быть и вывод, а не только захват.
        //
        // Первая попытка сделала сырым один захват. Уровень микрофона сразу
        // перестал гулять — с 26 дБ разброса до полутора, — но подавление так и
        // осталось плоским на 6-8 дБ. Оставшаяся половина причины на другом
        // конце: динамики Realtek имеют свой APO с тонкомпенсацией и
        // подъёмом низа, и он нелинейный. Эхоподавитель строит линейную модель
        // пути «опорный сигнал → микрофон»; если между ними стоит нелинейная
        // обработка, такой модели не существует, и сойтись не на чем.
        if (raw)
        {
            captureClient.SetClientProperties(AudioStreamCategory.Communications, AudioClientStreamOptions.Raw);
            renderClient.SetClientProperties(AudioStreamCategory.Communications, AudioClientStreamOptions.Raw);
        }
        else
        {
            captureClient.SetClientProperties(AudioStreamCategory.Communications);
            renderClient.SetClientProperties(AudioStreamCategory.Communications);
        }

        captureClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback,
            captureClient.DefaultDevicePeriod,
            0,
            captureFormat,
            Guid.Empty);

        renderClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback,
            renderClient.DefaultDevicePeriod,
            0,
            renderFormat,
            Guid.Empty);

        // Латентность потоков спрашивается у самих устройств — и только после
        // Initialize, до него клиент про неё не знает. У каждого устройства она
        // своя, и на Bluetooth она на порядок больше, чем на USB.
        Interlocked.Exchange(
            ref _streamLatencyFrames,
            (int)((captureClient.StreamLatency + renderClient.StreamLatency) / 10_000_000.0 * ProcessingRate));

        using var captureReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var renderReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        captureClient.SetEventHandle(captureReady.SafeWaitHandle.DangerousGetHandle());
        renderClient.SetEventHandle(renderReady.SafeWaitHandle.DangerousGetHandle());

        using var stop = new CancellationTokenSource();
        var counting = new ManualResetEventSlim(false);

        // Кольцо заполняется заранее: иначе первые кадры вывода — тишина, а
        // эхоподавитель в это время уже сходится и учится на пустом месте.
        var farEnd = new FarEndSource(ProcessingRate);
        _playbackPrimeMs = primeMs;
        var prime = new float[(int)(primeMs / 1000.0 * ProcessingRate)];
        farEnd.Fill(prime);
        playback.Write(prime);

        var renderThread = new Thread(() =>
            RenderLoop(renderClient, renderReady, renderFormat, playback, stop.Token))
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "aec-render",
        };

        var captureThread = new Thread(() =>
            CaptureLoop(
                captureClient, captureReady, captureFormat, apm, farEnd, playback,
                meter, frame, delayMs, counting, stop.Token))
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "aec-capture",
        };

        renderClient.Start();
        captureClient.Start();
        renderThread.Start();
        captureThread.Start();

        Thread.Sleep(convergeSeconds * 1000);
        meter.Reset();
        counting.Set();

        Thread.Sleep((seconds - convergeSeconds) * 1000);

        stop.Cancel();
        captureThread.Join(1000);
        renderThread.Join(1000);
        captureClient.Stop();
        renderClient.Stop();

        return Report(meter, apm, delayMs, quiet);
    }

    /// <summary>
    /// Поток вывода намеренно глупый: забрать из кольца и отдать устройству.
    /// Всё, что касается эхоподавителя, живёт на потоке захвата.
    /// </summary>
    private static void RenderLoop(
        AudioClient client,
        EventWaitHandle ready,
        WaveFormat format,
        MonoRing playback,
        CancellationToken token)
    {
        AudioRenderClient render = client.AudioRenderClient;
        int channels = format.Channels;
        int bufferFrames = client.BufferSize;
        var mono = new float[format.SampleRate];

        while (!token.IsCancellationRequested)
        {
            if (!ready.WaitOne(100))
            {
                continue;
            }

            int padding = client.CurrentPadding;
            Interlocked.Exchange(ref _renderPadding, padding);

            int free = bufferFrames - padding;
            if (free <= 0)
            {
                continue;
            }

            if (free > mono.Length)
            {
                free = mono.Length;
            }

            int got = playback.Read(mono.AsSpan(0, free));
            if (got < free)
            {
                Interlocked.Increment(ref _playbackStarved);
                Array.Clear(mono, got, free - got);
            }

            nint buffer = render.GetBuffer(free);
            unsafe
            {
                float* destination = (float*)buffer;
                for (int i = 0; i < free; i++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        destination[(i * channels) + c] = mono[i];
                    }
                }
            }

            render.ReleaseBuffer(free, AudioClientBufferFlags.None);
        }
    }

    private static void CaptureLoop(
        AudioClient client,
        EventWaitHandle ready,
        WaveFormat format,
        AudioProcessingModule apm,
        FarEndSource farEnd,
        MonoRing playback,
        ErleMeter meter,
        int frame,
        int delayMs,
        ManualResetEventSlim counting,
        CancellationToken token)
    {
        AudioCaptureClient capture = client.AudioCaptureClient;
        int channels = format.Channels;
        var pending = new MonoRing(ProcessingRate * 2);
        var scratch = new float[frame * 8];

        var near = new float[1][];
        var nearOut = new float[1][];
        var far = new float[1][];
        var farOut = new float[1][];
        near[0] = new float[frame];
        nearOut[0] = new float[frame];
        far[0] = new float[frame];
        farOut[0] = new float[frame];

        using var streamConfig = new StreamConfig(ProcessingRate, 1);

        while (!token.IsCancellationRequested)
        {
            if (!ready.WaitOne(100))
            {
                continue;
            }

            while (capture.GetNextPacketSize() > 0)
            {
                nint buffer = capture.GetBuffer(out int frames, out _);
                if (frames > scratch.Length)
                {
                    scratch = new float[frames];
                }

                if (frames > 0)
                {
                    unsafe
                    {
                        float* source = (float*)buffer;
                        for (int i = 0; i < frames; i++)
                        {
                            float sum = 0f;
                            for (int c = 0; c < channels; c++)
                            {
                                sum += source[(i * channels) + c];
                            }

                            scratch[i] = sum / channels;
                        }
                    }

                    pending.Write(scratch.AsSpan(0, frames));
                }

                capture.ReleaseBuffer(frames);
            }

            while (pending.Count >= frame && !token.IsCancellationRequested)
            {
                // 1. Рождается кадр дальней стороны.
                farEnd.Fill(far[0]);
                bool farEndActive = Energy(far[0]) > 1e-9;

                // 2. Он же отдаётся эхоподавителю опорным — до того, как
                //    придёт микрофонный кадр с его отражением.
                apm.ProcessReverseStream(far, streamConfig, streamConfig, farOut);

                // 3. И уходит в кольцо воспроизведения.
                playback.Write(far[0]);

                // 4. Микрофонный кадр обрабатывается.
                pending.Read(near[0]);
                double before = Energy(near[0]);

                // Задержка считается, а не объявляется.
                //
                // Объявленная константа не работает: попытка назвать её числом
                // дала 37,6 дБ в одном прогоне и 2,7 дБ в следующем на том же
                // значении 160 мс. Причина в том, что задержка не постоянная —
                // она зависит от того, где на старте улеглось кольцо
                // воспроизведения, а это гонка потоков, разная каждый раз.
                //
                // Считать её, наоборот, просто: это время, которое кадр
                // проведёт в кольце и в буфере устройства, прежде чем
                // прозвучит, плюс то, что микрофонный кадр уже пролежал у нас.
                // Ровно это и означает «задержка между ProcessReverseStream и
                // ProcessStream» в документации APM.
                int ringFill = playback.Count;
                int padding = Interlocked.CompareExchange(ref _renderPadding, 0, 0);
                int captured = pending.Count;

                int measured = delayMs >= 0
                    ? delayMs
                    : (int)((ringFill + padding + captured + _streamLatencyFrames) * 1000.0 / ProcessingRate);

                meter.NoteDelay(measured, ringFill, padding, captured);
                apm.SetStreamDelayMs(measured);
                apm.ProcessStream(near, streamConfig, streamConfig, nearOut);

                double after = Energy(nearOut[0]);

                if (counting.IsSet)
                {
                    meter.Add(before, after, farEndActive, Energy(far[0]));
                }
            }
        }
    }

    private static double Energy(float[] samples)
    {
        double sum = 0;
        foreach (float sample in samples)
        {
            sum += (double)sample * sample;
        }

        return sum / samples.Length;
    }

    private static int Report(ErleMeter meter, AudioProcessingModule apm, int delayMs, bool quiet)
    {
        if (quiet)
        {
            Console.WriteLine(
                "  задержка {0,4} мс   опорный {1,6:F1} дБ   микрофон {2,6:F1} дБ   после {3,6:F1} дБ   ERLE {4,5:F1} дБ   голоданий {5}",
                delayMs,
                meter.FarEndDb,
                meter.InputDb,
                meter.OutputDb,
                meter.Erle,
                _playbackStarved);
            Console.WriteLine("       посчитанная задержка: среднее {0:F0} мс, от {1} до {2}",
                meter.AverageDelay,
                meter.MinDelay == int.MaxValue ? 0 : meter.MinDelay,
                meter.MaxDelay);
            return 0;
        }

        Console.WriteLine();

        if (meter.ActiveFrames < 50)
        {
            Console.Error.WriteLine("Кадров с сигналом дальней стороны почти не было — замер не состоялся.");
            return 1;
        }

        double erle = meter.Erle;

        Console.WriteLine("Кадров в замере:          {0} с эхом, {1} в тишине", meter.ActiveFrames, meter.QuietFrames);
        Console.WriteLine("Опорный сигнал:           {0,7:F1} дБ", meter.FarEndDb);
        Console.WriteLine("Микрофон до обработки:    {0,7:F1} дБ", meter.InputDb);
        Console.WriteLine("После обработки:          {0,7:F1} дБ", meter.OutputDb);
        Console.WriteLine();
        Console.WriteLine("ERLE (подавление эха):    {0,7:F1} дБ", erle);
        Console.WriteLine("Задержка по мнению APM:   {0} мс", apm.GetStreamDelayMs());
        Console.WriteLine("Голоданий вывода:         {0}", _playbackStarved);
        Console.WriteLine("Задержка посчитанная:     среднее {0:F0} мс, от {1} до {2}",
            meter.AverageDelay,
            meter.MinDelay == int.MaxValue ? 0 : meter.MinDelay,
            meter.MaxDelay);
        Console.WriteLine("  из них кольцо вывода:   {0:F1} мс", meter.AverageRing / ProcessingRate * 1000.0);
        Console.WriteLine("  буфер устройства:       {0:F1} мс", meter.AveragePadding / ProcessingRate * 1000.0);
        Console.WriteLine("  не разобранный захват:  {0:F1} мс", meter.AverageCaptured / ProcessingRate * 1000.0);
        Console.WriteLine("  латентность потоков:    {0:F1} мс", _streamLatencyFrames / (double)ProcessingRate * 1000.0);
        Console.WriteLine();

        if (erle >= 30)
        {
            Console.WriteLine("Эхо подавлено. Так себя вёл VoiceProcessingIO на macOS — путь A подтверждён.");
            return 0;
        }

        if (erle >= 20)
        {
            Console.WriteLine("Эхо подавлено заметно, но не до конца. Разбираться с объявленной задержкой.");
            return 0;
        }

        Console.WriteLine("Эхоподавление не работает.");
        return 2;
    }

    private sealed class ErleMeter
    {
        private readonly Lock _gate = new();
        private double _input;
        private double _output;
        private double _far;

        public int ActiveFrames { get; private set; }

        public int QuietFrames { get; private set; }

        public void Add(double before, double after, bool farEndActive, double farEnergy)
        {
            lock (_gate)
            {
                if (farEndActive)
                {
                    _input += before;
                    _output += after;
                    _far += farEnergy;
                    ActiveFrames++;
                }
                else
                {
                    QuietFrames++;
                }
            }
        }

        public int MinDelay { get; private set; } = int.MaxValue;

        public int MaxDelay { get; private set; }

        public double AverageDelay => _delayCount == 0 ? 0 : (double)_delaySum / _delayCount;

        private long _delaySum;
        private long _delayCount;

        public double AverageRing => _delayCount == 0 ? 0 : (double)_ringSum / _delayCount;

        public double AveragePadding => _delayCount == 0 ? 0 : (double)_paddingSum / _delayCount;

        public double AverageCaptured => _delayCount == 0 ? 0 : (double)_capturedSum / _delayCount;

        private long _ringSum;
        private long _paddingSum;
        private long _capturedSum;

        public void NoteDelay(int delayMs, int ring, int padding, int captured)
        {
            lock (_gate)
            {
                _delaySum += delayMs;
                _delayCount++;
                _ringSum += ring;
                _paddingSum += padding;
                _capturedSum += captured;
                MinDelay = Math.Min(MinDelay, delayMs);
                MaxDelay = Math.Max(MaxDelay, delayMs);
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _delaySum = 0;
                _delayCount = 0;
                _ringSum = 0;
                _paddingSum = 0;
                _capturedSum = 0;
                MinDelay = int.MaxValue;
                MaxDelay = 0;
                _input = 0;
                _output = 0;
                _far = 0;
                ActiveFrames = 0;
                QuietFrames = 0;
            }
        }

        public double Erle
        {
            get
            {
                lock (_gate)
                {
                    return ActiveFrames == 0 || _output <= 0 ? 0 : 10.0 * Math.Log10(_input / _output);
                }
            }
        }

        public double InputDb => Db(_input, ActiveFrames);

        public double OutputDb => Db(_output, ActiveFrames);

        public double FarEndDb => Db(_far, ActiveFrames);

        private static double Db(double energy, int frames) =>
            frames == 0 || energy <= 0 ? double.NegativeInfinity : 10.0 * Math.Log10(energy / frames);
    }
}
