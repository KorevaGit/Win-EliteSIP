using EliteSIP.Audio;
using SoundFlow.Extensions.WebRtc.Apm;

namespace AudioProbe;

/// <summary>
/// Эхоподавление на смоделированной комнате. Без устройств и без звука.
///
/// **Зачем понадобилось.** Проверить AEC на живых динамиках нельзя: ноутбук
/// стоит в офисе, и громкость, при которой эхо поднимется над шумом микрофона
/// на нужные пятнадцать-двадцать децибел, там недопустима. А на тихих
/// динамиках мерить нечего — замер 7 сентября показал, что при 34% громкости
/// эхо всего на 1,5 дБ выше шума.
///
/// **Чем это отличается от <see cref="AecSelfTest"/>.** Тот проверял, что APM
/// вообще зовётся правильно, и подавал идеальное эхо — одну задержанную копию.
/// Такое давится на 52,9 дБ и ничего не доказывает про комнату. Здесь путь эха
/// — модель помещения: прямой приход плюс затухающий хвост отражений. Именно
/// хвост делает эхоподавление трудным, потому что фильтр приходится строить
/// длинный, и именно его не было в первой проверке.
///
/// **Что здесь настоящее, а что модель.** Настоящие — задержка 67 мс, взятая
/// из живого замера `echo-path` на этой машине, и полоса сигнала. Модель —
/// форма хвоста (экспоненциальное затухание, RT60 задаётся) и уровень шума.
/// Модель не заменяет приёмку на живой машине с громкими динамиками; она
/// отвечает на другой вопрос: **при каком отношении эха к шуму AEC3 перестаёт
/// работать.** Этого достаточно, чтобы решить, годится ли путь A, и чтобы
/// знать, что требовать от рабочего места.
/// </summary>
internal static class AecModelProbe
{
    private const int Rate = 48000;
    private const int FrameMs = 10;

    /// <summary>Задержка прямого прихода. Из живого замера на этой машине.</summary>
    private const int DirectDelayMs = 67;

    public static int Run(int seconds, int reverbMs, bool suppression, bool product = false)
    {
        int frame = Rate / 1000 * FrameMs;

        Console.WriteLine("Эхоподавление на модели комнаты. Устройства не открываются, звука нет.");
        Console.WriteLine("Прямой приход {0} мс, хвост отражений {1} мс.", DirectDelayMs, reverbMs);
        Console.WriteLine(product
            ? "Обработка: ПРОДУКТОВАЯ (EliteSIP.Audio.VoiceProcessor), полная цепочка."
            : "Обработка: собранная стендом.");
        Console.WriteLine();

        float[] response = RoomResponse(reverbMs);
        Console.WriteLine("Длина отклика: {0} мс ({1} отсчётов)", response.Length * 1000 / Rate, response.Length);
        Console.WriteLine();

        Console.WriteLine("Эхо/шум   ERLE     остаток");
        Console.WriteLine("-------   ------   -------");

        // Наименьшее отношение эха к шуму, на котором подавление ещё держит
        // 20 дБ.
        //
        // Здесь была ошибка вывода, стоившая неверного приговора в отчёте:
        // порог запоминался как «последнее пройденное значение», а перебор
        // идёт сверху вниз. Пока проходили не все, это случайно совпадало с
        // правдой; когда прошли все до нуля включительно, порог обнулился, и
        // стенд объявил, что подавления нет вовсе, — на прогоне, где оно было
        // лучшим из всех.
        int[] ratios = [40, 30, 20, 15, 10, 6, 3, 0];
        int threshold = int.MaxValue;

        foreach (int echoToNoiseDb in ratios)
        {
            (double erle, double residual) = product
                ? MeasureProduct(seconds, frame, response, echoToNoiseDb)
                : Measure(seconds, frame, response, echoToNoiseDb, suppression);
            Console.WriteLine("{0,5} дБ   {1,5:F1}    {2,6:F1} дБ", echoToNoiseDb, erle, residual);

            if (erle >= 20)
            {
                threshold = Math.Min(threshold, echoToNoiseDb);
            }
        }

        Console.WriteLine();

        if (threshold == int.MaxValue)
        {
            Console.WriteLine("Подавление не выходит на 20 дБ ни при каком отношении эха к шуму.");
            return 2;
        }

        if (threshold == ratios[^1])
        {
            Console.WriteLine(
                "Подавление держит 20 дБ на всех проверенных отношениях, вплоть до {0} дБ.",
                ratios[^1]);
            Console.WriteLine("Требования к рабочему месту эта цепочка не предъявляет.");
            return 0;
        }

        Console.WriteLine("Подавление держит 20 дБ, пока эхо выше шума хотя бы на {0} дБ.", threshold);
        Console.WriteLine("Это и есть требование к рабочему месту, а не к коду.");
        return 0;
    }

