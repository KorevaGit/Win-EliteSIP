namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Измерение задержки тракта по огибающей.
///
/// Своего оригинала у этого нет: на macOS задержку никто не мерил — там весь
/// тракт держала система, и складывать было нечего. Здесь буферов три (захват,
/// джиттер, воспроизведение), и сумма их счётчиков — это наша же оценка самих
/// себя. Проверять её надо звуком, прошедшим весь путь.
/// </summary>
public sealed class AudioDelayEstimatorTests
{
    private const int SampleRate = 8000;

    /// <summary>Речеподобный сигнал: слоги по 200 мс с паузами, разной громкости.</summary>
    private static short[] MakeSpeech(int milliseconds, int seed)
    {
        Random random = new(seed);
        short[] samples = new short[SampleRate * milliseconds / 1000];
        int syllable = SampleRate / 5;

        for (int index = 0; index < samples.Length; index++)
        {
            bool voiced = index / syllable % 2 == 0;
            double envelope = voiced ? 0.2 + (0.8 * ((index / syllable % 5) / 5.0)) : 0.02;
            samples[index] = (short)(random.Next(-8000, 8000) * envelope);
        }

        return samples;
    }

    private static float[] Envelope(ReadOnlySpan<short> samples) =>
        AudioDelayEstimator.BuildEnvelope(samples, AudioDelayEstimator.SamplesPerStep(SampleRate));

    [Fact]
    public void Находит_известную_задержку_с_точностью_до_шага()
    {
        short[] sent = MakeSpeech(6000, seed: 17);

        // 120 мс — правдоподобная задержка разговорного тракта: запас буфера,
        // кольцо и сеть вместе.
        int delaySamples = SampleRate * 120 / 1000;
        short[] received = new short[sent.Length];
        Array.Copy(sent, 0, received, delaySamples, sent.Length - delaySamples);

        AudioDelayEstimate estimate = Assert.IsType<AudioDelayEstimate>(
            AudioDelayEstimator.Estimate(Envelope(sent), Envelope(received)));

        Assert.Equal(120, estimate.Milliseconds, tolerance: AudioDelayEstimator.DefaultStepMilliseconds);
        Assert.True(estimate.Confidence > 0.9, $"пик {estimate.Confidence:F2} — совпадение обязано быть уверенным");
    }

    [Fact]
    public void Переживает_потерю_громкости_и_обрезку_кодеком()
    {
        // Через G.711 и АРУ сервера сигнал приходит другим по форме и по
        // уровню. Совпадать должна громкость, а не волна — ради этого меряется
        // огибающая, а не отсчёты.
        short[] sent = MakeSpeech(6000, seed: 4);
        int delaySamples = SampleRate * 80 / 1000;

        short[] received = new short[sent.Length];
        for (int index = delaySamples; index < sent.Length; index++)
        {
            short encoded = G711.DecodeMuLaw(G711.EncodeMuLaw(sent[index - delaySamples]));
            received[index] = (short)(encoded * 0.35);
        }

        AudioDelayEstimate estimate = Assert.IsType<AudioDelayEstimate>(
            AudioDelayEstimator.Estimate(Envelope(sent), Envelope(received)));

        Assert.Equal(80, estimate.Milliseconds, tolerance: AudioDelayEstimator.DefaultStepMilliseconds);
    }

    [Fact]
    public void Находит_сдвиг_и_тогда_когда_принятому_недостаёт_начала()
    {
        // Так выглядит эхо-номер лаборатории: сервер отвечает, выжидает секунду
        // (Wait(1) в плане набора) и только потом начинает возвращать звук. Всё
        // это время наш сигнал уходит в никуда, и в принятом недостаёт ровно
        // секунды начала — а значит, совпадение лежит на ОТРИЦАТЕЛЬНОМ сдвиге
        // номеров, хотя во времени звук, разумеется, отстаёт.
        //
        // Поиск только вперёд давал на живом прогоне пик 0,10 и число 810 мс
        // при настоящих 137: искал не там и находил случайное совпадение.
        short[] sent = MakeSpeech(8000, seed: 33);
        int missing = SampleRate * 1000 / 1000;

        short[] received = new short[sent.Length - missing];
        Array.Copy(sent, missing, received, 0, received.Length);

        AudioDelayEstimate estimate = Assert.IsType<AudioDelayEstimate>(
            AudioDelayEstimator.Estimate(Envelope(sent), Envelope(received)));

        Assert.True(estimate.IsConfident, $"пик {estimate.Confidence:F2}");
        Assert.Equal(-1000, estimate.Milliseconds, tolerance: AudioDelayEstimator.DefaultStepMilliseconds);
    }

    [Fact]
    public void На_чужом_сигнале_молчит_а_не_выдумывает_число()
    {
        // Отказ полезнее неправды: задержку, найденную по шуму, невозможно
        // отличить от настоящей, и один такой ответ обесценивает отчёт целиком.
        //
        // Ровный шум без слогов — это и есть «в линии есть звук, но не наш»:
        // фон помещения, музыка ожидания, чужой разговор на общем порту.
        short[] sent = MakeSpeech(6000, seed: 1);

        Random random = new(999);
        short[] noise = new short[sent.Length];
        for (int index = 0; index < noise.Length; index++)
        {
            noise[index] = (short)random.Next(-6000, 6000);
        }

        AudioDelayEstimate? found = AudioDelayEstimator.Estimate(Envelope(sent), Envelope(noise));
        Assert.False(found?.IsConfident, $"пик {found?.Confidence:F2} на чужом сигнале");
    }

    [Fact]
    public void На_тишине_молчит()
    {
        short[] sent = MakeSpeech(4000, seed: 5);
        short[] silence = new short[SampleRate * 4];

        AudioDelayEstimate? found = AudioDelayEstimator.Estimate(Envelope(sent), Envelope(silence));
        Assert.False(found?.IsConfident, $"пик {found?.Confidence:F2} на тишине");
    }

    [Fact]
    public void Огибающая_считается_по_шагу_а_не_по_кадру()
    {
        // Шаг — это разрешение измерения. Кадр в двадцать миллисекунд дал бы
        // разрешение, при котором «шестьдесят» и «восемьдесят» неразличимы, а
        // это ровно та разница, ради которой всё и меряется.
        Assert.Equal(40, AudioDelayEstimator.SamplesPerStep(SampleRate));

        short[] samples = new short[SampleRate / 10];
        Array.Fill(samples, (short)1000);

        float[] envelope = Envelope(samples);
        Assert.Equal(20, envelope.Length);
        Assert.All(envelope, value => Assert.Equal(1000, value, tolerance: 1));
    }
}
