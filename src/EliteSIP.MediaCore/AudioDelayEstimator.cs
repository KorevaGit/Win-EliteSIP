namespace EliteSIP.MediaCore;

/// <summary>Найденная задержка и то, насколько ей можно верить.</summary>
/// <param name="Milliseconds">Насколько принятое отстаёт от отправленного.</param>
/// <param name="Confidence">
/// Высота пика взаимной корреляции, 0…1. Единица — это совпадение огибающих
/// один в один; на живом звонке через кодек и АРУ хорошим считается 0,6 и выше.
/// Число отдаётся наружу намеренно: задержка, найденная по шуму, выглядит
/// точно так же, как настоящая, и отличить их можно только по этому полю.
/// </param>
public sealed record AudioDelayEstimate(double Milliseconds, double Confidence)
{
    /// <summary>
    /// Можно ли этому числу верить.
    ///
    /// Порог отделён от самого числа намеренно: неуверенный ответ полезнее
    /// пустого. «Пик 0,18 на сдвиге 340 мс» — это «мы искали и не нашли», и по
    /// нему видно, что искать надо иначе; <c>null</c> на том же месте выглядит
    /// как «замер не работает».
    /// </summary>
    public bool IsConfident => Confidence >= AudioDelayEstimator.MinimumConfidence;
}

/// <summary>
/// Измеряет задержку звукового тракта сравнением того, что мы отправили, с тем,
/// что вернулось.
///
/// <b>Зачем это отдельно от счётчиков буферов.</b> Сумма буферов отвечает на
/// вопрос «сколько задержки мы себе устроили» и считается из наших же чисел —
/// то есть повторяет наши собственные заблуждения. Корреляция отвечает на
/// вопрос «сколько её на самом деле», потому что меряет по звуку, прошедшему
/// весь путь целиком: кодирование, сеть, сервер, джиттер-буфер, воспроизведение.
/// Расхождение этих двух чисел — само по себе находка, и ловить его надо до
/// того, как оператор скажет «мы всё время перебиваем друг друга».
///
/// Считается по огибающей, а не по отсчётам. Через кодек, АРУ и эхоподавитель
/// сигнал приходит другим по форме — совпадать будет громкость, а не волна;
/// корреляция по отсчётам на таком тракте даёт красивый шум. Огибающая же
/// переживает и G.711, и АРУ, и смену уровня.
/// </summary>
public static class AudioDelayEstimator
{
    /// <summary>
    /// Шаг огибающей по умолчанию.
    ///
    /// Пять миллисекунд — это и разрешение измерения. Мельче нет смысла:
    /// разговорный тракт складывается из кадров по двадцать, и пятая часть
    /// кадра уже точнее, чем что-либо в нём меняется. Крупнее — и разница между
    /// «шестьдесят миллисекунд» и «сто» перестаёт быть видна, а это ровно та
    /// разница, ради которой всё и меряется.
    /// </summary>
    public const int DefaultStepMilliseconds = 5;

    /// <summary>
    /// Ниже этого пика ответ не отдаётся вовсе.
    ///
    /// Отказ полезнее неправды: задержку, найденную по тишине или по шуму,
    /// невозможно отличить от настоящей, и один такой ответ в отчёте
    /// обесценивает все остальные.
    /// </summary>
    public const double MinimumConfidence = 0.5;

    /// <summary>
    /// Строит огибающую: средняя громкость на каждом шаге.
    ///
    /// Средний модуль, а не среднеквадратичное: разница между ними для этой
    /// задачи в третьем знаке, а модуль не переполняется на громком сигнале и
    /// считается вдвое быстрее.
    /// </summary>
    public static float[] BuildEnvelope(ReadOnlySpan<short> samples, int samplesPerStep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samplesPerStep, 1);

        int steps = samples.Length / samplesPerStep;
        float[] envelope = new float[steps];

        for (int step = 0; step < steps; step++)
        {
            long sum = 0;
            int start = step * samplesPerStep;
            for (int index = 0; index < samplesPerStep; index++)
            {
                sum += Math.Abs(samples[start + index]);
            }

            envelope[step] = (float)sum / samplesPerStep;
        }

