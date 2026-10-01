namespace EliteSIP.Audio;

/// <summary>
/// Держит заполнение кольца воспроизведения на месте, подправляя темп
/// пересчёта.
///
/// <b>Почему компенсация не по измеренному уходу часов.</b> Число из
/// <see cref="ClockDriftEstimator"/> честное, но приходит поздно: чтобы ему
/// верить, нужны десятки секунд наблюдения, а к тому времени кольцо уже уехало.
/// Хуже того, оно отвечает не на весь вопрос: кольцо уползает не только от
/// разницы кварцев, но и от того, что сеть отдаёт кадры не ровно по двадцать
/// миллисекунд, и от округлений на каждом такте.
///
/// Поэтому чинится не причина, а следствие: <b>заполнение кольца</b> — та самая
/// величина, которая портит разговор, и её видно сразу.
///
/// <b>Мёртвой зоны здесь нет, и это исправление, а не упущение.</b> Первый
/// вариант трогал темп только за пределами зоны в четверть запаса — чтобы не
/// гоняться за дрожанием кольца. Проверка на модели показала, что так регулятор
/// не сходится вовсе: он входит в предельный цикл вдоль границы зоны. Кольцо
/// при этом действительно стояло на месте (разброс 494 отсчёта против 17 280,
/// которые дал бы некомпенсированный уход), но <b>поправка непрерывно
/// гуляла</b>: на модели со 100 ppm расхождения прогон заканчивался на −0,2 ppm
/// вместо −100. То есть темп пересчёта колебался весь разговор, хотя чинить
/// надо было постоянную величину.
///
/// Дрожание убирается тем, чем и полагается, — сглаживанием измерения, а
/// пропорционально-интегральный закон без зоны сходится к нулевой ошибке.
///
/// <b>Три вещи, без которых это не работает.</b>
///
/// <list type="number">
/// <item><b>Сглаживание заполнения.</b> Кольцо дрожит на десятки отсчётов от
/// каждого неровного пробуждения; без сглаживания это дрожание уезжало бы прямо
/// в темп пересчёта.</item>
/// <item><b>Медленный контур.</b> Уход часов — величина постоянная, торопиться
/// её выбирать некуда, а резкая смена темпа слышна. Контур настроен на
/// критическое затухание с постоянной в двадцать секунд.</item>
/// <item><b>Жёсткий предел с защитой от накопления.</b> Кварцы расходятся на
/// десятки, редко сотни миллионных долей; предел стоит на порядок выше — не
/// чтобы хватило, а чтобы <b>ошибка в другом месте не уехала через регулятор в
/// звук</b>. Если кольцо пустеет из-за молчащего устройства, поправка упрётся в
/// предел, интеграл перестанет расти, и разговор не превратится в писк.</item>
/// </list>
/// </summary>
public sealed class PlaybackRateController
{
    /// <summary>
    /// Предел поправки — половина процента, то есть 5000 ppm.
    ///
    /// На порядок с лишним выше любого настоящего расхождения кварцев. Это не
    /// запас на будущее, а предохранитель: см. описание типа.
    /// </summary>
    public const double MaximumCorrection = 0.005;

    private int _targetFill;
    private readonly double _smoothingSeconds;
    private readonly double _proportional;
    private readonly double _integral;

    private double _smoothedFill;
    private double _accumulated;
    private bool _started;

    /// <param name="targetFill">Целевой запас кольца в отсчётах.</param>
    /// <param name="sampleRate">Частота, в отсчётах которой считается запас.</param>
    /// <param name="loopSeconds">
    /// Постоянная времени контура. Двадцать секунд — компромисс: быстрее значит
    /// пускать дрожание кольца в темп, медленнее — дать кольцу уехать на
    /// заметную задержку, прежде чем регулятор её заметит.
    /// </param>
    /// <param name="smoothingSeconds">
    /// Постоянная сглаживания заполнения. На порядок быстрее контура, чтобы
    /// одно не мешало другому.
    /// </param>
    public PlaybackRateController(
        int targetFill,
        int sampleRate,
        double loopSeconds = 20,
        double smoothingSeconds = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetFill);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(loopSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(smoothingSeconds);

        _targetFill = targetFill;
        _smoothingSeconds = smoothingSeconds;

        // Контур второго порядка с критическим затуханием. Объект управления —
        // интегратор (заполнение накапливает разницу темпов), поэтому
        // собственная частота и затухание задаются прямо, а не подбором.
        double frequency = 1.0 / loopSeconds;
        _proportional = 2 * frequency / sampleRate;
        _integral = frequency * frequency / sampleRate;
    }

