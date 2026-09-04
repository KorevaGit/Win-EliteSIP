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
/// наушников. Это одно число в децибелах, воспроизводимое от прогона к
/// прогону.
///
/// Порядок: в наушники подаётся сигнал дальней стороны, он же отдаётся
/// эхоподавителю как опорный. Микрофон ловит его отражение. ERLE — отношение
/// энергии микрофона до обработки к энергии после, взятое только на тех
/// кадрах, где дальняя сторона звучит.
///
/// **Во время замера надо молчать.** Собственная речь — это полезный сигнал, а
/// не эхо; эхоподавитель обязан её сохранить, и в замер она войдёт как
/// «неподавленное эхо», занизив результат.
/// </summary>
internal static class AecProbe
{
    /// <summary>
    /// Частота обработки. 48 кГц — родная частота обоих устройств этой машины,
    /// и на ней в стенде нет ни одного пересчёта частоты. Пересчёт добавил бы
    /// свою задержку и своё искажение к тому, что мы как раз измеряем.
    /// APM работает на 8, 16, 32 и 48 кГц; в бою частота будет выбираться под
    /// кодек, но качество эхоподавления проверяется здесь, а не там.
    /// </summary>
    private const int ProcessingRate = 48000;

    /// <summary>Кадр APM — всегда 10 мс, это заложено в WebRTC.</summary>
    private const int FrameMs = 10;

    /// <summary>
    /// Сколько дать эхоподавителю на сходимость, прежде чем начать считать.
    /// AEC3 подстраивается под задержку и характеристику помещения не мгновенно.
    /// </summary>
    private const int ConvergeSeconds = 5;

    public static int Run(string? inputName, string? outputName, int seconds, int delayMs)
    {
        if (seconds <= ConvergeSeconds + 5)
        {
            Console.Error.WriteLine($"Прогон короче {ConvergeSeconds + 6} с ничего не покажет: сходимость съест замер.");
            return 1;
        }

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice input = Devices.Pick(enumerator, DataFlow.Capture, null, inputName);
        using MMDevice output = Devices.Pick(enumerator, DataFlow.Render, null, outputName);

        Console.WriteLine($"Вход:   {input.FriendlyName}");
        Console.WriteLine($"Выход:  {output.FriendlyName}");
        Console.WriteLine();

        using AudioClient captureClient = input.CreateAudioClient();
        using AudioClient renderClient = output.CreateAudioClient();
        WaveFormat captureFormat = captureClient.MixFormat;
        WaveFormat renderFormat = renderClient.MixFormat;

        if (captureFormat.SampleRate != ProcessingRate || renderFormat.SampleRate != ProcessingRate)
        {
            Console.Error.WriteLine(
                $"Стенд считает эхоподавление только на {ProcessingRate} Гц без пересчёта частоты. "
                + $"Здесь захват {captureFormat.SampleRate}, вывод {renderFormat.SampleRate}. "
                + "Пересчёт добавил бы свою задержку к тому, что измеряется.");
            return 1;
        }

        int frame = ProcessingRate / 1000 * FrameMs;

        using var apm = new AudioProcessingModule();
        using (var config = new ApmConfig())
        {
            // mobileMode = false: это AEC3, полноценный. Мобильный режим —
            // упрощённый AECM для телефонов, он заметно слабее и на настольной
            // машине не нужен.
            config.SetEchoCanceller(true, false);
            config.SetNoiseSuppression(true, NoiseSuppressionLevel.High);
            config.SetHighPassFilter(true);
            config.SetGainController2(true);
            config.SetPipeline(ProcessingRate, false, false, DownmixMethod.AverageChannels);
            apm.ApplyConfig(config);
        }

        apm.Initialize();

        Console.WriteLine("Обработка: WebRTC APM, AEC3 + шумодав + АРУ, {0} Гц, кадр {1} мс", ProcessingRate, FrameMs);
        Console.WriteLine("Задержка опорного сигнала объявлена: {0} мс", delayMs);
        Console.WriteLine();
        Console.WriteLine("СЕЙЧАС В НАУШНИКАХ БУДЕТ ШУМ. Молчите: собственная речь занизит результат.");
        Console.WriteLine($"Первые {ConvergeSeconds} с — сходимость, в замер не идут.");
        Console.WriteLine();

        var farEnd = new FarEndSource(ProcessingRate);
        var reference = new MonoRing(ProcessingRate * 2);
        var meter = new ErleMeter();

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
        var counting = new ManualResetEventSlim(false);

        var renderThread = new Thread(() =>
            RenderLoop(renderClient, renderReady, renderFormat, farEnd, reference, frame, apm, stop.Token))
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "aec-render",
        };