        return envelope;
    }

    /// <summary>Сколько отсчётов приходится на шаг огибающей при данной частоте.</summary>
    public static int SamplesPerStep(int sampleRate, int stepMilliseconds = DefaultStepMilliseconds) =>
        Math.Max(sampleRate * stepMilliseconds / 1000, 1);

    /// <summary>
    /// Ищет задержку принятого относительно отправленного.
    ///
    /// Обе огибающие сняты с одних часов — с часов звуковой карты, — поэтому
    /// сдвиг между ними и есть искомая задержка. Возвращает <c>null</c>, если
    /// сравнивать нечего или пик слишком низкий: см. <see cref="MinimumConfidence"/>.
    /// </summary>
    /// <param name="sent">Огибающая того, что мы отправили.</param>
    /// <param name="received">Огибающая того, что вернулось.</param>
    /// <param name="stepMilliseconds">Шаг обеих огибающих.</param>
    /// <param name="maximumLagMilliseconds">
    /// Докуда искать. Задержка больше секунды означает не задержку, а поломку, и
    /// искать её среди случайных совпадений незачем.
    /// </param>
    public static AudioDelayEstimate? Estimate(
        ReadOnlySpan<float> sent,
        ReadOnlySpan<float> received,
        int stepMilliseconds = DefaultStepMilliseconds,
        int maximumLagMilliseconds = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stepMilliseconds, 1);

        int maximumLag = maximumLagMilliseconds / stepMilliseconds;
        if (sent.IsEmpty || received.IsEmpty || maximumLag < 1)
        {
            return null;
        }

        // Сдвиг ищется в обе стороны, и это не перестраховка.
        //
        // Две огибающие начинаются не одновременно: приём начинает
        // накапливаться тогда, когда пришёл первый кадр, а он может прийти
        // сильно позже первого отправленного — например, если сервер, как
        // Asterisk на эхо-номере, сначала выжидает секунду и всё это время
        // выбрасывает наш звук. Тогда в массиве принятого недостаёт начала, и
        // совпадение оказывается на ОТРИЦАТЕЛЬНОМ сдвиге индексов, хотя во
        // времени звук, разумеется, отстаёт.
        //
        // Разбираться со временем — дело вызывающего: здесь считается сдвиг
        // между двумя рядами чисел, а к какому моменту относится нулевой
        // элемент каждого, знает только тот, кто их собирал.

        // Сравнивать надо колебания громкости, а не саму громкость: два потока
        // с разным средним уровнем — обычное дело (АРУ на сервере), и без
        // вычитания среднего они коррелируют между собой уже тем, что оба
        // громкие.
        float[] sentCentered = Centered(sent);
        float[] receivedCentered = Centered(received);

        double bestScore = 0;
        int bestLag = 0;

        for (int lag = -maximumLag; lag <= maximumLag; lag++)
        {
            int from = Math.Max(0, -lag);
            int length = Math.Min(sentCentered.Length - from, receivedCentered.Length - from - lag);

            // Сравнивать надо на длинном куске: чем меньше точек, тем выше
            // случайный пик, и самые дальние сдвиги всегда выигрывали бы у
            // настоящих.
            if (length < maximumLag)
            {
                continue;
            }

            double product = 0;
            double sentEnergy = 0;
            double receivedEnergy = 0;

            for (int index = from; index < from + length; index++)
            {
                float left = sentCentered[index];
                float right = receivedCentered[index + lag];
                product += left * right;
                sentEnergy += left * left;
                receivedEnergy += right * right;
            }

            if (sentEnergy <= 0 || receivedEnergy <= 0)
            {
                continue;
            }

            double score = product / Math.Sqrt(sentEnergy * receivedEnergy);
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        // Отдаётся всегда, даже неуверенное: судит вызывающий по
        // <see cref="AudioDelayEstimate.IsConfident"/>. Пустой ответ на месте
        // низкого пика выглядел бы как «замер сломан», хотя на самом деле это
        // «искали и не нашли» — а это разные новости.
        return new AudioDelayEstimate(bestLag * (double)stepMilliseconds, bestScore);
    }

    private static float[] Centered(ReadOnlySpan<float> values)
    {
        double sum = 0;
        for (int index = 0; index < values.Length; index++)
        {
            sum += values[index];
        }

        float mean = (float)(sum / values.Length);
        float[] centered = new float[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            centered[index] = values[index] - mean;
        }

        return centered;
    }
}
