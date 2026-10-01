using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Пересчёт частоты между устройством и разговором.
///
/// Проверок в оригинале нет, потому что нет и самого типа: на macOS пересчёт
/// делала система. Здесь он наш, а значит наши и его ошибки — и главная из них
/// не слышна как «плохой звук», а слышна как чужие призвуки: то, что выше
/// половины частоты кодека, при прореживании возвращается зеркально отражённым.
/// Ловится это не ухом, а замером, и потому проверяется здесь.
/// </summary>
public sealed class ResamplerTests
{
    [Fact]
    public void Равные_частоты_проходят_без_изменений()
    {
        // На гарнитуре в режиме связи устройство и кодек оба на 8 кГц. Лишний
        // фильтр там только съел бы верх полосы, которой и так мало.
        Resampler resampler = new(8000, 8000);
        float[] input = [0.1f, -0.4f, 0.9f, 0f, -1f];
        float[] output = new float[input.Length];

        Assert.Equal(input.Length, resampler.Process(input, output));
        Assert.Equal(input, output);
        Assert.Equal(0, resampler.LatencySamples);
    }

    [Fact]
    public void Постоянный_уровень_остаётся_постоянным()
    {
        // Усиление на постоянном токе — это то, что ломается первым и
        // незаметнее всего: дрожание в сотые доли процента не слышно, но
        // разъезжает баланс отсчётов и все замеры, которые на него опираются.
        Resampler resampler = new(48000, 8000);
        float[] input = new float[48000];
        Array.Fill(input, 0.5f);
        float[] output = new float[9000];

        int written = resampler.Process(input, output);

        // Первые отсчёты приходятся на разгон истории — там уровень законно
        // ниже, потому что слева тишина.
        int settled = resampler.LatencySamples;
        for (int i = settled; i < written; i++)
        {
            Assert.True(
                Math.Abs(output[i] - 0.5f) < 0.002f,
                $"отсчёт {i}: {output[i]} вместо 0.5");
        }
    }

    [Theory]
    [InlineData(48000, 8000)]
    [InlineData(48000, 16000)]
    [InlineData(44100, 8000)]
    [InlineData(16000, 48000)]
    public void Число_отсчётов_сходится_с_отношением_частот(int inputRate, int outputRate)
    {
        // Баланс отсчётов из W0 держится на этом: сколько устройство отдало,
        // столько и должно пройти дальше, с точностью до отношения частот.
        Resampler resampler = new(inputRate, outputRate);
        float[] input = new float[inputRate];
        float[] output = new float[outputRate + 64];

        int written = resampler.Process(input, output);
        double expected = inputRate * resampler.Ratio;

        // Допуск — задержка ядра, пересчитанная в отсчёты выхода: на неё выход
        // законно отстаёт от входа.
        double allowance = (resampler.LatencySamples * resampler.Ratio) + 8;
        Assert.True(
            Math.Abs(written - expected) <= allowance,
            $"выдано {written}, ожидалось около {expected:F0} (допуск {allowance:F0})");
    }

    [Fact]
    public void Тон_в_полосе_проходит_целым()
    {
        // Тысяча герц — середина телефонной полосы. Если пересчёт её портит,
        // портит он весь разговор.
        const int InputRate = 48000;
        const int OutputRate = 8000;
        const double Frequency = 1000;

        float[] output = Run(InputRate, OutputRate, Frequency, seconds: 1);
        double amplitude = PeakAmplitude(output, OutputRate, Frequency);

        Assert.True(amplitude > 0.95, $"тон 1 кГц ослаб до {amplitude:F3}");
    }

    [Fact]
    public void Тон_за_полосой_подавляется_а_не_возвращается_отражённым()
    {
        // Главная проверка файла. Шесть килогерц при выходе 8 кГц — это выше
        // половины частоты. При простом прореживании такой тон вернулся бы на
        // 2 кГц и звучал бы как посторонний свист в трубке собеседника; при
        // честном фильтре его не должно остаться вовсе.
        const int InputRate = 48000;
        const int OutputRate = 8000;

        float[] output = Run(InputRate, OutputRate, frequency: 6000, seconds: 1);

        // Отражение легло бы ровно на 8000 - 6000 = 2000 Гц.
        double ghost = PeakAmplitude(output, OutputRate, 2000);
        Assert.True(ghost < 0.01, $"отражение на 2 кГц не подавлено: {ghost:F4}");

        // И вообще ничего заметного остаться не должно.
        double loudest = 0;
        for (int i = OutputRate / 4; i < output.Length; i++)
        {
            loudest = Math.Max(loudest, Math.Abs(output[i]));
        }

        Assert.True(loudest < 0.02, $"за полосой осталось {loudest:F4}");
    }

