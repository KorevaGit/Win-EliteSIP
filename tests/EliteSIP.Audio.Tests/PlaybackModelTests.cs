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
    [InlineData(0, 0.0)]
    [InlineData(100, 0.7)]
    [InlineData(-100, 0.7)]
    [InlineData(60, 3.0)]
    [InlineData(-60, 8.0)]
    public void На_ровной_сети_тракт_не_прячет_ни_кадра_и_темп_не_в_упоре(int driftPpm, double jitterMs)
    {
        ModelResult result = Run(seconds: 180, driftPpm, jitterMs);

        Assert.True(
            result.Concealed == 0,
            $"спрятано {result.Concealed}, недоборов {result.Underruns}, темп {result.FinalPpm:F0} ppm");
        Assert.True(
            Math.Abs(result.FinalPpm) < 1000,
            $"темп {result.FinalPpm:F0} ppm — регулятор тянет не туда");
    }

    private sealed record ModelResult(int Concealed, int Underruns, double FinalPpm);

    /// <summary>
    /// Шаг модели — миллисекунда. Сеть шлёт кадр раз в 20 мс по часам
    /// отправителя (с уходом <paramref name="driftPpm"/> относительно карты) и
    /// случайной задержкой до <paramref name="jitterMs"/>; вывод забирает по
    /// 10 мс; подача просыпается раз в 15,6 мс — столько на деле даёт ожидание
    /// в 5 мс при системном таймере Windows.
    /// </summary>
    private static ModelResult Run(int seconds, int driftPpm, double jitterMs)
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
            if (((int)now % 10) == 0)
            {
                ring = Math.Max(0, ring - 480);
            }

            // Подача.
            if (now >= nextFeed)
            {
                nextFeed = now + 15.6;

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

        return new ModelResult(jitter.Statistics.Concealed, jitter.Statistics.Underruns, controller.CorrectionPpm);
    }
}
