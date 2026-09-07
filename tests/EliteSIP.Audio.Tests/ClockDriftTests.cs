using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Оценка ухода часов по наклону прямой.
///
/// <b>Что здесь проверяется, а что нет.</b> Проверяется арифметика: наклон,
/// вытащенный из зашумлённых проб, и честный отказ отвечать, когда мерили мало.
/// Не проверяется физика — годится ли квантованное показание часов устройства
/// как источник проб. Это живой замер на живом железе, он в приёмке этапа, и
/// W0 прямо оставил его сюда: «Числу пока верить нельзя».
/// </summary>
public sealed class ClockDriftEstimatorTests
{
    [Fact]
    public void Наклон_вытаскивается_из_шума()
    {
        // Шум ±10 мс — это квантование показаний часов периодом устройства.
        // Оно на порядок больше самой измеряемой величины: за 300 с при 57 ppm
        // разность вырастает всего на 17 мс. Наклон по сотням проб обязан
        // выбираться из-под такого шума — на этом держится весь метод.
        const double TruePpm = 57.0;

        // Свой генератор с постоянным зерном: прогон обязан быть повторимым,
        // иначе падение теста нечем воспроизвести.
        Random random = new(20260907);
        ClockDriftEstimator estimator = new();

        for (int i = 0; i < 3000; i++)
        {
            double time = i * 0.1;
            double exact = TruePpm * 1e-6 * time;
            double noise = (random.NextDouble() - 0.5) * 0.020;
            estimator.Add(time, exact + noise);
        }

        Assert.True(
            Math.Abs(estimator.Ppm - TruePpm) < 5,
            $"наклон {estimator.Ppm:F1} ± {estimator.StandardError:F1} ppm вместо {TruePpm}");
        Assert.True(estimator.IsTrustworthy, estimator.Summary);
    }

