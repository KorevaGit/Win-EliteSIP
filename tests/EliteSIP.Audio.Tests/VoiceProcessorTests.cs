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

    /// <summary>
    /// Шумодав работает, и его уровень доходит до библиотеки.
    /// </summary>
    ///
    /// <remarks>
    /// Проверка второй половины не лишняя: у регуляторов усиления та же
    /// обёртка молча выбрасывала все числа, кроме «включено». Здесь уровень
    /// переключается вместе с эхоподавлением (наушники — умеренный, динамики —
    /// сильный), и если бы он не доходил, умеренный и сильный совпали бы.
    /// </remarks>
    [Fact]
    public void Шумодав_убирает_ровный_шум_и_сильный_сильнее_умеренного()
    {
        double off = NoiseLevel(noiseSuppression: false, echoCancellation: true);
        double strong = NoiseLevel(noiseSuppression: true, echoCancellation: true);
        double gentle = NoiseLevel(noiseSuppression: true, echoCancellation: false);

        Assert.True(off - strong >= 10, $"шумодав не работает: без него {off:F1} дБ, с ним {strong:F1} дБ");
        Assert.True(
            gentle - strong >= 1,
            $"уровень шумодава не доходит: умеренный {gentle:F1} дБ, сильный {strong:F1} дБ");
    }

    /// <summary>
    /// Обработка идёт на каждой частоте, на которую тракт сводит устройства:
    /// 8 кГц — Bluetooth в режиме гарнитуры, 16 — большинство USB-гарнитур,
    /// 32, 48 — всё остальное (44,1 и 96 пересчитываются в 48).
    /// </summary>
    ///
    /// <remarks>
    /// Жалоба 2 октября 2026: «на USB-гарнитуре голоса вокруг слышно очень
    /// отчётливо» — первым делом надо было исключить, что обработка на
    /// какой-то частоте не идёт вовсе. До того коды возврата APM не
    /// проверялись нигде, и отказ библиотеки был бы неотличим от работы.
    /// </remarks>
    [Theory]
    [InlineData(8_000, true)]
    [InlineData(8_000, false)]
    [InlineData(16_000, true)]
    [InlineData(16_000, false)]
    [InlineData(32_000, true)]
    [InlineData(48_000, true)]
    [InlineData(48_000, false)]
    public void Шумодав_работает_на_каждой_частоте_устройств(int rate, bool echoCancellation)
    {
        double off = NoiseLevel(noiseSuppression: false, echoCancellation, rate);
        double on = NoiseLevel(noiseSuppression: true, echoCancellation, rate, out VoiceProcessor processor);

        Assert.True(off - on >= 10, $"{rate} Гц: шумодав не работает — без него {off:F1} дБ, с ним {on:F1} дБ");
        Assert.Equal(0, processor.FailedFrames);
        Assert.DoesNotContain("ОШИБКА", processor.Summary);
    }

    /// <summary>
    /// Голос соседа на 26 дБ тише оператора — как коллега в полутора метрах
    /// от штанги гарнитуры — в паузах оператора уходит вниз.
    /// </summary>
    ///
    /// <remarks>
    /// Первая половина проверки — та самая жалоба: один шумодав чужой голос
    /// почти не трогает, потому что для него это речь, а не шум. Вторая — что
    /// голос самого оператора не страдает.
    /// </remarks>
    [Theory]
    [InlineData(16_000)]
    [InlineData(48_000)]
    public void Голоса_вокруг_приглушаются_а_голос_оператора_нет(int rate)
    {
        (double operatorOff, double neighbourOff) = OperatorAndNeighbour(rate, background: false);
        (double operatorOn, double neighbourOn) = OperatorAndNeighbour(rate, background: true);

        Assert.True(
            neighbourOff - neighbourOn >= 18,
            $"{rate} Гц: сосед без блока {neighbourOff:F1} дБ, с блоком {neighbourOn:F1} дБ");
        Assert.True(
            Math.Abs(operatorOn - operatorOff) <= 1.5,
            $"{rate} Гц: голос оператора без блока {operatorOff:F1} дБ, с блоком {operatorOn:F1} дБ");
    }

    /// <summary>
    /// Пока голос оператора не услышан, блок не делает ничего: иначе первый
    /// же громкий сосед в начале звонка стал бы «оператором», а тихий оператор
    /// — чужим.
    /// </summary>
    [Fact]
    public void Без_голоса_оператора_ничего_не_приглушается()
    {
        BackgroundVoiceGate gate = new();
        float[] frame = new float[Rate / 100];
        double phase = 0;
        double energyIn = 0;
        double energyOut = 0;

        for (int f = 0; f < 300; f++)
        {
            phase = Speech(frame, f, Rate, -50, pitch: 210, phase);
            foreach (float sample in frame)
            {
                energyIn += sample * sample;
            }

            gate.Process(frame);
            foreach (float sample in frame)
            {
                energyOut += sample * sample;
            }
        }

        Assert.InRange(10 * Math.Log10(energyOut / energyIn), -1, 0.1);
    }

    /// <summary>
    /// Щелчок по столу не закрывает оператора: уровень его голоса вверх
    /// ползёт ограниченно.
    /// </summary>
    [Fact]
    public void Щелчок_не_закрывает_голос_оператора()
    {
        BackgroundVoiceGate gate = new();
        float[] frame = new float[Rate / 100];
        double phase = 0;

        for (int f = 0; f < 200; f++)
        {
            phase = Speech(frame, f, Rate, -26, pitch: 130, phase);
            gate.Process(frame);
        }

        double before = gate.VoiceDb!.Value;

        Array.Fill(frame, 0.9f);
        gate.Process(frame);
        gate.Process(frame);

        Assert.True(gate.VoiceDb!.Value - before <= 1.01, $"щелчок поднял голос оператора с {before:F1} до {gate.VoiceDb:F1} дБ");
    }

    /// <summary>
    /// Оператор говорит полторы секунды, полторы молчит; в его паузах говорит
    /// сосед. Возвращает уровень оператора в его репликах и соседа в паузах,
    /// после первых четырёх секунд — когда всё улеглось.
    /// </summary>
    private static (double Operator, double Neighbour) OperatorAndNeighbour(int rate, bool background)
    {
        using VoiceProcessor processor = new(
            rate,
            automaticGainControl: false,
            noiseSuppression: true,
            echoCancellation: false,
            backgroundVoiceSuppression: background);

        int size = processor.FrameSamples;
        float[] near = new float[size];
        float[] neighbour = new float[size];
        float[] far = new float[size];
        float[] output = new float[size];
        Random random = new(5);

        double operatorPhase = 0;
        double neighbourPhase = 0;
        double operatorEnergy = 0;
        double neighbourEnergy = 0;
        long operatorCount = 0;
        long neighbourCount = 0;

        for (int f = 0; f < 2000; f++)
        {
            // Реплика — 150 кадров, пауза — 150. Сосед говорит в паузах.
            bool talking = f % 300 < 150;
            operatorPhase = Speech(near, f, rate, talking ? -24 : -200, pitch: 125, operatorPhase);
            neighbourPhase = Speech(neighbour, f, rate, talking ? -200 : -50, pitch: 205, neighbourPhase);

            for (int i = 0; i < size; i++)
            {
                // Ровный фон комнаты на −62 дБ: шумодаву есть что делать.
                near[i] += neighbour[i] + (float)((random.NextDouble() - 0.5) * 0.0028);
            }

            processor.AnalyzeReverse(far);
            processor.Process(near, output, delayMilliseconds: 0);

            // Края реплик не мерятся: там удержание блока и разгон шумодава.
            int phaseInCycle = f % 150;
            if (f < 400 || phaseInCycle < 40 || phaseInCycle > 140)
            {
                continue;
            }

            double energy = 0;
            foreach (float sample in output)
            {
                energy += sample * sample;
            }

            if (talking)
            {
                operatorEnergy += energy;
                operatorCount += size;
            }
            else
            {
                neighbourEnergy += energy;
                neighbourCount += size;
            }
        }

        return (10 * Math.Log10(operatorEnergy / operatorCount), 10 * Math.Log10(neighbourEnergy / neighbourCount));
    }

    /// <summary>
    /// Кадр голосоподобного сигнала: гармоники с плавающим тоном и слоговой
    /// огибающей, RMS около <paramref name="levelDb"/>. Возвращает фазу для
    /// следующего кадра.
    /// </summary>
    private static double Speech(float[] frame, int index, int rate, double levelDb, double pitch, double phase)
    {
        double amplitude = Math.Pow(10, levelDb / 20) * 2.2;
        for (int i = 0; i < frame.Length; i++)
        {
            double t = ((double)index * frame.Length + i) / rate;
            double tone = pitch + (0.2 * pitch * Math.Sin(2 * Math.PI * 0.7 * t));
            phase += 2 * Math.PI * tone / rate;

            double syllable = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 4 * t));
            double voice = 0;
            for (int harmonic = 1; harmonic <= 6; harmonic++)
            {
                voice += Math.Sin(harmonic * phase) / harmonic;
            }

            frame[i] = (float)(voice * syllable * amplitude);
        }

        return phase;
    }

    private static double NoiseLevel(bool noiseSuppression, bool echoCancellation, int rate = Rate)
        => NoiseLevel(noiseSuppression, echoCancellation, rate, out VoiceProcessor _);

    private static double NoiseLevel(bool noiseSuppression, bool echoCancellation, int rate, out VoiceProcessor used)
    {
        using VoiceProcessor processor = new(rate, automaticGainControl: false, noiseSuppression, echoCancellation);
        used = processor;

        float[] near = new float[processor.FrameSamples];
        float[] far = new float[processor.FrameSamples];
        float[] output = new float[processor.FrameSamples];
        Random random = new(11);

        double energy = 0;
        long counted = 0;
        for (int f = 0; f < 600; f++)
        {
            for (int i = 0; i < near.Length; i++)
            {
                near[i] = (float)((random.NextDouble() - 0.5) * 0.02);
            }

            processor.AnalyzeReverse(far);
            processor.Process(near, output, delayMilliseconds: 0);

            if (f >= 400)
            {
                foreach (float sample in output)
                {
                    energy += sample * sample;
                }

                counted += output.Length;
            }
        }

        return 10 * Math.Log10(energy / counted);
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
