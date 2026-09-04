using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioProbe;

/// <summary>
/// Дуплексная петля: микрофон → кольцо → динамики. Этап W0.
///
/// **Что здесь на самом деле проверяется.** Не звук — звук здесь заведомо
/// сырой, без эхоподавления. Проверяется то, чего в macOS-версии не было и не
/// могло быть: **два независимых потока со своими часами.**
///
/// На macOS разные устройства на вход и выход сводились агрегатным
/// устройством, которое собирала система (<c>AggregateDevice</c>, 125 строк,
/// выброшены при переносе). В WASAPI такого нет: захват тактируется часами
/// одного устройства, воспроизведение — часами другого, и кварц у них разный.
/// Расхождение в сотню миллионных долей — это отсчёт в секунду, то есть
/// полсекунды набежавшей задержки за час разговора либо, наоборот, опустевшее
/// кольцо и щелчок. Услышать это на тридцатисекундном прогоне нельзя, а
/// посчитать — можно, и здесь считается.
///
/// Второе — задержка. Она складывается из периода захвата, заполнения кольца и
/// буфера воспроизведения, и знать её надо точно: эхоподавителю нужно сказать,
/// на сколько опорный сигнал опережает микрофонный, и ошибка в этом числе
/// сводит на нет весь AEC.
/// </summary>
internal static class LoopProbe
{
    /// <summary>
    /// Целевое заполнение кольца перед первым выводом. Два периода по 10 мс:
    /// меньше — и любая заминка планировщика опустошает кольцо, больше —
    /// задержка, которую слышно в разговоре.
    /// </summary>
    private const double DefaultPrimeMs = 20.0;

    /// <summary>
    /// Сколько отбросить в начале, прежде чем считать расхождение часов.
    ///
    /// Захват и воспроизведение стартуют не одновременно и разгоняются: первые
    /// пакеты приходят с задержкой открытия устройства, до секунды на
    /// Bluetooth. Если считать по итогам всего прогона, этот разгон попадёт в
    /// числитель и даст сотни ppm на ровном месте — что и случилось на первом
    /// же замере, пока здесь стояло деление итогов прогона.
    /// </summary>
    private const int WarmupSeconds = 3;

    public static int Run(
        int? inputIndex,
        int? outputIndex,
        string? inputName,
        string? outputName,
        int seconds,
        bool mute,
        double primeMs)
    {
        if (seconds <= WarmupSeconds + 2)
        {
            Console.Error.WriteLine($"Прогон короче {WarmupSeconds + 3} с ничего не измерит: разгон съест весь замер.");
            return 1;
        }

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice input = Devices.Pick(enumerator, DataFlow.Capture, inputIndex, inputName);
        using MMDevice output = Devices.Pick(enumerator, DataFlow.Render, outputIndex, outputName);

        Console.WriteLine($"Вход:   {input.FriendlyName}");
        Console.WriteLine($"Выход:  {output.FriendlyName}");
        if (mute)
        {
            Console.WriteLine("Режим:  тишина на выходе — тракт работает, звук не выводится.");
        }

        Console.WriteLine();

        using AudioClient captureClient = input.CreateAudioClient();
        using AudioClient renderClient = output.CreateAudioClient();

        WaveFormat captureFormat = captureClient.MixFormat;
        WaveFormat renderFormat = renderClient.MixFormat;

        Console.WriteLine("Захват:  {0} Гц, {1} кан.", captureFormat.SampleRate, captureFormat.Channels);
        Console.WriteLine("Вывод:   {0} Гц, {1} кан.", renderFormat.SampleRate, renderFormat.Channels);

        if (captureFormat.SampleRate != renderFormat.SampleRate)
        {
            // Не отказ, а предупреждение: пересчёт частоты в петле сделан
            // линейной интерполяцией — для замера часов достаточно, для звука
            // нет. В бою здесь будет нормальный ресемплер.
            Console.WriteLine("         частоты разные — в петле линейный пересчёт, качество не показательно.");
        }

        Console.WriteLine();

        Console.WriteLine("Запас кольца: {0:F0} мс", primeMs);
        Console.WriteLine();

        var ring = new MonoRing(captureFormat.SampleRate * 2);
        var stats = new Stats();
        int primeFrames = (int)(primeMs / 1000.0 * captureFormat.SampleRate);

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

        using var captureReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var renderReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        captureClient.SetEventHandle(captureReady.SafeWaitHandle.DangerousGetHandle());
        renderClient.SetEventHandle(renderReady.SafeWaitHandle.DangerousGetHandle());

        using var stop = new CancellationTokenSource();

        var captureThread = new Thread(() =>
            CaptureLoop(captureClient, captureReady, ring, captureFormat, stats, stop.Token))
        {
            // Приоритет выше обычного: пропущенное пробуждение — это потерянный
            // звук, и отдавать такт фоновому пересчёту в CRM нельзя. Реального
            // времени не просим: под ним ошибка в цикле вешает машину.
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "audio-capture",
        };

        var renderThread = new Thread(() =>
            RenderLoop(renderClient, renderReady, ring, renderFormat, captureFormat.SampleRate, primeFrames, stats, mute, stop.Token))
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "audio-render",
        };

