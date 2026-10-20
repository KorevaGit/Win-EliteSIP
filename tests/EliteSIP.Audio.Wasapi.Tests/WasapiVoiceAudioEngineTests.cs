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
/// <b>Что здесь есть.</b> То, что от железа не зависит: поведение
/// незапущенного тракта. Выбор частоты обработки — общий для всех движков и
/// проверяется в <c>EliteSIP.Audio.Tests</c> (<c>ProcessingRateTests</c>);
/// поправка темпа живёт в пересчёте частоты и проверяется там же
/// (<c>ResamplerTests</c>).
/// </summary>
public sealed class WasapiVoiceAudioEngineTests
{
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
