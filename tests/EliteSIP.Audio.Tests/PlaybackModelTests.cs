using EliteSIP.Audio;
using EliteSIP.MediaCore;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Модель приёмного тракта целиком: сеть → джиттер-буфер → подача → кольцо →
/// вывод, на настоящих <see cref="JitterBuffer"/> и
/// <see cref="PlaybackRateController"/>, без звуковой карты.
/// </summary>
///
/// <remarks>
/// Написана 1 октября 2026, когда выпуск 0.1.69 на живом звонке показал
/// «темп приёма −4273 ppm» и 10 недоборов за минуту на сети с джиттером
/// 0,7 мс. Регулятор и буфер по отдельности проверены, а дефект живёт между
/// ними: в том, до какого уровня подача держит кольцо, на какую глубину
/// буфер начинает выдачу и куда регулятор тянет их сумму. Такое ловится только
/// моделью всей цепочки.
/// </remarks>
public sealed class PlaybackModelTests
{
    private const int RenderRate = 48_000;
    private const int FrameSamples = 960;

    [Theory]
    [InlineData(0, 0.0, 0, 0)]
    [InlineData(100, 0.7, 0, 0)]
    [InlineData(-100, 0.7, 0, 0)]
    [InlineData(60, 3.0, 0, 0)]
    [InlineData(-60, 8.0, 0, 0)]

    // Подача зависла (сборка мусора, загруженный процессор): кольцо обязано
    // пережить это без пустого такта вывода. При двух кадрах запаса вместо
    // трёх эти строки дают десятки пустых тактов — щелчков.
    [InlineData(100, 0.7, 2000, 30)]
    [InlineData(-100, 0.7, 3000, 45)]
    [InlineData(100, 8.0, 1500, 40)]
    public void На_ровной_сети_тракт_не_прячет_ни_кадра_и_темп_не_в_упоре(
        int driftPpm, double jitterMs, int stallEveryMs, int stallMs)
    {
        ModelResult result = Run(seconds: 180, driftPpm, jitterMs, stallEveryMs, stallMs);

        Assert.True(
            result.Concealed == 0 && result.EmptyTicks == 0,
            $"спрятано {result.Concealed}, недоборов {result.Underruns}, пустых тактов вывода {result.EmptyTicks}, темп {result.FinalPpm:F0} ppm");
        Assert.True(
            Math.Abs(result.FinalPpm) < 1000,
            $"темп {result.FinalPpm:F0} ppm — регулятор тянет не туда");
    }

    private sealed record ModelResult(int Concealed, int Underruns, int EmptyTicks, double FinalPpm);

    /// <summary>
    /// Шаг модели — миллисекунда. Сеть шлёт кадр раз в 20 мс по часам
    /// отправителя (с уходом <paramref name="driftPpm"/> относительно карты) и
    /// случайной задержкой до <paramref name="jitterMs"/>; вывод забирает по
    /// 10 мс и будит подачу; подача просыпается через 0–2 мс после такта вывода
    /// (с 2 октября 2026 её будит сам вывод, а не таймер в 15,6 мс), а не
    /// дождавшись — через 20 мс. Раз в <paramref name="stallEveryMs"/> подача
    /// замирает на <paramref name="stallMs"/>, вывод при этом идёт дальше.
    /// </summary>
    private static ModelResult Run(int seconds, int driftPpm, double jitterMs, int stallEveryMs, int stallMs)
    {
        VoiceAudioConfiguration configuration = new();
        int lead = FrameSamples * configuration.PlaybackLeadFrames;

        // Буфер — тот же, что строит разговор, а не копия его параметров:
        // дефект 0.1.69 жил именно в них.
        JitterBuffer jitter = MediaSession.MakeJitter(
            new NegotiatedMedia(AudioCodec.Pcmu, 0, "192.0.2.1", 4000, null, MediaDirection.SendRecv, 20, null),
            configuration);
        PlaybackRateController controller = new(FrameSamples * configuration.TargetPlaybackFrames, RenderRate);
        Random random = new(12345);

        double ring = 0;
        double nextSend = 0;
        ushort sequence = 0;
        double nextFeed = 0;
        double lastObserved = 0;
        bool woken = false;
        int emptyTicks = 0;
        double stallUntil = -1;
        double nextStall = stallEveryMs;
        List<(double At, RtpPacket Packet)> inFlight = [];

        for (double now = 0; now < seconds * 1000.0; now += 1)
        {
            // Сеть: часы отправителя идут на driftPpm быстрее нашей карты.
            while (nextSend <= now)
            {
                RtpPacket packet = new(0, sequence, (uint)(sequence * 160), 1, new byte[160]);
                inFlight.Add((nextSend + (random.NextDouble() * jitterMs), packet));
                sequence++;
                nextSend += 20.0 / (1 + (driftPpm / 1e6));
            }

            for (int i = inFlight.Count - 1; i >= 0; i--)
            {
                if (inFlight[i].At <= now)
                {
                    jitter.Push(inFlight[i].Packet, inFlight[i].At / 1000);
                    inFlight.RemoveAt(i);
                }
            }

            // Вывод: 480 отсчётов раз в 10 мс.
            // Пустой такт после первой секунды — это щелчок у оператора.
            if (((int)now % 10) == 0)
            {
                if (jitter.IsPlaying && now > 1000 && ring < 480)
                {
                    emptyTicks++;
                }

                ring = Math.Max(0, ring - 480);
                woken = true;
            }

            if (stallEveryMs > 0 && now >= nextStall)
            {
                stallUntil = now + stallMs;
                nextStall += stallEveryMs;
            }

            if (now < stallUntil)
            {
                continue;
            }

            // Подача.
            if ((woken && now >= nextFeed) || now >= nextFeed + 20)
            {
                woken = false;
                nextFeed = now + (random.NextDouble() * 2);

                if (jitter.IsPlaying)
                {
                    PlaybackBacklog backlog = new(jitter.Depth, jitter.TargetDepth);
                    controller.Retarget(WasapiVoiceAudioEngine.BacklogTarget(backlog, FrameSamples));
                    controller.Observe((int)ring + (backlog.Frames * FrameSamples), (now - lastObserved) / 1000);
                }

                lastObserved = now;

                while (ring < lead && jitter.Pop() is not null)
                {
                    ring += FrameSamples * controller.Correction;
                }
            }
        }

        return new ModelResult(jitter.Statistics.Concealed, jitter.Statistics.Underruns, emptyTicks, controller.CorrectionPpm);
    }
}
