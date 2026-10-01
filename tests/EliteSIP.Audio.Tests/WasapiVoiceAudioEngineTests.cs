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
/// обработки и поведение незапущенного тракта. Поправка темпа живёт в
/// пересчёте частоты и проверяется там (<see cref="ResamplerTests"/>).
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
