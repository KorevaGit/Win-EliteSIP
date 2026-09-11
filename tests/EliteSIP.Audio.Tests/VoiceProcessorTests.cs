namespace EliteSIP.Audio.Tests;

/// <summary>
/// Обработка голоса на живой библиотеке WebRTC APM и своя АРУ — без звуковой
/// карты.
/// </summary>
public sealed class VoiceProcessorTests
{
    private const int Rate = 48_000;

    /// <summary>Длительность прогона: АРУ нужно время, чтобы разогнаться.</summary>
    private const int Seconds = 12;

    /// <summary>
    /// АРУ обязана поднимать тихий голос, а не только стоять в настройках.
    /// </summary>
    ///
    /// <remarks>
    /// До 11 сентября 2026 выключатель включал <c>GainController2</c> без
    /// настроек, а у него адаптивное усиление по умолчанию выключено: работал
    /// один ограничитель. Голос на −40 дБ выходил на тех же −40,1 дБ с АРУ и
    /// без неё — оператор слышал «тихо, как будто усиление не работает», и
    /// оно действительно не работало, а ручной ползунок при включённой АРУ
    /// погашен.
    /// </remarks>
    [Fact]
    public void АРУ_поднимает_тихий_голос_к_цели()
    {
        double without = OutputLevel(automaticGainControl: false, inputDb: -40);
        double with = OutputLevel(automaticGainControl: true, inputDb: -40);

        Assert.True(
            with - without >= 10,
            $"АРУ не усиливает: без неё {without:F1} дБ, с ней {with:F1} дБ");
        Assert.InRange(with, SpeechGainControl.TargetDb - 4, SpeechGainControl.TargetDb + 4);
    }

    [Fact]
    public void АРУ_убавляет_громкий_голос()
    {
        double with = OutputLevel(automaticGainControl: true, inputDb: -8);

        Assert.InRange(with, SpeechGainControl.TargetDb - 4, SpeechGainControl.TargetDb + 4);
    }

    [Fact]
    public void Без_АРУ_уровень_голоса_не_меняется()
    {
        // Выключенная АРУ — это «как есть»: ни подъёма, ни провала. Допуск на
        // фильтр низких частот и эхоподавитель, которые работают всегда.
        double level = OutputLevel(automaticGainControl: false, inputDb: -40);

        Assert.InRange(level, -43, -37);
    }

    [Fact]
    public void В_тишине_прибавка_не_растёт()
    {
        // Ровный шум комнаты без речи: регулятор обязан его не вытягивать. Это
        // и есть «дыхание», за которое АРУ не любят на хороших гарнитурах.
        SpeechGainControl control = new();
        float[] frame = new float[Rate / 100];
        Random random = new(7);

        for (int f = 0; f < Seconds * 100; f++)
        {
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = (float)((random.NextDouble() - 0.5) * 0.002);
            }

            control.Process(frame);
        }

        Assert.Equal(0, control.GainDb, precision: 1);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(-0.9f)]
    public void Ограничитель_не_трогает_то_что_ниже_колена(float sample)
        => Assert.Equal(sample, SpeechGainControl.Limit(sample));

    [Theory]
    [InlineData(1.5f)]
    [InlineData(-4f)]
    public void Ограничитель_не_пускает_за_шкалу(float sample)
    {
        float limited = SpeechGainControl.Limit(sample);

        Assert.InRange(Math.Abs(limited), 0.9f, 1f);
        Assert.Equal(Math.Sign(sample), Math.Sign(limited));
    }

    /// <summary>
    /// Прогоняет голосоподобный сигнал заданного уровня и возвращает уровень
    /// выхода за последние три секунды, когда регулировка улеглась.
    /// </summary>
    ///
    /// <remarks>
    /// Шумодав выключен: синтетический сигнал для него слишком ровный, и
    /// мерился бы он, а не АРУ. Сигнал — гармоники с плавающим основным тоном
    /// и слоговой огибающей: чистый тон на речь не похож.
    /// </remarks>
    private static double OutputLevel(bool automaticGainControl, double inputDb)
    {
        using VoiceProcessor processor = new(Rate, automaticGainControl, noiseSuppression: false);

        int frame = processor.FrameSamples;
        float[] near = new float[frame];
        float[] far = new float[frame];
        float[] output = new float[frame];

        int frames = Seconds * 100;
        int measureFrom = (Seconds - 3) * 100;

        // Множитель подобран так, чтобы RMS сигнала был около inputDb.
        double amplitude = Math.Pow(10, inputDb / 20) * 2.2;

        double phase = 0;
        double energy = 0;
        long counted = 0;

        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < frame; i++)
            {
                double t = ((double)f * frame + i) / Rate;
                double pitch = 140 + (30 * Math.Sin(2 * Math.PI * 0.7 * t));
                phase += 2 * Math.PI * pitch / Rate;

                double syllable = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 4 * t));
                double voice = 0;
                for (int harmonic = 1; harmonic <= 6; harmonic++)
                {
                    voice += Math.Sin(harmonic * phase) / harmonic;
                }

                near[i] = (float)(voice * syllable * amplitude);
            }

            processor.AnalyzeReverse(far);
            processor.Process(near, output, delayMilliseconds: 0);

            if (f >= measureFrom)
            {
                foreach (float sample in output)
                {
                    energy += sample * sample;
                }

                counted += frame;
            }
        }

        return 10 * Math.Log10(energy / counted);
    }
}
