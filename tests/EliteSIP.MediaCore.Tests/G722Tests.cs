namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Кодек G.722.
///
/// Проверить его сложнее, чем G.711: он с состоянием и с потерями, поэтому
/// «раскодировалось в то же самое» здесь не работает в принципе. Проверяется
/// то, что имеет смысл: скорость потока, отношение сигнал/шум, полоса — и
/// главное, ради чего он взят, — что верхняя половина полосы вообще доезжает.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/G722Tests.swift</c>.
/// </summary>
public sealed class G722Tests
{
    public static TheoryData<double> НижняяПолоса => new(300.0, 1000.0, 2000.0, 3000.0);

    public static TheoryData<double> ВерхняяПолоса => new(4500.0, 5000.0, 6000.0);

    /// <summary>Синус на 16 кГц.</summary>
    private static short[] Tone(double frequency, double seconds = 0.5, double amplitude = 0.4)
    {
        int count = (int)(16000 * seconds);
        short[] samples = new short[count];
        for (int index = 0; index < count; index++)
        {
            samples[index] = (short)(amplitude * 32000 * Math.Sin(2 * Math.PI * frequency * index / 16000));
        }

        return samples;
    }

    private static short[] RoundTrip(short[] samples)
    {
        G722.Encoder encoder = new();
        G722.Decoder decoder = new();
        return decoder.Decode(encoder.Encode(samples));
    }

    /// <summary>Уровень на частоте, в долях полной шкалы. Гёрцель по одной частоте.</summary>
    private static double Level(ReadOnlySpan<short> samples, double frequency)
    {
        double omega = 2 * Math.PI * frequency / 16000;
        double coefficient = 2 * Math.Cos(omega);
        double previous = 0;
        double beforePrevious = 0;
        foreach (short sample in samples)
        {
            double current = (sample / 32768.0) + (coefficient * previous) - beforePrevious;
            beforePrevious = previous;
            previous = current;
        }

        double real = previous - (beforePrevious * Math.Cos(omega));
        double imaginary = beforePrevious * Math.Sin(omega);
        return 2 * Math.Sqrt((real * real) + (imaginary * imaginary)) / samples.Length;
    }

    private static double Deviation(short[] original, short[] restored, double frequency, int skip)
    {
        double expected = Level(original.AsSpan(skip), frequency);
        double actual = Level(restored.AsSpan(skip), frequency);
        return 20 * Math.Log10(actual / expected);
    }

    [Fact]
    public void Байт_на_пару_отсчётов_то_есть_те_же_64_кбит_с()
    {
        G722.Encoder encoder = new();
        byte[] encoded = encoder.Encode(Tone(1000, seconds: 0.02));

        Assert.Equal(160, encoded.Length);
        Assert.Equal(160, AudioCodec.G722.ByteCount(20));
        Assert.Equal(320, AudioCodec.G722.SampleCount(20));
    }

    [Fact]
    public void Метка_времени_растёт_вдвое_медленнее_числа_отсчётов()
    {
        // Ошибка RFC 1890, оставленная в RFC 3551 §4.5.2: у G.722 частота часов
        // объявлена 8000 при выборке 16 000. Кто нарастит метку на 320, получит
        // от собеседника либо ускоренную речь, либо тишину.
        Assert.Equal(160u, AudioCodec.G722.TimestampIncrement(20));
        Assert.Equal(8000u, AudioCodec.G722.RtpClockRate());
        Assert.Equal(16000u, AudioCodec.G722.SampleRate());
        Assert.True(AudioCodec.G722.IsWideband());
    }

    [Theory]
    [MemberData(nameof(НижняяПолоса))]
    public void Тон_восстанавливается_на_своей_частоте_и_своём_уровне(double frequency)
    {
        // Начало отбрасывается: квадратурный фильтр и предсказатель на первых
        // отсчётах ещё не установились.
        short[] original = Tone(frequency);
        double deviation = Deviation(original, RoundTrip(original), frequency, 1000);

        Assert.True(Math.Abs(deviation) < 1.5, $"на {frequency} Гц уровень уехал на {deviation:F1} дБ");
    }