    /// <summary>
    /// Отклик помещения: прямой приход и затухающий хвост.
    ///
    /// Хвост — шум с экспоненциальной огибающей. Это стандартная модель малого
    /// помещения, и для проверки эхоподавителя важна не точность формы, а сам
    /// факт, что отражений много и они размазаны во времени: линейный фильтр
    /// приходится строить на всю длину хвоста.
    /// </summary>
    private static float[] RoomResponse(int reverbMs)
    {
        int delay = Rate / 1000 * DirectDelayMs;
        int tail = Rate / 1000 * reverbMs;
        var response = new float[delay + tail];
        var random = new Random(20260907);

        // Прямой приход — самый громкий.
        response[delay] = 1.0f;

        // Хвост: затухание до -60 дБ за reverbMs.
        double decay = Math.Log(1000.0) / tail;
        for (int i = 1; i < tail; i++)
        {
            float noise = (float)((random.NextDouble() * 2.0) - 1.0);
            response[delay + i] = noise * (float)Math.Exp(-decay * i) * 0.5f;
        }

        // Нормируем по энергии, чтобы уровень эха задавался отдельно и не
        // зависел от длины хвоста.
        double energy = 0;
        foreach (float value in response)
        {
            energy += (double)value * value;
        }

        float scale = (float)(1.0 / Math.Sqrt(energy));
        for (int i = 0; i < response.Length; i++)
        {
            response[i] *= scale;
        }

        return response;
    }

    private static (double Erle, double Residual) Measure(
        int seconds,
        int frame,
        float[] response,
        int echoToNoiseDb,
        bool suppression)
    {
        using var apm = new AudioProcessingModule();
        using (var config = new ApmConfig())
        {
            config.SetPipeline(Rate, false, false, DownmixMethod.AverageChannels);
            config.SetEchoCanceller(true, false);
            config.SetNoiseSuppression(suppression, NoiseSuppressionLevel.High);
            config.SetHighPassFilter(suppression);
            config.SetGainController2(suppression);
            apm.ApplyConfig(config);
        }

        apm.Initialize();

        var farEnd = new FarEndSource(Rate, 0.4f);
        var random = new Random(20260907);

        var far = new float[1][];
        var farOut = new float[1][];
        var near = new float[1][];
        var nearOut = new float[1][];
        far[0] = new float[frame];
        farOut[0] = new float[frame];
        near[0] = new float[frame];
        nearOut[0] = new float[frame];

        using var streamConfig = new StreamConfig(Rate, 1);

        int frames = seconds * 1000 / FrameMs;
        int converge = 4 * 1000 / FrameMs;

        // Линия задержки под свёртку с откликом.
        var history = new float[response.Length + frame];

        // Уровень шума считается от энергии эха: эхо получается единичной
        // мощности после нормировки отклика, значит шум задаётся напрямую.
        double noiseAmplitude = 0.4 * Math.Pow(10.0, -echoToNoiseDb / 20.0) * 0.3;

        double inputEnergy = 0;
        double outputEnergy = 0;
        int counted = 0;

        for (int f = 0; f < frames; f++)
        {
            farEnd.Fill(far[0]);
            apm.ProcessReverseStream(far, streamConfig, streamConfig, farOut);

            // Сдвигаем историю и дописываем новый кадр.
            Array.Copy(history, frame, history, 0, history.Length - frame);
            Array.Copy(far[0], 0, history, history.Length - frame, frame);

            for (int i = 0; i < frame; i++)
            {
                // Свёртка: эхо — сумма отражений опорного сигнала.
                double echo = 0;
                int position = history.Length - frame + i;
                for (int k = 0; k < response.Length; k++)
                {
                    int index = position - k;
                    if (index < 0)
                    {
                        break;
                    }

                    echo += history[index] * response[k];
                }

                float noise = (float)(((random.NextDouble() * 2.0) - 1.0) * noiseAmplitude);
                near[0][i] = (float)echo + noise;
            }

            apm.SetStreamDelayMs(DirectDelayMs);
            apm.ProcessStream(near, streamConfig, streamConfig, nearOut);

            if (f >= converge)
            {
                double before = Energy(near[0]);
                if (before > 1e-12)
                {
                    inputEnergy += before;
                    outputEnergy += Energy(nearOut[0]);
                    counted++;
                }
            }
        }

        if (counted == 0 || outputEnergy <= 0)
        {
            return (0, double.NegativeInfinity);
        }

        double erle = 10.0 * Math.Log10(inputEnergy / outputEnergy);
        double residual = 10.0 * Math.Log10(outputEnergy / counted);
        return (erle, residual);
    }

