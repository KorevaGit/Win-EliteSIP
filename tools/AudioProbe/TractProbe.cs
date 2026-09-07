using System.Collections.Concurrent;
using System.Diagnostics;
using EliteSIP.Audio;
using NAudio.CoreAudioApi;

namespace AudioProbe;

/// <summary>
/// Прогон боевого тракта. Этап W4.
///
/// <b>Чем отличается от <see cref="LoopProbe"/>.</b> Тот проверял, годится ли
/// WASAPI вообще: свои потоки, своё кольцо, линейная интерполяция вместо
/// пересчёта частоты. Здесь работает продуктовый
/// <see cref="WasapiVoiceAudioEngine"/> целиком — с обработкой голоса,
/// кодеком, балансом отсчётов и компенсацией часов, — а стенд только
/// замыкает петлю и смотрит на числа.
///
/// <b>Зачем петля через кодек.</b> Кадры, вышедшие в сеть, тут же просятся
/// обратно на воспроизведение. Это не имитация разговора — собеседника здесь
/// нет, — а способ нагрузить весь путь целиком: кодирование, декодирование,
/// кольцо, пересчёт в обе стороны и подачу. Молчащий путь воспроизведения
/// оставил бы половину тракта непроверенной, а расхождение часов — вовсе
/// неизмеримым.
///
/// <b>Приговор выносится числами и по ходу дела, а не в конце.</b> Урок W0:
/// итоговая строка стенда однажды оказалась неверна ровно на лучшем
/// результате, и заметить это можно было только сверив её с таблицей рядом.
/// Поэтому здесь печатается срез каждые полминуты, а итог только сводит
/// худшее из увиденного.
/// </summary>
internal static class TractProbe
{
    /// <summary>
    /// Допуск на число кадров в срезе.
    ///
    /// Процент: кадр уходит раз в двадцать миллисекунд, за полминуты их
    /// полторы тысячи, и полтора десятка потерянных — это уже слышная пауза.
    /// </summary>
    private const double FrameTolerance = 0.01;

    public static int Run(string? inputName, string? outputName, int seconds, bool audible, bool matrix)
    {
        if (matrix)
        {
            return RunMatrix(seconds, audible);
        }

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice input = Devices.Pick(enumerator, DataFlow.Capture, null, inputName);
        using MMDevice output = Devices.Pick(enumerator, DataFlow.Render, null, outputName);

        return RunOne(input.ID, output.ID, input.FriendlyName, output.FriendlyName, seconds, audible) ? 0 : 1;
    }

    /// <summary>
    /// Матрица устройств: тракт на каждой паре «вход — выход».
    ///
    /// Приёмка W4 требует именно матрицы, а не одного устройства, и вот
    /// почему: замеры W0 расходились между устройствами на два порядка.
    /// Проводная гарнитура отдавала 99,98% звука, донгл в том же прогоне —
    /// 79%, и оба выглядели для системы одинаково исправными.
    /// </summary>
    private static int RunMatrix(int seconds, bool audible)
    {
        using var enumerator = new MMDeviceEnumerator();
        IReadOnlyList<MMDevice> inputs = Devices.All(enumerator, DataFlow.Capture);
        IReadOnlyList<MMDevice> outputs = Devices.All(enumerator, DataFlow.Render);

        Console.WriteLine($"Матрица: {inputs.Count} входов × {outputs.Count} выходов, по {seconds} с на пару.");
        Console.WriteLine();

        List<string> failures = [];

        foreach (MMDevice input in inputs)
        {
            foreach (MMDevice output in outputs)
            {
                string title = $"{input.FriendlyName} → {output.FriendlyName}";
                Console.WriteLine(new string('=', 70));
                Console.WriteLine(title);
                Console.WriteLine(new string('=', 70));

                if (!RunOne(input.ID, output.ID, input.FriendlyName, output.FriendlyName, seconds, audible))
                {
                    failures.Add(title);
                }

                Console.WriteLine();
            }
        }

        foreach (MMDevice device in inputs)
        {
            device.Dispose();
        }

        foreach (MMDevice device in outputs)
        {
            device.Dispose();
        }

        Console.WriteLine(new string('=', 70));
        if (failures.Count == 0)
        {
            Console.WriteLine("МАТРИЦА ПРОЙДЕНА: все пары отработали без деградации.");
            return 0;
        }

        Console.WriteLine($"МАТРИЦА НЕ ПРОЙДЕНА, пар с замечаниями: {failures.Count}");
        foreach (string failure in failures)
        {
            Console.WriteLine("  " + failure);
        }

        return 1;
    }

