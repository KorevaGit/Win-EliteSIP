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

    public static int Run(int seconds, int reverbMs, bool suppression)
    {
        int frame = Rate / 1000 * FrameMs;

        Console.WriteLine("Эхоподавление на модели комнаты. Устройства не открываются, звука нет.");
        Console.WriteLine("Прямой приход {0} мс, хвост отражений {1} мс.", DirectDelayMs, reverbMs);
        Console.WriteLine();

        float[] response = RoomResponse(reverbMs);
        Console.WriteLine("Длина отклика: {0} мс ({1} отсчётов)", response.Length * 1000 / Rate, response.Length);
        Console.WriteLine();

        Console.WriteLine("Эхо/шум   ERLE     остаток");
        Console.WriteLine("-------   ------   -------");

        int worst = 0;
        foreach (int echoToNoiseDb in new[] { 40, 30, 20, 15, 10, 6, 3, 0 })
        {
            (double erle, double residual) = Measure(seconds, frame, response, echoToNoiseDb, suppression);
            Console.WriteLine("{0,5} дБ   {1,5:F1}    {2,6:F1} дБ", echoToNoiseDb, erle, residual);

            if (erle >= 20)
            {
                worst = echoToNoiseDb;
            }
        }

        Console.WriteLine();
        if (worst > 0)
        {
            Console.WriteLine(
                "AEC3 держит 20 дБ подавления, пока эхо выше шума хотя бы на {0} дБ.", worst);
            Console.WriteLine("Это и есть требование к рабочему месту, а не к коду.");
            return 0;
        }

        Console.WriteLine("AEC3 не выходит на 20 дБ ни при каком отношении эха к шуму.");
        return 2;
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
