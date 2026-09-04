namespace AudioProbe;

/// <summary>
/// Уход часов одного устройства относительно другого — по наклону, а не по
/// разности концов.
///
/// **Почему не «конец минус начало».** <c>IAudioClock</c> обновляется раз в
/// период устройства, то есть его показание квантовано десятью миллисекундами.
/// Разность двух таких показаний несёт до ±20 мс случайной ошибки, и на окне в
/// семнадцать секунд это шум порядка тысячи ppm — на порядок больше самого
/// ухода. Первые прогоны стенда так и выглядели: одна и та же гарнитура давала
/// то +500, то −2650 ppm, и числа выглядели убедительно ровно до тех пор, пока
/// их не сравнили между собой.
///
/// Наклон прямой, проведённой по сотням проб, от квантования почти не
/// страдает: ошибка каждой пробы случайна и в сумме гасится. Заодно появляется
/// то, чего у разности концов нет вовсе, — <see cref="StandardError"/>, то есть
/// честный ответ на вопрос «а достаточно ли долго мы мерили».
/// </summary>
internal sealed class DriftEstimator
{
    private readonly Lock _gate = new();
    private readonly List<(double Time, double Difference)> _samples = [];

    public void Add(double timeSeconds, double differenceSeconds)
    {
        lock (_gate)
        {
            _samples.Add((timeSeconds, differenceSeconds));
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
    /// Наклон в ppm: на сколько миллионных долей вывод уходит от захвата.
    /// Знак положителен, когда захват обгоняет вывод.
    /// </summary>
    public double Ppm => Fit().Slope * 1_000_000.0;

    /// <summary>
    /// Насколько можно верить наклону, в тех же ppm. Правило простое: если
    /// ошибка сравнима с самим наклоном, мерили мало, и вывод делать рано.
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

            // Разброс точек вокруг прямой. Он и превращается в оценку
            // достоверности наклона.
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