        var captureThread = new Thread(() =>
            CaptureLoop(captureClient, captureReady, captureFormat, apm, reference, meter, frame, delayMs, counting, stop.Token))
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "aec-capture",
        };

        renderClient.Start();
        captureClient.Start();
        renderThread.Start();
        captureThread.Start();

        Thread.Sleep(ConvergeSeconds * 1000);
        meter.Reset();
        counting.Set();
        Console.WriteLine("Считаю…");

        Thread.Sleep((seconds - ConvergeSeconds) * 1000);

        stop.Cancel();
        captureThread.Join(1000);
        renderThread.Join(1000);
        captureClient.Stop();
        renderClient.Stop();

        return Report(meter, apm);
    }

    private static void RenderLoop(
        AudioClient client,
        EventWaitHandle ready,
        WaveFormat format,
        FarEndSource farEnd,
        MonoRing reference,
        int frame,
        AudioProcessingModule apm,
        CancellationToken token)
    {
        AudioRenderClient render = client.AudioRenderClient;
        int channels = format.Channels;
        int bufferFrames = client.BufferSize;
        var mono = new float[frame];
        var reverse = new float[1][];
        reverse[0] = mono;
        using var reverseConfig = new StreamConfig(ProcessingRate, 1);

        while (!token.IsCancellationRequested)
        {
            if (!ready.WaitOne(100))
            {
                continue;
            }

            int free = bufferFrames - client.CurrentPadding;

            // Выдаём целыми кадрами по 10 мс: APM другого размера не принимает,
            // а опорный сигнал обязан совпадать с тем, что реально прозвучало.
            while (free >= frame && !token.IsCancellationRequested)
            {
                farEnd.Fill(mono);

                // Опорный сигнал отдаётся эхоподавителю ровно тот, что уходит в
                // наушники, и до того, как придёт микрофонный кадр.
                apm.AnalyzeReverseStream(reverse, reverseConfig);
                reference.Write(mono);

                nint buffer = render.GetBuffer(frame);
                unsafe
                {
                    float* destination = (float*)buffer;
                    for (int i = 0; i < frame; i++)
                    {
                        for (int c = 0; c < channels; c++)
                        {
                            destination[(i * channels) + c] = mono[i];
                        }
                    }
                }

                render.ReleaseBuffer(frame, AudioClientBufferFlags.None);
                free -= frame;
            }
        }
    }

    private static void CaptureLoop(
        AudioClient client,
        EventWaitHandle ready,
        WaveFormat format,
        AudioProcessingModule apm,
        MonoRing reference,
        ErleMeter meter,
        int frame,
        int delayMs,
        ManualResetEventSlim counting,
        CancellationToken token)
    {
        AudioCaptureClient capture = client.AudioCaptureClient;
        int channels = format.Channels;
        var pending = new MonoRing(ProcessingRate * 2);
        var scratch = new float[frame];
        var near = new float[1][];
        var outp = new float[1][];
        near[0] = new float[frame];
        outp[0] = new float[frame];
        var referenceFrame = new float[frame];
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
                if (frames > 0 && scratch.Length < frames)
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

            // Обрабатываем накопленное целыми кадрами по 10 мс.
            while (pending.Count >= frame && !token.IsCancellationRequested)
            {
                pending.Read(near[0]);

                double before = Energy(near[0]);

                apm.SetStreamDelayMs(delayMs);
                apm.ProcessStream(near, streamConfig, streamConfig, outp);

                double after = Energy(outp[0]);

                // Опорный кадр нужен только чтобы понять, звучала ли дальняя
                // сторона: ERLE считается по тем кадрам, где эху есть откуда
                // взяться.
                bool farEndActive = reference.Count >= frame
                    && ReadActive(reference, referenceFrame);

                if (counting.IsSet)
                {
                    meter.Add(before, after, farEndActive);
                }
            }
        }
    }

    private static bool ReadActive(MonoRing reference, float[] frame)
    {
        reference.Read(frame);
        return Energy(frame) > 1e-8;
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

    private static int Report(ErleMeter meter, AudioProcessingModule apm)
    {
        Console.WriteLine();

        if (meter.ActiveFrames < 50)
        {
            Console.Error.WriteLine("Кадров с сигналом дальней стороны почти не было — замер не состоялся.");
            return 1;
        }

        double erle = meter.Erle;

        Console.WriteLine("Кадров в замере:          {0} с эхом, {1} в тишине", meter.ActiveFrames, meter.QuietFrames);
        Console.WriteLine("Микрофон до обработки:    {0,7:F1} дБ", meter.InputDb);
        Console.WriteLine("После обработки:          {0,7:F1} дБ", meter.OutputDb);
        Console.WriteLine();
        Console.WriteLine("ERLE (подавление эха):    {0,7:F1} дБ", erle);
        Console.WriteLine("Задержка по мнению APM:   {0} мс", apm.GetStreamDelayMs());
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

        Console.WriteLine("Эхоподавление не работает. Первый подозреваемый — задержка опорного сигнала (--delay).");
        return 2;
    }

    private sealed class ErleMeter
    {
        private readonly Lock _gate = new();
        private double _input;
        private double _output;
        private double _quiet;

        public int ActiveFrames { get; private set; }

        public int QuietFrames { get; private set; }

        public void Add(double before, double after, bool farEndActive)
        {
            lock (_gate)
            {
                if (farEndActive)
                {
                    _input += before;
                    _output += after;
                    ActiveFrames++;
                }
                else
                {
                    _quiet += after;
                    QuietFrames++;
                }
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _input = 0;
                _output = 0;
                _quiet = 0;
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
                    if (ActiveFrames == 0 || _output <= 0)
                    {
                        return 0;
                    }

                    return 10.0 * Math.Log10(_input / _output);
                }
            }
        }

        public double InputDb => Db(_input, ActiveFrames);

        public double OutputDb => Db(_output, ActiveFrames);

        private static double Db(double energy, int frames)
        {
            if (frames == 0 || energy <= 0)
            {
                return double.NegativeInfinity;
            }

            return 10.0 * Math.Log10(energy / frames);
        }
    }
}