    /// <summary>
    /// Множитель к темпу пересчёта. Единица — без поправки.
    ///
    /// Меньше единицы означает «производить медленнее»: кольцо переполнено.
    /// </summary>
    public double Correction { get; private set; } = 1.0;

    /// <summary>Поправка в миллионных долях — для журнала и сверки с замеренным уходом часов.</summary>
    public double CorrectionPpm => (Correction - 1.0) * 1_000_000.0;

    /// <summary>Упёрлась ли поправка в предел. Признак того, что дело не в часах.</summary>
    public bool IsSaturated => Math.Abs(Correction - 1.0) >= MaximumCorrection - 1e-12;

    /// <summary>Целевой запас кольца.</summary>
    public int TargetFill => _targetFill;

    /// <summary>
    /// Меняет цель на ходу.
    ///
    /// Цель — не константа тракта, а глубина джиттер-буфера, и он сам её
    /// подстраивает под сеть. Регулятор при этом не сбрасывается: интеграл —
    /// это уход часов, и от смены цели он не меняется.
    /// </summary>
    public void Retarget(int targetFill)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetFill);
        _targetFill = targetFill;
    }

    /// <summary>Сглаженное заполнение — то, по чему регулятор на самом деле судит.</summary>
    public double SmoothedFill => _smoothedFill;

    /// <summary>Сообщает заполнение кольца и получает обновлённую поправку.</summary>
    /// <param name="fillSamples">Сколько отсчётов лежит в кольце сейчас.</param>
    /// <param name="elapsedSeconds">Сколько прошло с прошлой пробы.</param>
    public double Observe(int fillSamples, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0)
        {
            return Correction;
        }

        if (!_started)
        {
            // Первая проба задаёт начало сглаживания. Начинать с нуля значило
            // бы объявить кольцо пустым и рвануть темп вверх на старте
            // разговора.
            _smoothedFill = fillSamples;
            _started = true;
        }
        else
        {
            double alpha = 1 - Math.Exp(-elapsedSeconds / _smoothingSeconds);
            _smoothedFill += (fillSamples - _smoothedFill) * alpha;
        }

        double error = _smoothedFill - _targetFill;
        double candidate = _accumulated + (error * elapsedSeconds);

        // Знак минус: кольцо переполнено — производим медленнее.
        double correction = 1.0 - ((_proportional * error) + (_integral * candidate));

        if (correction >= 1.0 - MaximumCorrection && correction <= 1.0 + MaximumCorrection)
        {
            _accumulated = candidate;
            Correction = correction;
        }
        else
        {
            // Защита от накопления: пока поправка стоит в упоре, интеграл не
            // растёт. Без этого после долгой пропажи устройства регулятор
            // выходил бы из упора минутами, и всё это время разговор шёл бы не в
            // том темпе.
            Correction = Math.Clamp(correction, 1.0 - MaximumCorrection, 1.0 + MaximumCorrection);
        }

        return Correction;
    }

    /// <summary>Забывает накопленное. Зовётся при пересборке тракта.</summary>
    public void Reset()
    {
        Correction = 1.0;
        _accumulated = 0;
        _smoothedFill = 0;
        _started = false;
    }

    public string Summary =>
        $"темп пересчёта: {CorrectionPpm:+#;-#;0} ppm, запас приёма {_smoothedFill:F0} из {_targetFill}"
        + (IsSaturated ? " — ПОПРАВКА В УПОРЕ, дело не в часах" : string.Empty);
}
