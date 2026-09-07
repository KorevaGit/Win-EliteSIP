using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioProbe;

/// <summary>
/// Прямой замер пути эха: есть ли вообще линейная связь между тем, что мы
/// отдали в динамики, и тем, что услышал микрофон.
///
/// **Почему это надо было сделать первым.** Мы перебрали объявленную задержку
/// от нуля до 260 мс, свели все вызовы APM на один поток, выключили системную
/// обработку и на захвате, и на выводе, уменьшали и увеличивали кольцо, пробовали
/// AEC3 и AECM. Подавление всё время держалось на 5–8 дБ и не отзывалось ни на
/// что. Между тем проверка без устройств давала 52,9 дБ.
///
/// Одинаковый результат у двух разных эхоподавителей означает, что дело не в
/// их настройке. Эхоподавитель строит линейную модель пути; если такой модели
/// не существует — не подавит никакой. Значит надо измерить сам путь, а не
/// качество подавления.
///
/// **Как.** Играем известный сигнал, пишем микрофон, считаем нормированную
/// взаимную корреляцию на всех задержках до 400 мс. Три возможных ответа:
///
/// * острый пик с коэффициентом в десятых долях — путь линейный, задержка
///   известна точно, и виноват тогда способ, которым мы кормим APM;
/// * размазанный низкий пик — путь есть, но плывёт: уход часов, нелинейность
///   динамика, перестраивающаяся обработка;
/// * пика нет вовсе — микрофон не слышит наш сигнал, и всё, что мерилось
///   раньше, меряло что-то другое.
///
/// Считается на 8 кГц: путь эха — акустика комнаты, выше четырёх килогерц там
/// нечего искать, а вычислений в тридцать шесть раз меньше.
/// </summary>
internal static class EchoPathProbe
{
    private const int Rate = 48000;
    private const int FrameMs = 10;

    /// <summary>Во сколько раз прореживаем перед корреляцией.</summary>
    private const int Decimation = 6;

    /// <summary>До какой задержки искать.</summary>
    private const int MaxLagMs = 400;

    public static int Run(string? inputName, string? outputName, int seconds, bool raw, float level)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice input = Devices.Pick(enumerator, DataFlow.Capture, null, inputName);
        using MMDevice output = Devices.Pick(enumerator, DataFlow.Render, null, outputName);

        Console.WriteLine($"Вход:   {input.FriendlyName}");
        Console.WriteLine($"Выход:  {output.FriendlyName}");
        Console.WriteLine($"Захват: {(raw ? "сырой" : "обычный")},  уровень сигнала {level:F2}");
        Console.WriteLine();

        using AudioClient captureClient = input.CreateAudioClient();
        using AudioClient renderClient = output.CreateAudioClient();
        WaveFormat captureFormat = captureClient.MixFormat;
        WaveFormat renderFormat = renderClient.MixFormat;

        if (captureFormat.SampleRate != Rate || renderFormat.SampleRate != Rate)
        {
            Console.Error.WriteLine("Замер пути сделан только для 48 кГц на обоих концах.");
            return 1;
        }