        var watch = Stopwatch.StartNew();
        captureClient.Start();
        renderClient.Start();
        captureThread.Start();
        renderThread.Start();

        Console.WriteLine($"Идёт {seconds} с (первые {WarmupSeconds} — разгон, в замер не идут)…");

        // Часы устройств, а не собственные счётчики.
        //
        // Первая версия считала расхождение по числу отсчётов, которые стенд
        // сам захватил и сам записал, — и получала две тысячи ppm на проводной
        // гарнитуре, где физически один кварц на вход и выход. Ошибка была в
        // постановке: воспроизведение выводит ровно столько, сколько ему дало
        // кольцо, то есть счётчик записей меряет пропускную способность кольца,
        // а не ход часов устройства. Про часы знает только само устройство —
        // IAudioClock, у NAudio это AudioClockClient.
        AudioClockClient captureClock = captureClient.AudioClockClient;
        AudioClockClient renderClock = renderClient.AudioClockClient;

        // Наклон, а не разность концов.
        //
        // IAudioClock обновляется раз в период, то есть квантован десятью
        // миллисекундами. На окне в семнадцать секунд это шум в шестьсот ppm —
        // больше самого измеряемого ухода, и первые прогоны честно показывали
        // то +500, то -2650 ppm на одной и той же гарнитуре. Регрессия по
        // сотням точек убирает квантование: случайная ошибка каждой пробы в
        // наклон почти не идёт.
        var drift = new DriftEstimator();
        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                double t = watch.Elapsed.TotalSeconds;
                if (t >= WarmupSeconds)
                {
                    drift.Add(t, Snapshot.Seconds(captureClock) - Snapshot.Seconds(renderClock));
                }

