namespace EliteSIP.Audio.Tests;

/// <summary>
/// Громкость собеседника (2 октября 2026): ползунок до 200 % и выравнивание
/// приёма тем же регулятором, что у микрофона, но с осторожными пределами.
/// </summary>
public sealed class ReceiveLevelingTests
{
    private const int Rate = 8_000;
    private const int Chunk = Rate / 100;

    [Theory]
    [InlineData(1.5f, 1.5f)]
    [InlineData(5f, VoiceAudioConfiguration.PlaybackVolumeLimit)]
    [InlineData(-1f, 0f)]
    public void Громкость_собеседника_до_двухсот_процентов(float asked, float expected)
        => Assert.Equal(expected, new VoiceAudioConfiguration { PlaybackVolume = asked }.PlaybackVolume);

    [Fact]
    public void Выравнивание_по_умолчанию_выключено()
        => Assert.False(new VoiceAudioConfiguration().ReceiveGainControl);

    [Fact]
    public void Тихая_линия_поднимается_не_больше_чем_на_десять_децибел()
    {
        // Голос на −40 дБ: до цели −20 не хватает двадцати, а поднять можно
        // только десять — вместе с голосом поднимается шум линии.
        double level = Leveled(inputDb: -40, out double gain);

        Assert.InRange(gain, WasapiVoiceAudioEngine.ReceiveMaximumBoostDb - 0.5, WasapiVoiceAudioEngine.ReceiveMaximumBoostDb);
        Assert.InRange(level, -32, -28);
    }

    [Fact]
    public void Громкая_линия_убавляется_не_больше_чем_на_шесть()
    {
        Leveled(inputDb: -6, out double gain);

        Assert.InRange(gain, -WasapiVoiceAudioEngine.ReceiveMaximumCutDb, -WasapiVoiceAudioEngine.ReceiveMaximumCutDb + 0.5);
    }

    [Fact]
    public void Шум_линии_в_паузе_не_вытягивается()
    {
        SpeechGainControl control = new(
            WasapiVoiceAudioEngine.ReceiveTargetDb,
            WasapiVoiceAudioEngine.ReceiveMaximumBoostDb,
            WasapiVoiceAudioEngine.ReceiveMaximumCutDb);
        float[] frame = new float[Chunk];
        Random random = new(3);

        for (int f = 0; f < 1000; f++)
        {
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = (float)((random.NextDouble() - 0.5) * 0.002);
            }

            control.Process(frame);
        }

        Assert.Equal(0, control.GainDb, precision: 1);
    }

    [Fact]
    public void Двести_процентов_на_полной_шкале_не_уходят_за_шкалу()
    {
        // Ограничитель на выводе — та же функция, что после громкости в тракте.
        float loudest = SpeechGainControl.Limit(1f * VoiceAudioConfiguration.PlaybackVolumeLimit);

        Assert.InRange(loudest, 0.9f, 1f);
    }

    /// <summary>Десять секунд голосоподобного сигнала; уровень за последние три.</summary>
    private static double Leveled(double inputDb, out double gain)
    {
        SpeechGainControl control = new(
            WasapiVoiceAudioEngine.ReceiveTargetDb,
            WasapiVoiceAudioEngine.ReceiveMaximumBoostDb,
            WasapiVoiceAudioEngine.ReceiveMaximumCutDb);
        float[] frame = new float[Chunk];
        double amplitude = Math.Pow(10, inputDb / 20) * 2.2;
        double phase = 0;
        double energy = 0;
        long counted = 0;

        for (int f = 0; f < 1000; f++)
        {
            for (int i = 0; i < frame.Length; i++)
            {
                double t = ((double)f * Chunk + i) / Rate;
                phase += 2 * Math.PI * (140 + (30 * Math.Sin(2 * Math.PI * 0.7 * t))) / Rate;
                double voice = 0;
                for (int harmonic = 1; harmonic <= 6; harmonic++)
                {
                    voice += Math.Sin(harmonic * phase) / harmonic;
                }

                frame[i] = (float)(voice * (0.5 + (0.5 * Math.Sin(2 * Math.PI * 4 * t))) * amplitude);
            }

            control.Process(frame);

            if (f >= 700)
            {
                foreach (float sample in frame)
                {
                    energy += sample * sample;
                }

                counted += frame.Length;
            }
        }

        gain = control.GainDb;
        return 10 * Math.Log10(energy / counted);
    }
}