        int frame = Rate / 1000 * FrameMs;

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
            AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
            captureClient.DefaultDevicePeriod, 0, captureFormat, Guid.Empty);
        renderClient.Initialize(
            AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
            renderClient.DefaultDevicePeriod, 0, renderFormat, Guid.Empty);

        using var captureReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var renderReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        captureClient.SetEventHandle(captureReady.SafeWaitHandle.DangerousGetHandle());
        renderClient.SetEventHandle(renderReady.SafeWaitHandle.DangerousGetHandle());

        // Здесь дальняя сторона звучит непрерывно: паузы нужны были для замера
        // остатка, а корреляции они только мешают.
        var farEnd = new FarEndSource(Rate, level);
        var playback = new MonoRing(Rate * 2);

        var reference = new List<float>(Rate * seconds);
        var microphone = new List<float>(Rate * seconds);
        var micLeft = new List<float>(Rate * seconds);
        var micRight = new List<float>(Rate * seconds);
        var recording = new ManualResetEventSlim(false);

        using var stop = new CancellationTokenSource();

        var renderThread = new Thread(() =>
        {
            AudioRenderClient render = renderClient.AudioRenderClient;
            int channels = renderFormat.Channels;
            int bufferFrames = renderClient.BufferSize;
            var mono = new float[Rate];

            while (!stop.IsCancellationRequested)
            {
                if (!renderReady.WaitOne(100))
                {
                    continue;
                }

                int free = Math.Min(bufferFrames - renderClient.CurrentPadding, mono.Length);
                if (free <= 0)
                {
                    continue;
                }

                int got = playback.Read(mono.AsSpan(0, free));
                if (got < free)
                {
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
        })
        { Priority = ThreadPriority.Highest, IsBackground = true, Name = "path-render" };

        var captureThread = new Thread(() =>
        {
            AudioCaptureClient capture = captureClient.AudioCaptureClient;
            int channels = captureFormat.Channels;
            var scratch = new float[frame * 8];
            var left = new float[frame * 8];
            var right = new float[frame * 8];
            var pendingLeft = new MonoRing(Rate * 2);
            var pendingRight = new MonoRing(Rate * 2);
            var pending = new MonoRing(Rate * 2);
            var far = new float[frame];
            var near = new float[frame];
            var nearLeft = new float[frame];
            var nearRight = new float[frame];

            while (!stop.IsCancellationRequested)
            {
                if (!captureReady.WaitOne(100))
                {
                    continue;
                }

                while (capture.GetNextPacketSize() > 0)
                {
                    nint buffer = capture.GetBuffer(out int frames, out _);
                    if (frames > scratch.Length)
                    {
                        scratch = new float[frames];
                        left = new float[frames];
                        right = new float[frames];
                    }

                    if (frames > 0)
                    {
                        unsafe
                        {
                            float* source = (float*)buffer;
                            for (int i = 0; i < frames; i++)
                            {
                                // Каналы сохраняются по отдельности, а не только
                                // сведёнными.
                                //
                                // Подозрение, ради которого это сделано:
                                // «Набор микрофонов» — это массив, и если его
                                // капсюли включены дифференциально, усреднение
                                // вычитает общий сигнал, то есть ровно то эхо,
                                // которое мы ищем. Тогда все прежние замеры
                                // мерили остаток после вычитания.
                                if (channels > 0)
                                {
                                    left[i] = source[i * channels];
                                }

                                right[i] = channels > 1 ? source[(i * channels) + 1] : left[i];

                                float sum = 0f;
                                for (int c = 0; c < channels; c++)
                                {
                                    sum += source[(i * channels) + c];
                                }

                                scratch[i] = sum / channels;
                            }
                        }

                        pending.Write(scratch.AsSpan(0, frames));
                        pendingLeft.Write(left.AsSpan(0, frames));
                        pendingRight.Write(right.AsSpan(0, frames));
                    }

                    capture.ReleaseBuffer(frames);
                }

                while (pending.Count >= frame && !stop.IsCancellationRequested)
                {
                    farEnd.Fill(far);
                    playback.Write(far);
                    pending.Read(near);
                    pendingLeft.Read(nearLeft);
                    pendingRight.Read(nearRight);

                    if (recording.IsSet)
                    {
                        reference.AddRange(far);
                        microphone.AddRange(near);
                        micLeft.AddRange(nearLeft);
                        micRight.AddRange(nearRight);
                    }
                }
            }
        })
        { Priority = ThreadPriority.Highest, IsBackground = true, Name = "path-capture" };

        Console.WriteLine("СЕЙЧАС БУДЕТ ШУМ, {0} с. Молчите и не двигайте ноутбук.", seconds);
        Console.WriteLine();

        renderClient.Start();
        captureClient.Start();
        renderThread.Start();
        captureThread.Start();

        Thread.Sleep(2000);
        recording.Set();
        Thread.Sleep(seconds * 1000);

        stop.Cancel();
        captureThread.Join(1000);
        renderThread.Join(1000);
        captureClient.Stop();
        renderClient.Stop();

        Console.WriteLine("=== сведённые каналы ===");
        int verdict = Analyse(reference, microphone);
        Console.WriteLine();
        Console.WriteLine("=== только первый капсюль ===");
        Analyse(reference, micLeft, brief: true);
        Console.WriteLine("=== только второй капсюль ===");
        Analyse(reference, micRight, brief: true);
        return verdict;
    }

    private static int Analyse(List<float> reference, List<float> microphone, bool brief = false)
    {
        int count = Math.Min(reference.Count, microphone.Count);
        if (count < Rate)
        {
            Console.Error.WriteLine("Записано слишком мало.");
            return 1;
        }

        float[] far = Decimate(reference, count);
        float[] near = Decimate(microphone, count);
        int rate = Rate / Decimation;
        int maxLag = MaxLagMs * rate / 1000;

        Console.WriteLine("Записано {0:F1} с, считаю корреляцию на {1} задержках…", count / (double)Rate, maxLag);
        Console.WriteLine();

        double bestValue = 0;
        int bestLag = 0;
        var profile = new double[maxLag];

        // near[t] сравнивается с far[t - lag]: эхо приходит позже опорного.
        for (int lag = 0; lag < maxLag; lag++)
        {
            double sum = 0;
            double farEnergy = 0;
            double nearEnergy = 0;
            int n = near.Length - lag;

            for (int t = lag; t < near.Length; t++)
            {
                float a = far[t - lag];
                float b = near[t];
                sum += a * b;
                farEnergy += (double)a * a;
                nearEnergy += (double)b * b;
            }

            double denominator = Math.Sqrt(farEnergy * nearEnergy);
            double value = denominator > 0 ? Math.Abs(sum / denominator) : 0;
            profile[lag] = value;

            if (value > bestValue)
            {
                bestValue = value;
                bestLag = lag;
            }

            _ = n;
        }

        Console.WriteLine("Пик корреляции:   {0:F3} на задержке {1} мс", bestValue, bestLag * 1000 / rate);

        // Насколько пик острый: сравниваем его с типичным уровнем вдали от него.
        double background = 0;
        int backgroundCount = 0;
        for (int lag = 0; lag < maxLag; lag++)
        {
            if (Math.Abs(lag - bestLag) > rate / 50)
            {
                background += profile[lag];
                backgroundCount++;
            }
        }

        background = backgroundCount > 0 ? background / backgroundCount : 0;
        Console.WriteLine("Фон вдали от пика: {0:F3}", background);
        Console.WriteLine("Отношение:         {0:F1}", background > 0 ? bestValue / background : 0);
        Console.WriteLine();

        if (brief)
        {
            return bestValue >= 0.2 ? 0 : 2;
        }

        Console.WriteLine("Профиль по задержке (шаг 20 мс):");
        for (int ms = 0; ms < MaxLagMs; ms += 20)
        {
            int lag = ms * rate / 1000;
            if (lag >= maxLag)
            {
                break;
            }

            int bar = (int)(profile[lag] / Math.Max(bestValue, 1e-9) * 40);
            Console.WriteLine("  {0,4} мс  {1:F3}  {2}", ms, profile[lag], new string('#', Math.Max(0, bar)));
        }

        Console.WriteLine();

        if (bestValue >= 0.2)
        {
            Console.WriteLine("Путь эха линейный и устойчивый. Виноват способ, которым мы кормим APM.");
            return 0;
        }

        if (bestValue >= 0.05)
        {
            Console.WriteLine("Связь есть, но слабая: путь плывёт или сильно нелинеен.");
            return 0;
        }

        Console.WriteLine("Связи нет. Микрофон не слышит наш сигнал так, как мы думаем.");
        return 2;
    }

    private static float[] Decimate(List<float> source, int count)
    {
        int outputLength = count / Decimation;
        var result = new float[outputLength];
        for (int i = 0; i < outputLength; i++)
        {
            float sum = 0;
            for (int j = 0; j < Decimation; j++)
            {
                sum += source[(i * Decimation) + j];
            }

            result[i] = sum / Decimation;
        }

        return result;
    }
}