                Thread.Sleep(250);
            }
        })
        {
            IsBackground = true,
            Name = "clock-sampler",
        };
        sampler.Start();

        Thread.Sleep(WarmupSeconds * 1000);
        var start = Snapshot.Take(stats, watch, captureClock, renderClock);
        stats.ResetIncidents();

        Thread.Sleep((seconds - WarmupSeconds) * 1000);
        var end = Snapshot.Take(stats, watch, captureClock, renderClock);

        stop.Cancel();
        sampler.Join(1000);
        captureThread.Join(1000);
        renderThread.Join(1000);
        captureClient.Stop();
        renderClient.Stop();

        Report(stats, captureFormat, renderFormat, start, end, ring, drift);
        return stats.Underruns == 0 && stats.Overruns == 0 ? 0 : 2;
    }

    private static void CaptureLoop(
        AudioClient client,
        EventWaitHandle ready,
        MonoRing ring,
        WaveFormat format,
        Stats stats,
        CancellationToken token)
    {
        AudioCaptureClient capture = client.AudioCaptureClient;
        int channels = format.Channels;
        var mono = new float[format.SampleRate / 2];

        while (!token.IsCancellationRequested)
        {
            if (!ready.WaitOne(100))
            {
                continue;
            }

            while (capture.GetNextPacketSize() > 0)
            {
                nint buffer = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);

                if (frames > 0 && mono.Length >= frames)
                {
                    unsafe
                    {
                        float* source = (float*)buffer;
                        for (int i = 0; i < frames; i++)
                        {
                            // Сведение в моно суммой по каналам, а не взятием
                            // первого: у части гарнитур полезный сигнал лежит
                            // во втором канале, и «первый» дал бы тишину.
                            float sum = 0f;
                            for (int c = 0; c < channels; c++)
                            {
                                sum += source[(i * channels) + c];
                            }

                            mono[i] = sum / channels;
                        }
                    }

                    if (!ring.Write(mono.AsSpan(0, frames)))
                    {
                        Interlocked.Increment(ref stats.Overruns);
                    }

                    Interlocked.Add(ref stats.FramesCaptured, frames);
                }

                if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0
                    && Interlocked.Exchange(ref stats.SawFirstCapturePacket, 1) == 1)
                {
                    // Первый пакет после Start() приходит с этим флагом всегда:
                    // он означает начало потока, а не потерю звука.
                    Interlocked.Increment(ref stats.CaptureDiscontinuities);
                }

                capture.ReleaseBuffer(frames);
            }
        }
    }

    private static void RenderLoop(
        AudioClient client,
        EventWaitHandle ready,
        MonoRing ring,
        WaveFormat format,
        int sourceRate,
        int primeFrames,
        Stats stats,
        bool mute,
        CancellationToken token)
    {
        AudioRenderClient render = client.AudioRenderClient;
        int channels = format.Channels;
        int bufferFrames = client.BufferSize;
        double ratio = (double)sourceRate / format.SampleRate;
        double position = 0;
        var scratch = new float[format.SampleRate];
        bool primed = false;

        while (!token.IsCancellationRequested)
        {
            if (!ready.WaitOne(100))
            {
                continue;
            }

            int fill = ring.Count;
            stats.NoteFill(fill);

            int free = bufferFrames - client.CurrentPadding;
            if (free <= 0)
            {
                continue;
            }

            if (!primed)
            {
                // Пока кольцо не набрало запас, выводить нечего. Тишина здесь —
                // не срыв, а нормальный старт: считать её опустошением значит
                // получить тысячу «срывов» на каждом прогоне и перестать им
                // верить. Ровно это и произошло на первом замере.
                if (fill < primeFrames)
                {
                    WriteSilence(render, free, channels);
                    continue;
                }

                primed = true;
            }

            // Выводим ровно столько, сколько обеспечено кольцом. WASAPI не
            // требует заполнять буфер целиком, а досыпать тишину «до края» —
            // значит вставлять щелчок там, где звук просто ещё не доехал.
            int affordable = (int)((fill - 1) / ratio);
            int toWrite = Math.Min(free, Math.Max(0, affordable));

            if (toWrite == 0)
            {
                // Кольцо пусто после разгона — вот это уже настоящий срыв, и
                // именно так расхождение часов проявляется на слух.
                Interlocked.Increment(ref stats.Underruns);
                WriteSilence(render, free, channels);
                continue;
            }

            int need = Math.Min((int)Math.Ceiling(toWrite * ratio) + 2, scratch.Length);
            int got = ring.Read(scratch.AsSpan(0, need));
            if (got < need)
            {
                Array.Clear(scratch, got, need - got);
            }

            nint buffer = render.GetBuffer(toWrite);
            unsafe
            {
                float* destination = (float*)buffer;
                for (int i = 0; i < toWrite; i++)
                {
                    float sample = 0f;
                    if (!mute)
                    {
                        double source = position + (i * ratio);
                        int index = (int)source;
                        if (index + 1 < got)
                        {
                            float a = scratch[index];
                            float b = scratch[index + 1];
                            sample = a + (float)((b - a) * (source - index));
                        }
                        else if (index < got)
                        {
                            sample = scratch[index];
                        }
                    }

                    for (int c = 0; c < channels; c++)
                    {
                        destination[(i * channels) + c] = sample;
                    }
                }
            }

            render.ReleaseBuffer(toWrite, AudioClientBufferFlags.None);
            Interlocked.Add(ref stats.FramesRendered, toWrite);

            // Дробная часть переносится на следующий виток: без этого пересчёт
            // частоты копит ошибку и уезжает.
            position = (position + (toWrite * ratio)) % 1.0;
        }
    }

    private static void WriteSilence(AudioRenderClient render, int frames, int channels)
    {
        nint buffer = render.GetBuffer(frames);
        unsafe
        {
            float* destination = (float*)buffer;
            for (int i = 0; i < frames * channels; i++)
            {
                destination[i] = 0f;
            }
        }

        render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }

    private static void Report(
        Stats stats,
        WaveFormat captureFormat,
        WaveFormat renderFormat,
        Snapshot start,
        Snapshot end,
        MonoRing ring,
        DriftEstimator drift)
    {
        Console.WriteLine();

        // Считаем по приращениям внутри окна, а не по итогам прогона: разгон
        // устройств остался снаружи.
        double window = (end.Elapsed - start.Elapsed).TotalSeconds;
        long captured = end.FramesCaptured - start.FramesCaptured;
        long rendered = end.FramesRendered - start.FramesRendered;

        double capturedSeconds = (double)captured / captureFormat.SampleRate;
        double renderedSeconds = (double)rendered / renderFormat.SampleRate;

        // Ход самих устройств: сколько времени натикали их часы за то же окно.
        double captureClock = end.CaptureClockSeconds - start.CaptureClockSeconds;
        double renderClock = end.RenderClockSeconds - start.RenderClockSeconds;

        Console.WriteLine("Окно замера:              {0:F3} с по часам машины", window);
        Console.WriteLine("Захвачено:                {0:F3} с ({1} отсчётов)", capturedSeconds, captured);
        Console.WriteLine("Воспроизведено:           {0:F3} с ({1} отсчётов)", renderedSeconds, rendered);

        double capturePpm = window > 0 ? (captureClock - window) / window * 1_000_000.0 : 0;
        double renderPpm = window > 0 ? (renderClock - window) / window * 1_000_000.0 : 0;
        double driftPpm = captureClock > 0
            ? (renderClock - captureClock) / captureClock * 1_000_000.0
            : 0;

        Console.WriteLine();
        Console.WriteLine("Часы захвата:             {0:F3} с   {1,9:F1} ppm к часам машины", captureClock, capturePpm);
        Console.WriteLine("Часы вывода:              {0:F3} с   {1,9:F1} ppm к часам машины", renderClock, renderPpm);
        Console.WriteLine("Разность концов:          {0,9:F1} ppm  — квантована периодом, верить нельзя", driftPpm);

        double slopePpm = drift.Ppm;
        double errorPpm = drift.StandardError;
        Console.WriteLine();
        Console.WriteLine("УХОД ЧАСОВ (наклон):      {0,9:F1} ± {1:F1} ppm   по {2} пробам за {3:F0} с",
            slopePpm,
            errorPpm,
            drift.Count,
            drift.WindowSeconds);

        if (errorPpm > Math.Abs(slopePpm) / 3.0)
        {
            Console.WriteLine("                          мерили мало: ошибка сравнима с наклоном, нужен прогон длиннее");
        }
        else
        {
            Console.WriteLine("                          {0:F1} мс за минуту, {1:F1} с за восьмичасовую смену",
                Math.Abs(slopePpm) / 1000.0 * 60.0,
                Math.Abs(slopePpm) / 1_000_000.0 * 8 * 3600.0);
        }

        Console.WriteLine();
        Console.WriteLine("Кольцо, мс:               среднее {0:F1}   мин {1:F1}   макс {2:F1}",
            stats.AverageFillMs(captureFormat.SampleRate),
            stats.MinFill == int.MaxValue ? 0 : stats.MinFill / (double)captureFormat.SampleRate * 1000.0,
            stats.MaxFill / (double)captureFormat.SampleRate * 1000.0);
        Console.WriteLine("Осталось в кольце:        {0:F1} мс", ring.Count / (double)captureFormat.SampleRate * 1000.0);

        Console.WriteLine();
        Console.WriteLine("Опустошений кольца:       {0}", stats.Underruns);
        Console.WriteLine("Переполнений кольца:      {0}", stats.Overruns);
        Console.WriteLine("Разрывов захвата:         {0}", stats.CaptureDiscontinuities);

        Console.WriteLine();
        if (stats.Underruns == 0 && stats.Overruns == 0)
        {
            Console.WriteLine("Тракт держится. Расхождение часов — число выше, его надо будет компенсировать.");
        }
        else
        {
            Console.WriteLine("Тракт рвётся. Разбираться до эхоподавления.");
        }
    }

    private readonly record struct Snapshot(
        long FramesCaptured,
        long FramesRendered,
        double CaptureClockSeconds,
        double RenderClockSeconds,
        TimeSpan Elapsed)
    {
        public static Snapshot Take(
            Stats stats,
            Stopwatch watch,
            AudioClockClient captureClock,
            AudioClockClient renderClock) => new(
            Interlocked.Read(ref stats.FramesCaptured),
            Interlocked.Read(ref stats.FramesRendered),
            Seconds(captureClock),
            Seconds(renderClock),
            watch.Elapsed);

        /// <summary>
        /// Положение часов устройства в секундах. Frequency — сколько единиц
        /// позиции приходится на секунду; у разных драйверов она разная, и
        /// делить надо именно на неё, а не на частоту дискретизации.
        /// </summary>
        public static double Seconds(AudioClockClient clock)
        {
            ulong frequency = clock.Frequency;
            return frequency == 0 ? 0 : (double)clock.AdjustedPosition / frequency;
        }
    }

    private sealed class Stats
    {
        public long FramesCaptured;
        public long FramesRendered;
        public int Underruns;
        public int Overruns;
        public int CaptureDiscontinuities;
        public int SawFirstCapturePacket;

        private long _fillSum;
        private long _fillCount;
        public int MinFill = int.MaxValue;
        public int MaxFill;

        /// <summary>
        /// Обнуляет счётчики происшествий после разгона. Срывы на открытии
        /// устройства — это не то, что проверяет стенд.
        /// </summary>
        public void ResetIncidents()
        {
            Interlocked.Exchange(ref Underruns, 0);
            Interlocked.Exchange(ref Overruns, 0);
            Interlocked.Exchange(ref CaptureDiscontinuities, 0);
            Interlocked.Exchange(ref _fillSum, 0);
            Interlocked.Exchange(ref _fillCount, 0);
            Interlocked.Exchange(ref MinFill, int.MaxValue);
            Interlocked.Exchange(ref MaxFill, 0);
        }

        public void NoteFill(int frames)
        {
            Interlocked.Add(ref _fillSum, frames);
            Interlocked.Increment(ref _fillCount);

            int min = MinFill;
            while (frames < min && Interlocked.CompareExchange(ref MinFill, frames, min) != min)
            {
                min = MinFill;
            }

            int max = MaxFill;
            while (frames > max && Interlocked.CompareExchange(ref MaxFill, frames, max) != max)
            {
                max = MaxFill;
            }
        }

        public double AverageFillMs(int rate) =>
            _fillCount == 0 ? 0 : (double)_fillSum / _fillCount / rate * 1000.0;
    }
}
