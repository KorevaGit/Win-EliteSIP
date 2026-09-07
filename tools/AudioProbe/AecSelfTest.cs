using SoundFlow.Extensions.WebRtc.Apm;

namespace AudioProbe;

/// <summary>
/// Проверка эхоподавителя без звуковых устройств.
///
/// **Зачем.** На живых динамиках ERLE вышел 1,2 дБ при заведомо живом эхе:
/// опорный сигнал −28,4 дБ, микрофон −40,6 дБ. Причин ровно две — либо APM
/// зовётся неправильно, либо неправильно устроен тракт вокруг него. Разделить
/// их наблюдением нельзя, а подстановкой — можно.
///
/// Здесь «микрофон» синтезируется: это тот же опорный сигнал, задержанный на
/// известное число миллисекунд и ослабленный. Идеальное эхо без комнаты, без
/// нелинейности динамика, без ухода часов. Такое обязано подавляться на
/// десятки децибел. Если не подавляется — виноват не тракт, а вызовы.
///
/// Заодно печатаются коды возврата, которые в первой версии игнорировались.
/// Это тоже урок: у всех четырёх функций APM возвращаемое значение — код
/// ошибки, и молчаливое его игнорирование стоило двух прогонов вслепую.
/// </summary>
internal static class AecSelfTest
{
    private const int Rate = 48000;
    private const int FrameMs = 10;

    public static int Run(int delayMs, int seconds)
    {
        int frame = Rate / 1000 * FrameMs;
        int delaySamples = Rate / 1000 * delayMs;

        Console.WriteLine("Проверка APM без устройств.");
        Console.WriteLine("«Микрофон» = опорный сигнал, задержанный на {0} мс и ослабленный на 12 дБ.", delayMs);
        Console.WriteLine();

        using var apm = new AudioProcessingModule();
        using (var config = new ApmConfig())
        {
            config.SetPipeline(Rate, false, false, DownmixMethod.AverageChannels);
            config.SetEchoCanceller(true, false);
            config.SetNoiseSuppression(false, NoiseSuppressionLevel.Low);
            config.SetHighPassFilter(false);
            config.SetGainController2(false);
            Console.WriteLine("ApplyConfig  -> {0}", apm.ApplyConfig(config));
        }

        Console.WriteLine("Initialize   -> {0}", apm.Initialize());
        Console.WriteLine();

        var farEnd = new FarEndSource(Rate);
        var echoLine = new float[delaySamples + frame];
        int echoFill = 0;

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
        int converge = 3 * 1000 / FrameMs;

        double inputEnergy = 0;
        double outputEnergy = 0;
        int counted = 0;
        ApmError? firstReverseCode = null;
        ApmError? firstForwardCode = null;

        var line = new List<float>(delaySamples + (frames * frame));

        for (int f = 0; f < frames; f++)
        {
            farEnd.Fill(far[0]);

            ApmError reverseCode = apm.ProcessReverseStream(far, streamConfig, streamConfig, farOut);
            firstReverseCode ??= reverseCode;

            // Линия задержки: эхо — это опорный сигнал, прозвучавший delayMs
            // назад, ослабленный на 12 дБ (коэффициент 0.25).
            foreach (float sample in far[0])
            {
                line.Add(sample * 0.25f);
            }

            int start = (f * frame) - delaySamples;
            for (int i = 0; i < frame; i++)
            {
                int index = start + i;
                near[0][i] = index >= 0 && index < line.Count ? line[index] : 0f;
            }

            apm.SetStreamDelayMs(delayMs);
            ApmError forwardCode = apm.ProcessStream(near, streamConfig, streamConfig, nearOut);
            firstForwardCode ??= forwardCode;

            if (f >= converge)
            {
                double before = Energy(near[0]);
                if (before > 1e-9)
                {
                    inputEnergy += before;
                    outputEnergy += Energy(nearOut[0]);
                    counted++;
                }
            }
        }

        _ = echoLine;
        _ = echoFill;

        Console.WriteLine("ProcessReverseStream -> {0}", firstReverseCode);
        Console.WriteLine("ProcessStream        -> {0}", firstForwardCode);
        Console.WriteLine();

        if (counted == 0 || outputEnergy <= 0)
        {
            Console.WriteLine("Считать нечего.");
            return 1;
        }

        double erle = 10.0 * Math.Log10(inputEnergy / outputEnergy);
        Console.WriteLine("Кадров в замере:   {0}", counted);
        Console.WriteLine("Вход:              {0,7:F1} дБ", 10.0 * Math.Log10(inputEnergy / counted));
        Console.WriteLine("Выход:             {0,7:F1} дБ", 10.0 * Math.Log10(outputEnergy / counted));
        Console.WriteLine("ERLE:              {0,7:F1} дБ", erle);
        Console.WriteLine();

        if (erle >= 20)
        {
            Console.WriteLine("APM зовётся правильно. Значит виноват тракт вокруг него, а не вызовы.");
            return 0;
        }

        Console.WriteLine("APM не подавляет даже идеальное эхо. Виноваты вызовы, а не тракт.");
        return 2;
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