    [Fact]
    public void Короткое_окно_объявляется_ненадёжным_даже_когда_числа_красивые()
    {
        // Главная проверка файла. Замер W0: та же гарнитура дала 57 ± 2 ppm на
        // 297 секундах и 212 ± 22 на 57. Погрешность честно выросла, но обе
        // оценки выглядели как числа с ошибкой, и различить их было нечем.
        // Здесь короткое окно отвергается по самому окну, а не по красоте
        // чисел: данные ниже идеально ложатся на прямую.
        ClockDriftEstimator estimator = new();
        for (int i = 0; i < 100; i++)
        {
            double time = i * 0.1; // всего 10 с
            estimator.Add(time, 57e-6 * time);
        }

        Assert.True(Math.Abs(estimator.Ppm - 57) < 0.1, "арифметика верна");
        Assert.False(estimator.IsTrustworthy, estimator.Summary);
        Assert.Contains("верить рано", estimator.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Проб_мало_даже_на_длинном_окне()
    {
        // Прямая по трём точкам — это не замер, сколько бы времени между ними
        // ни прошло.
        ClockDriftEstimator estimator = new();
        estimator.Add(0, 0);
        estimator.Add(200, 0.0114);
        estimator.Add(400, 0.0228);

        Assert.False(estimator.IsTrustworthy, estimator.Summary);
    }

    [Fact]
    public void Отсутствие_ухода_тоже_ответ()
    {
        // Ноль с малой погрешностью — это «расхождения нет», а не «мерили
        // мало». Путать эти два случая нельзя: в первом компенсация не нужна, а
        // во втором вывод делать рано.
        ClockDriftEstimator estimator = new();
        for (int i = 0; i < 300; i++)
        {
            estimator.Add(i, 0);
        }

        Assert.True(Math.Abs(estimator.Ppm) < 1);
        Assert.True(estimator.IsTrustworthy, estimator.Summary);
    }

    [Fact]
    public void Проб_меньше_трёх_не_дают_числа()
    {
        ClockDriftEstimator estimator = new();
        estimator.Add(0, 0);
        estimator.Add(1, 0.001);

        Assert.Equal(0, estimator.Ppm);
        Assert.False(estimator.IsTrustworthy);
        Assert.Contains("проб мало", estimator.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Старые_пробы_вытесняются()
    {
        // Восьмичасовая смена при пробе в секунду — почти тридцать тысяч точек.
        // Держать их все незачем, и память тракта не должна расти с длиной
        // смены.
        ClockDriftEstimator estimator = new(capacity: 100);
        for (int i = 0; i < 1000; i++)
        {
            estimator.Add(i, 0);
        }

        Assert.Equal(100, estimator.Count);
    }
}

/// <summary>
/// Компенсация расхождения — по заполнению кольца, а не по замеренному уходу.
///
/// Проверяется на модели: производитель и потребитель с заведомо разными
/// часами. Настоящая приёмка — восемь часов разговора на живом железе, и она в
/// приёмке этапа; здесь закрывается то, что моделью проверяется честно и за
/// миллисекунды. Именно эта модель и вскрыла предельный цикл у первого
/// варианта регулятора — см. описание <see cref="PlaybackRateController"/>.
/// </summary>
public sealed class PlaybackRateControllerTests
{
    private const int SampleRate = 48000;
    private const int TargetFill = 960;   // 20 мс
    private const int ChunkSamples = 960; // кадр 20 мс
    private const double Tick = ChunkSamples / (double)SampleRate;

    [Fact]
    public void Ровное_кольцо_не_трогается()
    {
        // Поправка обязана стоять на месте, пока чинить нечего: любое её
        // движение слышно как «плавающий» тон.
        PlaybackRateController controller = new(TargetFill, SampleRate);

        for (int i = 0; i < 10_000; i++)
        {
            controller.Observe(TargetFill, Tick);
        }

        Assert.Equal(1.0, controller.Correction, 12);
    }

    [Fact]
    public void Дрожание_кольца_почти_не_доходит_до_темпа()
    {
        // Заполнение дрожит на десятки отсчётов от каждого неровного
        // пробуждения. Сглаживание обязано не пустить это дрожание в темп:
        // иначе тон разговора «плавает» в такт планировщику.
        PlaybackRateController controller = new(TargetFill, SampleRate);

        for (int i = 0; i < 10_000; i++)
        {
            controller.Observe(TargetFill + (i % 2 == 0 ? 200 : -200), Tick);
        }

        Assert.True(
            Math.Abs(controller.CorrectionPpm) < 20,
            $"дрожание протекло в темп: {controller.CorrectionPpm:F1} ppm");
    }

    [Fact]
    public void Переполненное_кольцо_замедляет_темп()
    {
        PlaybackRateController controller = new(TargetFill, SampleRate);

        for (int i = 0; i < 1000; i++)
        {
            controller.Observe(TargetFill + 5000, Tick);
        }

        Assert.True(controller.Correction < 1.0, $"поправка {controller.CorrectionPpm:F1} ppm");
    }

    [Fact]
    public void Пустеющее_кольцо_ускоряет_темп()
    {
        PlaybackRateController controller = new(TargetFill, SampleRate);

        for (int i = 0; i < 1000; i++)
        {
            controller.Observe(0, Tick);
        }

        Assert.True(controller.Correction > 1.0, $"поправка {controller.CorrectionPpm:F1} ppm");
    }

    [Fact]
    public void Поправка_упирается_в_предел_и_не_копит_сверх_него()
    {
        // Предохранитель: если кольцо пустеет не из-за часов, а из-за молчащего
        // устройства, поправка обязана упереться, а не превратить разговор в
        // писк. И интеграл при этом не должен расти — иначе после возвращения
        // устройства регулятор выходил бы из упора минутами.
        PlaybackRateController controller = new(TargetFill, SampleRate);

        for (int i = 0; i < 100_000; i++)
        {
            controller.Observe(0, Tick);
        }

        Assert.True(controller.IsSaturated, controller.Summary);
        Assert.Equal(1.0 + PlaybackRateController.MaximumCorrection, controller.Correction, 12);

        // Устройство вернулось: кольцо на месте. Поправка обязана сойти с упора
        // за считанные пробы, а не за минуты.
        for (int i = 0; i < 200; i++)
        {
            controller.Observe(TargetFill, Tick);
        }

        Assert.False(controller.IsSaturated, controller.Summary);
    }

    [Fact]
    public void Кольцо_не_уползает_при_настоящем_расхождении_часов()
    {
        // Модель разговора: устройство забирает отсчёты на 100 ppm медленнее,
        // чем мы их производим. Без компенсации кольцо растёт линейно — за час
        // это 17 280 отсчётов, то есть 360 мс лишней задержки, разговор с эхом
        // собственного голоса.
        const double DeviceSlowerPpm = 100;

        PlaybackRateController controller = new(TargetFill, SampleRate);
        double fill = TargetFill;
        double maximumFill = fill;
        double minimumFill = fill;

        // Час разговора: 180 000 кадров по 20 мс.
        for (int i = 0; i < 180_000; i++)
        {
            fill += ChunkSamples * controller.Correction;
            fill -= ChunkSamples * (1.0 - (DeviceSlowerPpm * 1e-6));
            controller.Observe((int)fill, Tick);

            // Первые сто секунд — выход контура на режим, в оценку не идут.
            if (i > 5_000)
            {
                maximumFill = Math.Max(maximumFill, fill);
                minimumFill = Math.Min(minimumFill, fill);
            }
        }

        Assert.True(
            maximumFill - minimumFill < 100,
            $"кольцо гуляет от {minimumFill:F0} до {maximumFill:F0} отсчётов");

        // И, в отличие от варианта с мёртвой зоной, поправка обязана сойтись к
        // самому расхождению, а не гулять вокруг него.
        Assert.True(
            Math.Abs(controller.CorrectionPpm + DeviceSlowerPpm) < 5,
            $"поправка {controller.CorrectionPpm:F1} ppm при расхождении {DeviceSlowerPpm} ppm");
        Assert.False(controller.IsSaturated, controller.Summary);
    }

    [Fact]
    public void Кольцо_возвращается_к_цели_а_не_замирает_где_попало()
    {
        // Разница между этим регулятором и вариантом с мёртвой зоной. Тот
        // успокаивался на границе зоны, то есть с постоянной лишней задержкой;
        // этот обязан вернуть запас к цели.
        PlaybackRateController controller = new(TargetFill, SampleRate);
        double fill = TargetFill + 2000;

        for (int i = 0; i < 180_000; i++)
        {
            fill += ChunkSamples * controller.Correction;
            fill -= ChunkSamples * (1.0 - 50e-6);
            controller.Observe((int)fill, Tick);
        }

        Assert.True(
            Math.Abs(fill - TargetFill) < 50,
            $"кольцо успокоилось на {fill:F0} вместо {TargetFill}");
    }

    [Fact]
    public void Сброс_забывает_накопленное()
    {
        PlaybackRateController controller = new(TargetFill, SampleRate);
        for (int i = 0; i < 1000; i++)
        {
            controller.Observe(0, Tick);
        }

        controller.Reset();
        Assert.Equal(1.0, controller.Correction);
        Assert.Equal(0, controller.SmoothedFill);
    }
}
