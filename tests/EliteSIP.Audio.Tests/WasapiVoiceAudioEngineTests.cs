using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Тракт на WASAPI.
///
/// <b>Чего здесь нет.</b> Ни одной проверки, которая открывает устройство:
/// в CI звуковых устройств нет вовсе, а на машине разработки они есть, и один и
/// тот же тест давал бы там и там противоположные ответы. Настоящая приёмка
/// тракта — прогон на живом железе, и она сделана: матрица устройств, баланс
/// отсчётов и разбор задержки печатаются самим трактом.
///
/// <b>Что здесь есть.</b> То, что от железа не зависит: выбор частоты
/// обработки и поправка темпа. Второе — не про полноту, а про конкретный
/// дефект: первый вариант поправки округлял её на каждом куске и потому не
/// делал ничего вовсе. Прогон на живом железе показал в журнале честные
/// 179 ppm компенсации, которой не было.
/// </summary>
public sealed class WasapiVoiceAudioEngineTests
{
    [Theory]
    [InlineData(8000, 8000)]
    [InlineData(16000, 16000)]
    [InlineData(32000, 32000)]
    [InlineData(48000, 48000)]
    public void Поддерживаемая_частота_остаётся_собой(int deviceRate, int expected)
    {
        // Гарнитура в режиме связи работает на 8 кГц. Обрабатывать её на 48
        // значило бы трижды пересчитать то, чего в сигнале нет.
        Assert.Equal(expected, VoiceProcessor.NearestSupportedRate(deviceRate));
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(22050, 32000)]
    [InlineData(11025, 16000)]
    [InlineData(96000, 48000)]
    public void Неподдерживаемая_частота_поднимается_до_ближайшей(int deviceRate, int expected)
    {
        // Вверх, а не вниз: понижение резало бы полосу до обработки, то есть
        // выбрасывало бы часть разговора ещё до эхоподавителя.
        Assert.Equal(expected, VoiceProcessor.NearestSupportedRate(deviceRate));
    }

    [Fact]
    public void Поправка_темпа_накапливается_а_не_теряется_в_округлении()
    {
        // Главная проверка файла, и она написана по следам дефекта. Кадр — 960
        // отсчётов, поправка — 179 миллионных: произведение 960,17 округляется
        // обратно в 960, и без переноса остатка компенсация не делает ничего.
        // Настоящее расхождение кварцев — десятки ppm, то есть терялась бы
        // ЛЮБАЯ поправка меньше тысячи ppm.
        const double Correction = 1.000179;
        const int ChunkSamples = 960;
        const int Chunks = 1000;

        float[] buffer = new float[ChunkSamples + 16];
        double carry = 0;
        long total = 0;

        for (int i = 0; i < Chunks; i++)
        {
            total += WasapiVoiceAudioEngine.ApplyRateCorrection(
                buffer,
                ChunkSamples,
                Correction,
                ref carry);
        }

        long expected = (long)Math.Round(ChunkSamples * Chunks * Correction);
        Assert.True(
            Math.Abs(total - expected) <= 1,
            $"выдано {total}, ожидалось {expected} — поправка потерялась в округлении");
    }

    [Fact]
    public void Отрицательная_поправка_укорачивает_ровно_настолько_же()
    {
        const double Correction = 0.999821;
        const int ChunkSamples = 960;
        const int Chunks = 1000;

        float[] buffer = new float[ChunkSamples + 16];
        double carry = 0;
        long total = 0;

        for (int i = 0; i < Chunks; i++)
        {
            total += WasapiVoiceAudioEngine.ApplyRateCorrection(
                buffer,
                ChunkSamples,
                Correction,
                ref carry);
        }

        long expected = (long)Math.Round(ChunkSamples * Chunks * Correction);
        Assert.True(
            Math.Abs(total - expected) <= 1,
            $"выдано {total}, ожидалось {expected}");
    }

    [Fact]
    public void Единичная_поправка_не_трогает_кусок()
    {
        float[] buffer = new float[976];
        double carry = 0;

        Assert.Equal(960, WasapiVoiceAudioEngine.ApplyRateCorrection(buffer, 960, 1.0, ref carry));
        Assert.Equal(0, carry);
    }

    [Fact]
    public void Добавленный_отсчёт_повторяет_последний_а_не_обнуляет()
    {
        // Ноль в середине волны — это щелчок. Повтор последнего отсчёта на
        // такой доле процента не слышен вовсе.
        float[] buffer = new float[8];
        buffer[3] = 0.5f;
        double carry = 0.99;

        int written = WasapiVoiceAudioEngine.ApplyRateCorrection(buffer, 4, 1.01, ref carry);

        Assert.Equal(5, written);
        Assert.Equal(0.5f, buffer[4]);
    }

    [Fact]
    public void Кусок_не_укорачивается_в_ничто()
    {
        // Пустой кусок — это провал в звуке. Даже при упёршейся в предел
        // поправке должен остаться хотя бы один отсчёт.
        float[] buffer = new float[8];
        double carry = -100;

        Assert.Equal(1, WasapiVoiceAudioEngine.ApplyRateCorrection(buffer, 4, 1.0, ref carry));
    }

    [Fact]
    public void Незапущенный_тракт_так_и_говорит()
    {
        using WasapiVoiceAudioEngine engine = new(new VoiceAudioConfiguration());

        Assert.Equal("тракт не запущен", engine.Summary());
        Assert.Null(engine.Balance);
        Assert.Equal(DeviceActivity.Warmup, engine.CaptureActivity);
    }

    [Fact]
    public void Остановка_незапущенного_проходит_вхолостую()
    {
        // Сюда приходят и по отбою, и запоздавшим путём — то же требование,
        // что и у шины.
        using WasapiVoiceAudioEngine engine = new(new VoiceAudioConfiguration());
        engine.Stop();
        engine.Stop();
    }

    [Fact]
    public void Перестройка_остановленного_тракта_разрешена()
    {
        using WasapiVoiceAudioEngine engine = new(new VoiceAudioConfiguration());
        engine.Reconfigure(new VoiceAudioConfiguration { MicrophoneGain = 1.5f });
    }

    [Fact]
    public void Повторное_освобождение_проходит_вхолостую()
    {
        WasapiVoiceAudioEngine engine = new(new VoiceAudioConfiguration());
        engine.Dispose();
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() => engine.Start());
    }
}