    private static bool RunOne(
        string inputId,
        string outputId,
        string inputName,
        string outputName,
        int seconds,
        bool audible)
    {
        var queue = new ConcurrentQueue<byte[]>();
        long sent = 0;
        long starved = 0;
        var events = new List<string>();
        var watch = Stopwatch.StartNew();

        var configuration = new VoiceAudioConfiguration
        {
            InputDeviceId = inputId,
            OutputDeviceId = outputId,

            // По умолчанию тишина на выходе: тракт работает целиком, звук
            // наружу не идёт. Прогон на открытых динамиках без этого — это
            // самовозбуждение, а не замер.
            PlaybackVolume = audible ? 1f : 0f,
        };

        using var engine = new WasapiVoiceAudioEngine(configuration);
        engine.Handlers = new VoiceAudioHandlers
        {
            Diagnostic = text => Console.WriteLine($"  [{watch.Elapsed.TotalSeconds,7:F1}] {text}"),
            Event = value =>
            {
                lock (events)
                {
                    events.Add($"[{watch.Elapsed.TotalSeconds,7:F1}] {value}");
                }
            },
            EncodedFrame = payload =>
            {
                Interlocked.Increment(ref sent);
                queue.Enqueue(payload.ToArray());
            },
            NeedsFrame = () =>
            {
                if (queue.TryDequeue(out byte[]? payload))
                {
                    return new PlaybackFrame(payload, false);
                }

                Interlocked.Increment(ref starved);
                return null;
            },
        };

        Console.WriteLine($"Вход:   {inputName}");
        Console.WriteLine($"Выход:  {outputName}");
        Console.WriteLine(audible ? "Режим:  СО ЗВУКОМ" : "Режим:  тишина на выходе");

        try
        {
            engine.Start();
        }
        catch (VoiceAudioException e)
        {
            Console.WriteLine($"  тракт не поднялся: {e.Message}");
            return false;
        }

        var slice = new SliceTracker();
        long previousSent = 0;
        double previousTime = 0;
        bool baselineTaken = false;
        var report = TimeSpan.FromSeconds(Math.Min(30, Math.Max(5, seconds / 6)));
        double nextReport = report.TotalSeconds;

        while (watch.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(200);

            double now = watch.Elapsed.TotalSeconds;
            if (now < nextReport)
            {
                continue;
            }

            nextReport += report.TotalSeconds;

            long total = Interlocked.Read(ref sent);
            long inSlice = total - previousSent;
            double window = now - previousTime;
            previousSent = total;
            previousTime = now;

            // Первый срез задаёт начало отсчёта и в приговор не идёт.
            //
            // Правило перенесено из W0 дословно, и первый же прогон матрицы
            // показал, зачем оно: срез включал открытие устройства и разгон
            // захвата, дал 92,71% и объявил исправный тракт негодным. В
            // оригинале стенда то же самое звучало как «разгон в замер не
            // идёт», и цена вопроса там была та же — ложный приговор.
            if (!baselineTaken)
            {
                baselineTaken = true;
                Console.WriteLine("  [{0,7:F1}] разгон, в приговор не идёт", now);
                continue;
            }

            double expected = window * 1000.0 / configuration.PacketTimeMilliseconds;
            double error = expected > 0 ? Math.Abs(inSlice - expected) / expected : 0;

            slice.Note(error, engine);

            Console.WriteLine(
                "  [{0,7:F1}] кадров {1,5} из {2,5:F0} ({3,6:P2}), {4}",
                now,
                inSlice,
                expected,
                1 - error,
                engine.CaptureActivity);
        }

        Console.WriteLine();
        Console.WriteLine(engine.Summary());

        lock (events)
        {
            if (events.Count > 0)
            {
                Console.WriteLine("События тракта:");
                foreach (string value in events)
                {
                    Console.WriteLine("  " + value);
                }
            }
        }

        bool verdict = slice.Verdict(engine, Interlocked.Read(ref starved), out string reason);
        Console.WriteLine();
        Console.WriteLine(verdict ? "ПРОЙДЕНО" : "НЕ ПРОЙДЕНО: " + reason);

        engine.Stop();
        return verdict;
    }

    /// <summary>
    /// Худшее из увиденного за прогон.
    ///
    /// Считается по ходу, а не в конце, потому что деградация может пройти и
    /// закрыться сама: тракт, замолчавший на десять минут в середине смены,
    /// в итоговых счётчиках выглядит почти исправным.
    /// </summary>
    private sealed class SliceTracker
    {
        private double _worstFrameError;
        private bool _sawBadActivity;
        private bool _sawBrokenBalance;

        public void Note(double frameError, WasapiVoiceAudioEngine engine)
        {
            _worstFrameError = Math.Max(_worstFrameError, frameError);

            if (engine.CaptureActivity is not (DeviceActivity.Working or DeviceActivity.Warmup))
            {
                _sawBadActivity = true;
            }

            if (engine.Balance is { IsBalanced: false })
            {
                _sawBrokenBalance = true;
            }
        }

        public bool Verdict(WasapiVoiceAudioEngine engine, long starved, out string reason)
        {
            List<string> problems = [];

            if (_worstFrameError > FrameTolerance)
            {
                problems.Add($"кадры уходили с ошибкой до {_worstFrameError:P2}");
            }

            if (_sawBadActivity)
            {
                problems.Add("устройство молчало или сбоило");
            }

            if (_sawBrokenBalance || engine.Balance is { IsBalanced: false })
            {
                problems.Add("баланс отсчётов разошёлся");
            }

            // Упор поправки темпа в приговор не входит намеренно. В петле
            // кольцо пустует по построению — собеседника нет, и на старте
            // играть нечего, — так что регулятор упирается в предел
            // закономерно. Признак остаётся в сводке тракта, где его читает
            // человек и понимает, что видит.
            reason = string.Join("; ", problems);
            _ = starved;
            return problems.Count == 0;
        }
    }
}
