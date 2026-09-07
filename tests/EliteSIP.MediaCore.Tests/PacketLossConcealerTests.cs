namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Сокрытие потерь.
///
/// Перенесено из
/// <c>Packages/MediaCore/Tests/MediaCoreTests/PacketLossConcealmentTests.swift</c>.
/// </summary>
public sealed class PacketLossConcealerTests
{
    private const int FrameCount = 160; // 20 мс на 8 кГц

    public static TheoryData<double, int> ТоныИПериоды => new()
    {
        { 100.0, 80 },
        { 160.0, 50 },
        { 200.0, 40 },
        { 250.0, 32 },
    };

    /// <summary>
    /// Кусок «голоса»: пила на заданной частоте — сигнал с ярко выраженным
    /// основным тоном и богатыми обертонами, то есть ровно то, на чём период
    /// обязан находиться уверенно.
    /// </summary>
    private static short[] Voice(
        double frequency,
        int count,
        double sampleRate = 8000,
        double phase = 0,
        double amplitude = 8000)
    {
        short[] samples = new short[count];
        for (int index = 0; index < count; index++)
        {
            double position = (phase + (index * frequency / sampleRate)) % 1;
            samples[index] = (short)(amplitude * ((2 * position) - 1));
        }

        return samples;
    }

    private static double RootMeanSquare(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double energy = 0;
        foreach (short sample in samples)
        {
            energy += (double)sample * sample;
        }

        return Math.Sqrt(energy / samples.Length);
    }

    /// <summary>Наибольший скачок между соседними отсчётами. Щелчок на шве — это именно он.</summary>
    private static int MaximumStep(ReadOnlySpan<short> samples)
    {
        int maximum = 0;
        for (int index = 1; index < samples.Length; index++)
        {
            maximum = Math.Max(maximum, Math.Abs(samples[index] - samples[index - 1]));
        }

        return maximum;
    }

    /// <summary>
    /// Прогоняет через сокрытие несколько настоящих кадров, чтобы кольцо
    /// заполнилось, и возвращает готовый к потере экземпляр.
    /// </summary>
    private static (PacketLossConcealer Concealer, short[] Tail) Primed(double frequency = 200, int frames = 5)
    {
        PacketLossConcealer concealer = new(8000);
        short[] tail = [];
        for (int index = 0; index < frames; index++)
        {
            double phase = index * FrameCount * frequency / 8000;
            tail = concealer.Receive(Voice(frequency, FrameCount, phase: phase));
        }

        return (concealer, tail);
    }

    [Fact]
    public void Настоящий_звук_проходит_насквозь_без_изменений()
    {
        PacketLossConcealer concealer = new(8000);
        short[] block = Voice(200, FrameCount);
        Assert.Equal(block, concealer.Receive(block));
    }

