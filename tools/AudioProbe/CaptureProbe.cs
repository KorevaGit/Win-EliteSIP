using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioProbe;

/// <summary>
/// Замер такта захвата. Звука не издаёт: здесь проверяется не эхоподавление, а
/// то, на чём оно будет стоять.
///
/// **Зачем это отдельно и первым.** В macOS-версии тактом тракта распоряжалась
/// система: <c>AVAudioEngine</c> звал обработчик, и вопроса «а ровно ли он
/// зовёт» не возникало. В Windows разрешение системного таймера по умолчанию
/// 15,6 мс, и пакетизация 20 мс, построенная на сне потока, разваливается ещё
/// до того, как в тракте появится первый кодек. Поэтому тактует само
/// устройство: <c>IAudioClient</c> в режиме событий будит поток каждый период,
/// и он же отсчитывает отправку RTP.
///
/// Мерить надо было до, а не после: если такт рваный, «плохой звук» спишут на
/// эхоподавление, кодек, сеть — на что угодно, кроме настоящей причины.
/// </summary>
internal static class CaptureProbe
{
    public static int Run(int seconds)
    {
        using var enumerator = new MMDeviceEnumerator();

        MMDevice device;
        try
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (COMException)
        {
            Console.Error.WriteLine("Устройства захвата нет.");
            return 1;
        }

        using (device)
        {
            Console.WriteLine($"Устройство: {device.FriendlyName}");
            return Measure(device, seconds);
        }
    }

    private static int Measure(MMDevice device, int seconds)
    {
        using AudioClient client = device.CreateAudioClient();
        WaveFormat mix = client.MixFormat;

        // Shared mode: в исключительном режиме софтфон отобрал бы устройство у
        // всей системы, а оператор работает в CRM и слушает уведомления. Цена —
        // период задаёт микшер, и подогнать его под ровно 20 мс нельзя.
        // Поэтому кадр 20 мс собирается из периодов, а не запрашивается у ОС.
        long period = client.DefaultDevicePeriod;

        client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback,
            period,
            0,
            mix,
            Guid.Empty);

        using var frameReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        client.SetEventHandle(frameReady.SafeWaitHandle.DangerousGetHandle());

        AudioCaptureClient capture = client.AudioCaptureClient;

        Console.WriteLine(
            "Формат:     {0} Гц, {1} кан., {2} бит",
            mix.SampleRate,
            mix.Channels,
            mix.BitsPerSample);
        Console.WriteLine("Период:     {0:F2} мс (буфер {1} кадров)", period / 10000.0, client.BufferSize);
        Console.WriteLine("Замер:      {0} с. Звук не выводится.", seconds);
        Console.WriteLine();

        var intervals = new List<double>(capacity: seconds * 200);
        int discontinuities = 0;
        int silentBuffers = 0;
        int wakeups = 0;
        long framesTotal = 0;
        int timeouts = 0;
        bool isFirstPacket = true;

        var watch = Stopwatch.StartNew();
        double previous = 0;
        double deadline = seconds * 1000.0;

        client.Start();
        try
        {
            while (watch.Elapsed.TotalMilliseconds < deadline)
            {
                // Таймаут вдвое больше периода: если устройство не разбудило нас
                // за это время, такт уже сорван, и это надо посчитать, а не
                // ждать дальше молча.
                if (!frameReady.WaitOne(millisecondsTimeout: Math.Max(4, (int)(period / 10000.0 * 2))))
                {
                    timeouts++;
                    continue;
                }

                double now = watch.Elapsed.TotalMilliseconds;
                if (previous > 0)
                {
                    intervals.Add(now - previous);
                }

                previous = now;
                wakeups++;

                // Забирать надо всё, что накопилось: одно пробуждение может
                // принести несколько пакетов, и невыбранный остаток превращается
                // в разрыв на следующем витке.
                while (capture.GetNextPacketSize() > 0)
                {
                    _ = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);

                    framesTotal += frames;

                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
                    {
                        // Первый пакет после Start() приходит с этим флагом
                        // всегда: он означает «здесь начало потока», а не
                        // «здесь потерян звук». Считать его сбоем — значит
                        // объявлять исправный тракт рваным на каждом прогоне,
                        // а потом привыкнуть и не заметить настоящий разрыв.
                        if (isFirstPacket)
                        {
                            isFirstPacket = false;
                        }
                        else
                        {
                            discontinuities++;
                        }
                    }
                    else
                    {
                        isFirstPacket = false;
                    }

                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        silentBuffers++;
                    }

                    capture.ReleaseBuffer(frames);
                }
            }
        }
        finally
        {
            client.Stop();
        }

        Report(intervals, mix, framesTotal, wakeups, discontinuities, silentBuffers, timeouts, watch.Elapsed);
        return discontinuities == 0 && timeouts == 0 ? 0 : 2;
    }

    private static void Report(
        List<double> intervals,
        WaveFormat mix,
        long framesTotal,
        int wakeups,
        int discontinuities,
        int silentBuffers,
        int timeouts,
        TimeSpan elapsed)
    {
        if (intervals.Count == 0)
        {
            Console.Error.WriteLine("Ни одного пробуждения — устройство не тактирует.");
            return;
        }

        intervals.Sort();

        Console.WriteLine("Пробуждений:      {0}", wakeups);
        Console.WriteLine("Интервал, мс:     медиана {0:F2}   p95 {1:F2}   макс {2:F2}   мин {3:F2}",
            Percentile(intervals, 0.50),
            Percentile(intervals, 0.95),
            intervals[^1],
            intervals[0]);

        // Сколько звука реально доехало против того, сколько его прошло по
        // часам. Расхождение — это и есть потерянный звук, и слышно его как
        // щелчки, а не как тишину.
        double expected = elapsed.TotalSeconds * mix.SampleRate;
        double delivered = framesTotal;
        Console.WriteLine("Отсчётов:         {0} из ожидаемых {1:F0}  ({2:F3}%)",
            framesTotal,
            expected,
            delivered / expected * 100.0);

        Console.WriteLine("Разрывов потока:  {0}", discontinuities);
        Console.WriteLine("Тишина в буфере:  {0}", silentBuffers);
        Console.WriteLine("Таймаутов:        {0}", timeouts);
        Console.WriteLine();

        bool ok = discontinuities == 0 && timeouts == 0;
        Console.WriteLine(ok
            ? "Такт ровный. На таком фундаменте эхоподавление можно мерить."
            : "Такт рваный. Мерить эхоподавление на нём бессмысленно — сначала разобраться здесь.");
    }

    private static double Percentile(List<double> sorted, double p)
    {
        int index = (int)Math.Clamp(Math.Round((sorted.Count - 1) * p), 0, sorted.Count - 1);
        return sorted[index];
    }
}