    [Theory]
    [InlineData(300, 0.99, 1.01)]
    [InlineData(1000, 0.99, 1.01)]
    [InlineData(3000, 0.99, 1.01)]
    [InlineData(3400, 0.97, 1.01)]
    [InlineData(4500, 0, 0.02)]
    [InlineData(5000, 0, 0.002)]
    [InlineData(6000, 0, 0.001)]
    public void Отклик_фильтра_держится_в_замеренных_границах(
        double frequency,
        double atLeast,
        double atMost)
    {
        // Числа сняты замером на этой сборке, а не выведены из теории окна.
        // Полоса ровная до 3400 Гц — это ровно верх телефонной полосы G.711, то
        // есть всё, что вообще доедет до собеседника, проходит нетронутым:
        //
        //    300 Гц  0,0 дБ    3400 Гц  -0,0 дБ    4500 Гц  -39,4 дБ
        //   1000 Гц  0,0 дБ    3600 Гц  -0,3 дБ    5000 Гц  -75,3 дБ
        //   3000 Гц  0,0 дБ    3800 Гц  -1,9 дБ    6000 Гц  -89,9 дБ
        //
        // Провал между 3800 и 4500 — переходная полоса фильтра, и трогать её
        // незачем: сеть в G.711 выше 3400 Гц ничего не несёт.
        //
        // Проверка сторожевая: если ядро, окно или ширина изменятся, числа
        // уедут, и это должно быть замечено здесь, а не ухом собеседника.
        Resampler resampler = new(48000, 8000);
        float[] output = Run(48000, 8000, frequency, seconds: 1);
        double amplitude = PeakAmplitude(output, 8000, frequency);

        Assert.True(amplitude >= atLeast, $"{frequency:F0} Гц ослаб до {amplitude:F5}");
        Assert.True(amplitude <= atMost, $"{frequency:F0} Гц прошёл на {amplitude:F5}");
        Assert.Equal(96, resampler.LatencySamples);
    }

    [Fact]
    public void Повышение_частоты_не_портит_тон()
    {
        // Обратное направление: 8 кГц кодека в 48 кГц устройства. Здесь резать
        // нечего, но интерполировать надо честно.
        float[] output = Run(8000, 48000, frequency: 1000, seconds: 1);
        double amplitude = PeakAmplitude(output, 48000, 1000);

        Assert.True(amplitude > 0.95, $"тон ослаб до {amplitude:F3}");
    }

    [Fact]
    public void Пересчёт_идёт_кусками_так_же_как_целиком()
    {
        // Движок отдаёт отсчёты пакетами по десять миллисекунд, а не секундой.
        // Разрывов на границах пакетов быть не должно — иначе в разговоре будет
        // сто щелчков в секунду, и списаны они будут на сеть.
        const int InputRate = 48000;
        const int OutputRate = 8000;

        float[] input = Tone(InputRate, 1000, seconds: 1);

        Resampler whole = new(InputRate, OutputRate);
        float[] wholeOutput = new float[OutputRate + 64];
        int wholeCount = whole.Process(input, wholeOutput);

        Resampler chunked = new(InputRate, OutputRate);
        float[] chunkedOutput = new float[OutputRate + 64];
        int chunkedCount = 0;
        const int Chunk = 480; // 10 мс — период устройства из замеров W0
        for (int offset = 0; offset < input.Length; offset += Chunk)
        {
            int take = Math.Min(Chunk, input.Length - offset);
            chunkedCount += chunked.Process(
                input.AsSpan(offset, take),
                chunkedOutput.AsSpan(chunkedCount));
        }

        Assert.Equal(wholeCount, chunkedCount);
        for (int i = 0; i < wholeCount; i++)
        {
            Assert.True(
                Math.Abs(wholeOutput[i] - chunkedOutput[i]) < 1e-6f,
                $"отсчёт {i} разошёлся: {wholeOutput[i]} против {chunkedOutput[i]}");
        }
    }

    [Fact]
    public void Сброс_забывает_прошлый_разговор()
    {
        // Отсчёты прошлого разговора в новом — это чужой голос в первом кадре,
        // и слышит его именно тот, кому не надо.
        Resampler resampler = new(48000, 8000);
        float[] loud = new float[4800];
        Array.Fill(loud, 1f);
        resampler.Process(loud, new float[800]);

        resampler.Reset();

        float[] silence = new float[4800];
        float[] output = new float[800];
        int written = resampler.Process(silence, output);

        for (int i = 0; i < written; i++)
        {
            Assert.True(Math.Abs(output[i]) < 1e-6f, $"отсчёт {i} остался от прошлого: {output[i]}");
        }
    }

    [Fact]
    public void Тесное_место_на_выходе_не_теряет_вход()
    {
        // Вызывающий может дать выходу меньше места, чем нужно. Остаток обязан
        // дождаться следующего вызова, а не пропасть: пропажа здесь — это
        // разрыв волны, то есть щелчок.
        Resampler roomy = new(48000, 8000);
        float[] input = Tone(48000, 1000, seconds: 1);
        float[] expected = new float[9000];
        int expectedCount = roomy.Process(input, expected);

        Resampler tight = new(48000, 8000);
        float[] actual = new float[9000];

        // Весь вход одним куском, но выходу дано место на сотню отсчётов —
        // вшестеро меньше, чем пересчёт готов отдать.
        int actualCount = tight.Process(input, actual.AsSpan(0, 100));

        int written;
        do
        {
            int room = Math.Min(100, actual.Length - actualCount);
            written = room > 0 ? tight.Process([], actual.AsSpan(actualCount, room)) : 0;
            actualCount += written;
        }
        while (written > 0);

        Assert.True(actualCount >= expectedCount - 1, $"потеряно отсчётов: {expectedCount - actualCount}");
        for (int i = 0; i < Math.Min(expectedCount, actualCount); i++)
        {
            Assert.True(
                Math.Abs(expected[i] - actual[i]) < 1e-6f,
                $"отсчёт {i} разошёлся: {expected[i]} против {actual[i]}");
        }
    }

