namespace EliteSIP.Audio;

/// <summary>
/// Уход часов одного устройства относительно другого — по наклону, а не по
/// разности концов.
///
/// <b>Этого в оригинале нет и не могло быть.</b> На macOS разные устройства на
/// вход и выход сводились агрегатным устройством, которое собирала система:
/// часы получались общие, и расхождения не существовало как явления. В WASAPI
/// это два независимых потока со своими кварцами, и сотня миллионных долей —
/// это отсчёт в секунду, то есть полсекунды набежавшей задержки за час
/// разговора либо, наоборот, опустевшее кольцо и щелчок.
///
/// <b>Почему не «конец минус начало».</b> Часы устройства обновляются раз в его
/// период, то есть показание квантовано десятью миллисекундами. Разность двух
/// таких показаний несёт до ±20 мс случайной ошибки, и на окне в семнадцать
/// секунд это шум порядка тысячи ppm — на порядок больше самой измеряемой
/// величины. Первые прогоны W0 так и выглядели: одна и та же гарнитура давала
/// то +500, то −2650 ppm, и числа выглядели убедительно ровно до тех пор, пока
/// их не сравнили между собой.
///
/// Наклон прямой по сотням проб от квантования почти не страдает: ошибка каждой
/// пробы случайна и в сумме гасится. Заодно появляется то, чего у разности
/// концов нет вовсе, — <see cref="StandardError"/>, то есть честный ответ на
/// вопрос «а достаточно ли долго мы мерили».
///
/// <b>Считает, но не чинит.</b> Компенсацией занимается
/// <see cref="PlaybackRateController"/>, и она устроена иначе — по заполнению
/// кольца, а не по этому числу. Здесь величина для диагностики и для приёмки
/// «восемь часов без деградации»: если уход измерен и мал, а кольцо всё равно
/// уползает, значит дело не в часах.
/// </summary>
public sealed class ClockDriftEstimator
{
    private readonly Lock _gate = new();
    private readonly List<(double Time, double Difference)> _samples = [];
    private readonly int _capacity;

    /// <param name="capacity">
    /// Сколько проб держать. Восьмичасовая смена при пробе в секунду — это
    /// почти тридцать тысяч точек, и хранить их все незачем: наклон считается
    /// по скользящему окну, а не по всей смене. Старые пробы вытесняются.
    /// </param>
    public ClockDriftEstimator(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 3);
        _capacity = capacity;
    }

    /// <summary>Проба: момент по часам машины и разность показаний двух устройств.</summary>
    public void Add(double timeSeconds, double differenceSeconds)
    {
        lock (_gate)
        {
            _samples.Add((timeSeconds, differenceSeconds));
            if (_samples.Count > _capacity)
            {
                _samples.RemoveRange(0, _samples.Count - _capacity);
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>
    /// Наклон в ppm: на сколько миллионных долей вывод уходит от захвата. Знак
    /// положителен, когда захват обгоняет вывод.
    /// </summary>
    public double Ppm => Fit().Slope * 1_000_000.0;

    /// <summary>
    /// Насколько можно верить наклону, в тех же ppm.
    ///
    /// Правило простое и им пользовались в W0: если ошибка сравнима с самим
    /// наклоном, мерили мало, и вывод делать рано.
    /// </summary>
    public double StandardError => Fit().StandardError * 1_000_000.0;

    /// <summary>Длина окна, по которому построена прямая.</summary>
    public double WindowSeconds
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count < 2 ? 0 : _samples[^1].Time - _samples[0].Time;
            }
        }
    }

    /// <summary>
    /// Наименьшее окно, на котором числу можно верить.
    ///
    /// Замер W0 прямо про это: та же гарнитура на 297 с дала 57 ± 2 ppm, а на
    /// 57 с — 212 ± 22. Погрешность честно выросла на порядок, но обе оценки
    /// выглядели как числа с ошибкой, и различить их было нечем. Дело в
    /// квантовании: показание часов обновляется раз в период устройства, и на
    /// коротком окне этот шаг перевешивает измеряемую величину.
    /// </summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(60);

    /// <summary>Наименьшее число проб. Прямая по трём точкам — это не замер.</summary>
    public const int MinimumSamples = 30;

    /// <summary>Можно ли уже верить числу.</summary>
    public bool IsTrustworthy
    {
        get
        {
            if (Count < MinimumSamples || WindowSeconds < MinimumWindow.TotalSeconds)
            {
                return false;
            }

            (double slope, double error) = Fit();
            double ppm = Math.Abs(slope * 1_000_000.0);
            double errorPpm = error * 1_000_000.0;

            // Либо уход заметно больше своей погрешности, либо он мал вместе с
            // ней — второе тоже ответ: «расхождения нет».
            return ppm > 3 * errorPpm || (ppm < 5 && errorPpm < 5);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
        }
    }

    public string Summary
    {
        get
        {
            if (Count < 3)
            {
                return "уход часов: проб мало";
            }

            return $"уход часов: {Ppm:F1} ± {StandardError:F1} ppm "
                + $"по {Count} пробам за {WindowSeconds:F0} с"
                + (IsTrustworthy ? string.Empty : " — верить рано");
        }
    }

    private (double Slope, double StandardError) Fit()
    {
        lock (_gate)
        {
            int n = _samples.Count;
            if (n < 3)
            {
                return (0, 0);
            }

            double meanTime = 0;
            double meanDifference = 0;
            foreach ((double time, double difference) in _samples)
            {
                meanTime += time;
                meanDifference += difference;
            }

            meanTime /= n;
            meanDifference /= n;

            double covariance = 0;
            double variance = 0;
            foreach ((double time, double difference) in _samples)
            {
                double dt = time - meanTime;
                covariance += dt * (difference - meanDifference);
                variance += dt * dt;
            }

            if (variance <= 0)
            {
                return (0, 0);
            }

            double slope = covariance / variance;
            double intercept = meanDifference - (slope * meanTime);

            double residual = 0;
            foreach ((double time, double difference) in _samples)
            {
                double predicted = intercept + (slope * time);
                double error = difference - predicted;
                residual += error * error;
            }

            double sigma = Math.Sqrt(residual / (n - 2));
            return (slope, sigma / Math.Sqrt(variance));
        }
    }
}