    [Fact]
    public void Потеря_в_самом_начале_разговора_закрывается_тишиной_а_не_выдумкой()
    {
        PacketLossConcealer concealer = new(8000);
        concealer.Receive(Voice(200, FrameCount));

        short[] concealed = concealer.Conceal(FrameCount);
        Assert.Equal(FrameCount, concealed.Length);
        Assert.All(concealed, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Спрятанный_кадр_сохраняет_громкость_голоса()
    {
        (PacketLossConcealer concealer, short[] tail) = Primed();
        short[] concealed = concealer.Conceal(FrameCount);

        double expected = RootMeanSquare(tail);
        double actual = RootMeanSquare(concealed);

        // Первые 10 мс идут в полную силу, дальше начинается затухание, поэтому
        // за кадр целиком уровень законно проседает — но не в разы.
        Assert.True(actual > expected * 0.6, $"синтез слишком тихий: {actual} против {expected}");
        Assert.True(actual < expected * 1.2, $"синтез громче оригинала: {actual} против {expected}");
    }

    [Theory]
    [MemberData(nameof(ТоныИПериоды))]
    public void Основной_тон_находится_точно_а_не_с_ошибкой_в_октаву(double frequency, int expected)
    {
        (PacketLossConcealer concealer, _) = Primed(frequency);
        concealer.Conceal(FrameCount);

        // Кратный период — тоже «похожий» кусок, и корреляция на него ловится
        // легко. Слышно это как голос октавой ниже, поэтому проверяем число, а
        // не похожесть.
        Assert.Equal(expected, concealer.EstimatedPeriod);
    }

    [Fact]
    public void Спрятанный_кадр_продолжает_тот_же_основной_тон()
    {
        // 200 Гц на 8 кГц — период ровно 40 отсчётов. Синтез обязан повторяться
        // с тем же периодом: именно за это платится вся сложность.
        (PacketLossConcealer concealer, _) = Primed(200);
        short[] concealed = concealer.Conceal(FrameCount);

        const int Period = 40;
        double mismatch = 0;
        for (int index = Period; index < Math.Min(concealed.Length, 80); index++)
        {
            mismatch += Math.Abs(concealed[index] - (concealed[index - Period] * 0.99));
        }

        double average = mismatch / (80 - Period);
        Assert.True(average < 400, $"период не выдержан: среднее расхождение {average}");
    }

    [Fact]
    public void На_шве_между_настоящим_звуком_и_синтезом_нет_щелчка()
    {
        (PacketLossConcealer concealer, short[] tail) = Primed();
        short[] concealed = concealer.Conceal(FrameCount);

        // Пила сама по себе даёт один большой скачок за период — это её обрыв,
        // а не щелчок. Поэтому шов сравнивается не с нулём, а с тем, какие
        // скачки в этом сигнале и так есть.
        int natural = MaximumStep(tail);
        int seam = Math.Abs(concealed[0] - tail[^1]);
        Assert.True(seam <= natural, $"на входе в сокрытие скачок {seam} при природных {natural}");
    }

    [Fact]
    public void На_возврате_настоящего_звука_тоже_нет_щелчка()
    {
        const double Frequency = 200;
        (PacketLossConcealer concealer, _) = Primed(Frequency);
        short[] concealed = concealer.Conceal(FrameCount);

        // Настоящий звук продолжается ровно с того места, где его прервали:
        // шестой кадр по счёту.
        short[] real = Voice(Frequency, FrameCount, phase: 6 * FrameCount * Frequency / 8000);
        short[] recovered = concealer.Receive(real);

        int natural = MaximumStep(real);
        int seam = Math.Abs(recovered[0] - concealed[^1]);
        Assert.True(seam <= natural, $"на выходе из сокрытия скачок {seam} при природных {natural}");

        // Сшивка короткая: к середине кадра должен идти уже честный сигнал.
        Assert.Equal(real[^(FrameCount / 2)..], recovered[^(FrameCount / 2)..]);
    }

    [Fact]
    public void Долгая_потеря_уходит_в_тишину_а_не_в_гудок()
    {
        (PacketLossConcealer concealer, short[] tail) = Primed();
        double loud = RootMeanSquare(tail);

        List<double> levels = [];
        for (int index = 0; index < 5; index++)
        {
            levels.Add(RootMeanSquare(concealer.Conceal(FrameCount)));
        }

        Assert.True(levels[0] > loud * 0.6, "первый спрятанный кадр обязан быть слышен");
        for (int index = 1; index < levels.Count; index++)
        {
            Assert.True(levels[index] <= levels[index - 1] + 1, "громкость обязана только падать");
        }

        // 10 мс полной громкости плюс 50 мс затухания — к четвёртому кадру
        // (60 мс) продолжать уже нечего.
        Assert.Equal(0, levels[3]);
    }

    [Fact]
    public void После_настоящего_кадра_затухание_начинается_заново()
    {
        (PacketLossConcealer concealer, _) = Primed();
        concealer.Conceal(FrameCount);
        concealer.Conceal(FrameCount);

        short[] real = Voice(200, FrameCount, phase: 0.25);
        concealer.Receive(real);
        double afterRecovery = RootMeanSquare(concealer.Conceal(FrameCount));

        Assert.True(afterRecovery > RootMeanSquare(real) * 0.6, "серия обязана считаться с нуля");
    }

    [Fact]
    public void Шкала_считается_по_частоте_кодека_а_не_по_числу_отсчётов()
    {
        // Тот же тон на 16 кГц — период вдвое длиннее в отсчётах. Если бы пороги
        // были заданы в отсчётах, а не в миллисекундах, на G.722 основной тон
        // искался бы вне допустимого диапазона.
        PacketLossConcealer concealer = new(AudioCodec.G722);
        for (int index = 0; index < 6; index++)
        {
            concealer.Receive(Voice(200, 320, sampleRate: 16000, phase: index * 320 * 200 / 16000.0));
        }

        short[] concealed = concealer.Conceal(320);
        const int Period = 80; // 200 Гц на 16 кГц
        double mismatch = 0;
        for (int index = Period; index < 160; index++)
        {
            mismatch += Math.Abs(concealed[index] - (concealed[index - Period] * 0.99));
        }

        Assert.True(mismatch / (160 - Period) < 400);
    }

    [Fact]
    public void Сброс_возвращает_сокрытие_в_исходное_состояние()
    {
        (PacketLossConcealer concealer, _) = Primed();
        concealer.Reset();

        Assert.All(concealer.Conceal(FrameCount), sample => Assert.Equal(0, sample));
    }
}