    [Theory]
    [MemberData(nameof(ВерхняяПолоса))]
    public void Верхняя_половина_полосы_доезжает_ради_неё_кодек_и_взят(double frequency)
    {
        // Именно это G.711 не умеет физически: у него потолок 4 кГц. Если
        // верхняя полоса собирается неправильно, тест поймает и это — тон либо
        // пропадёт, либо вылезет не на своей частоте.
        short[] original = Tone(frequency);
        double deviation = Deviation(original, RoundTrip(original), frequency, 1000);

        Assert.True(Math.Abs(deviation) < 3, $"на {frequency} Гц уровень уехал на {deviation:F1} дБ");
    }

    [Fact]
    public void Отношение_сигнал_шум_не_хуже_G711()
    {
        short[] original = Tone(1000, seconds: 1.0);
        short[] restored = RoundTrip(original);

        // Первые отсчёты пропускаем и сравниваем с задержкой фильтра: у
        // квадратурного банка она есть, и без выравнивания «шумом» окажется
        // сдвиг, а не шум.
        const int Offset = 22;
        double signal = 0;
        double noise = 0;
        for (int index = 2000; index < original.Length - Offset; index++)
        {
            double clean = original[index];
            double error = restored[index + Offset] - clean;
            signal += clean * clean;
            noise += error * error;
        }

        double snr = 10 * Math.Log10(signal / Math.Max(noise, 1e-12));
        Assert.True(snr > 20, $"отношение сигнал/шум {snr:F1} дБ");
    }

    [Fact]
    public void Тишина_остаётся_тишиной()
    {
        short[] restored = RoundTrip(new short[8000]);
        int peak = restored.Max(sample => Math.Abs((int)sample));
        Assert.True(peak < 64, $"на тишине вылезло {peak}");
    }

    [Fact]
    public void Состояние_переживает_нарезку_на_кадры()
    {
        // В разговоре кодек вызывается кадрами по 20 мс, а не одним куском.
        // Если состояние где-то теряется, на каждой границе будет щелчок.
        short[] original = Tone(1000, seconds: 0.5);
        short[] whole = RoundTrip(original);

        G722.Encoder encoder = new();
        G722.Decoder decoder = new();
        List<short> framed = [];
        for (int start = 0; start < original.Length; start += 320)
        {
            int length = Math.Min(320, original.Length - start);
            framed.AddRange(decoder.Decode(encoder.Encode(original.AsSpan(start, length))));
        }

        Assert.Equal(whole, framed);
    }

    [Fact]
    public void Кодер_и_декодер_разговора_выбирают_G722_по_кодеку()
    {
        AudioFrameEncoder encoder = new(AudioCodec.G722);
        AudioFrameDecoder decoder = new(AudioCodec.G722);

        byte[] payload = encoder.Encode(Tone(1000, seconds: 0.02));
        Assert.Equal(160, payload.Length);
        Assert.Equal(320, decoder.Decode(payload).Length);

        // А G.711 остаётся один байт на отсчёт.
        AudioFrameEncoder narrow = new(AudioCodec.Pcmu);
        Assert.Equal(160, narrow.Encode(new short[160]).Length);
    }

    [Fact]
    public void Молчащий_кадр_имеет_правильную_длину_в_обоих_кодеках()
    {
        Assert.Equal(160, AudioCodec.G722.SilencePayload(20).Length);
        Assert.Equal(160, AudioCodec.Pcmu.SilencePayload(20).Length);
        Assert.Equal(160, AudioCodec.Pcma.SilencePayload(20).Length);
    }

    [Fact]
    public void Полоса_восстанавливается_ровно_по_всей_ширине()
    {
        // Этот тест существует ради одной конкретной ошибки: если в
        // квадратурном фильтре перепутать развязку чётных и нечётных отводов,
        // кодек продолжает работать. Ниже 1 кГц потерь почти нет, речь
        // разборчива, ничего не падает — но на 3 и 5 кГц появляется провал в
        // 13,8 дБ, и голос звучит глухо. Поймать это на слух, не зная, что
        // искать, практически невозможно.
        double[] frequencies = [300, 1000, 2000, 3000, 3800, 4200, 5000, 6000, 7000];
        foreach (double frequency in frequencies)
        {
            short[] original = Tone(frequency, seconds: 0.5);
            double deviation = Deviation(original, RoundTrip(original), frequency, 2000);
            Assert.True(Math.Abs(deviation) < 3.5, $"на {frequency} Гц отклонение {deviation:F1} дБ");
        }
    }
}