    [Theory]
    [InlineData(1.000179)]
    [InlineData(0.999821)]
    [InlineData(0.995)]
    public void Поправка_темпа_выдаёт_ровно_назначенное_число_отсчётов(double correction)
    {
        // Настоящее расхождение кварцев — десятки ppm. Поправка, которая
        // теряется в округлении на каждом кадре, не делает ничего — так было с
        // первым вариантом поправки (см. W4-AUDIO.md). Здесь шаг дробный и
        // копится в позиции, а значит теряться нечему.
        const int Frames = 1000;
        Resampler resampler = new(8000, 48000, adjustable: true);
        resampler.SetRateCorrection(correction);

        float[] input = new float[160];
        float[] output = new float[2048];
        long total = 0;
        for (int i = 0; i < Frames; i++)
        {
            total += resampler.Process(input, output);
        }

        // Из выхода вычитается задержка ядра: последние её отсчёты ещё ждут
        // правых соседей.
        double expected = ((960.0 * Frames) - (resampler.LatencySamples * 6)) * correction;
        Assert.True(
            Math.Abs(total - expected) <= 2,
            $"выдано {total}, ожидалось {expected:F0} — поправка не дошла до звука");
    }

    [Fact]
    public void Поправка_темпа_не_рвёт_волну()
    {
        // Ради этого поправка и переехала в пересчёт. Прежняя выбрасывала
        // отсчёты с конца каждого кадра, и на поправке в полпроцента это
        // ступенька на каждом шве — пятьдесят щелчков в секунду. Ступенька
        // видна как скачок между соседними отсчётами, которого у гладкого
        // тона быть не может: для 1 кГц на 48 кГц он не больше 2π·1000/48000.
        Resampler resampler = new(8000, 48000, adjustable: true);
        resampler.SetRateCorrection(0.995);

        float[] tone = Tone(8000, 1000, 2);
        float[] output = new float[tone.Length * 7];
        int written = 0;
        for (int offset = 0; offset + 160 <= tone.Length; offset += 160)
        {
            written += resampler.Process(tone.AsSpan(offset, 160), output.AsSpan(written));
        }

        double limit = 2 * Math.PI * 1000 / 48000 * 1.1;
        for (int i = 4800; i < written; i++)
        {
            Assert.True(
                Math.Abs(output[i] - output[i - 1]) <= limit,
                $"скачок {output[i] - output[i - 1]:F3} на отсчёте {i}");
        }
    }

    [Fact]
    public void Подстраиваемый_пересчёт_на_равных_частотах_не_проход_насквозь()
    {
        // Гарнитура в режиме связи и кодек — оба 8 кГц. Проход насквозь
        // растянуть нельзя, поэтому для вывода работает настоящий фильтр, и
        // поправка до звука доходит.
        Resampler resampler = new(8000, 8000, adjustable: true);
        resampler.SetRateCorrection(0.99);

        float[] input = new float[8000];
        float[] output = new float[9000];
        int written = resampler.Process(input, output);

        Assert.True(resampler.LatencySamples > 0);
        Assert.InRange(written, 7880, 7910);
    }

    private static float[] Run(int inputRate, int outputRate, double frequency, int seconds)
    {
        Resampler resampler = new(inputRate, outputRate);
        float[] input = Tone(inputRate, frequency, seconds);
        float[] output = new float[(outputRate * seconds) + 64];
        int written = resampler.Process(input, output);
        return output[..written];
    }

    private static float[] Tone(int rate, double frequency, int seconds)
    {
        float[] samples = new float[rate * seconds];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(2 * Math.PI * frequency * i / rate);
        }

        return samples;
    }

    /// <summary>
    /// Амплитуда на заданной частоте — через свёртку с синусом и косинусом.
    ///
    /// Полного преобразования Фурье здесь не нужно: вопрос всегда про одну
    /// известную частоту, а не про весь спектр.
    /// </summary>
    private static double PeakAmplitude(float[] samples, int rate, double frequency)
    {
        // Начало пропускается: там разгон ядра фильтра.
        int from = Math.Min(rate / 10, samples.Length / 4);
        double real = 0;
        double imaginary = 0;
        int count = samples.Length - from;

        for (int i = from; i < samples.Length; i++)
        {
            double phase = 2 * Math.PI * frequency * i / rate;
            real += samples[i] * Math.Cos(phase);
            imaginary += samples[i] * Math.Sin(phase);
        }

        return 2 * Math.Sqrt((real * real) + (imaginary * imaginary)) / count;
    }
}