    /// <summary>
    /// Тот же замер, но через продуктовый блок обработки.
    ///
    /// <b>Зачем повторять уже сделанное.</b> Прогон выше проверяет APM,
    /// настроенный <b>стендом</b>. Продукт настраивает его сам и своим кодом, и
    /// между этими настройками помещается целый класс ошибок, которых замер
    /// стенда не увидит: не тот порядок вызовов, потерянный блок цепочки,
    /// кадр не той длины, мобильный режим вместо AEC3. Все они выглядят как
    /// работающий тракт — просто эхо не давится, а узнать об этом можно только
    /// от собеседника.
    ///
    /// Разница между двумя столбцами чисел и есть ответ на вопрос «настроено ли
    /// в продукте то же, что проверено в W0».
    ///
    /// Опорный сигнал здесь только <b>наблюдается</b>, а не обрабатывается:
    /// продукт зовёт <c>AnalyzeReverse</c> на то, что уже отдал устройству, и
    /// свёртка комнаты идёт по тому же неизменённому сигналу. Это ближе к
    /// действительности, чем <c>ProcessReverseStream</c> в прогоне выше.
    /// </summary>
    private static (double Erle, double Residual) MeasureProduct(
        int seconds,
        int frame,
        float[] response,
        int echoToNoiseDb)
    {
        using var processor = new VoiceProcessor(Rate, automaticGainControl: false);

        if (processor.FrameSamples != frame)
        {
            throw new InvalidOperationException(
                $"кадр продукта {processor.FrameSamples} не совпадает с кадром стенда {frame}");
        }

        var farEnd = new FarEndSource(Rate, 0.4f);
        var random = new Random(20260907);

        var far = new float[frame];
        var near = new float[frame];
        var nearOut = new float[frame];
        var history = new float[response.Length + frame];

        int frames = seconds * 1000 / FrameMs;
        int converge = 4 * 1000 / FrameMs;
        double noiseAmplitude = 0.4 * Math.Pow(10.0, -echoToNoiseDb / 20.0) * 0.3;

        double inputEnergy = 0;
        double outputEnergy = 0;
        int counted = 0;

        for (int f = 0; f < frames; f++)
        {
            farEnd.Fill(far);
            processor.AnalyzeReverse(far);

            Array.Copy(history, frame, history, 0, history.Length - frame);
            Array.Copy(far, 0, history, history.Length - frame, frame);

            for (int i = 0; i < frame; i++)
            {
                double echo = 0;
                int position = history.Length - frame + i;
                for (int k = 0; k < response.Length; k++)
                {
                    int index = position - k;
                    if (index < 0)
                    {
                        break;
                    }

                    echo += history[index] * response[k];
                }

                float noise = (float)(((random.NextDouble() * 2.0) - 1.0) * noiseAmplitude);
                near[i] = (float)echo + noise;
            }

            processor.Process(near, nearOut, DirectDelayMs);

            if (f >= converge)
            {
                double before = Energy(near);
                if (before > 1e-12)
                {
                    inputEnergy += before;
                    outputEnergy += Energy(nearOut);
                    counted++;
                }
            }
        }

        if (counted == 0 || outputEnergy <= 0)
        {
            return (0, double.NegativeInfinity);
        }

        return (
            10.0 * Math.Log10(inputEnergy / outputEnergy),
            10.0 * Math.Log10(outputEnergy / counted));
    }

    private static double Energy(float[] samples)
    {
        double sum = 0;
        foreach (float sample in samples)
        {
            sum += (double)sample * sample;
        }

        return sum / samples.Length;
    }
}
