using System.IO;
using EliteSIP.App.Settings;
using EliteSIP.Audio;

namespace EliteSIP.App.Tests;

/// <summary>
/// Однократные правки старых файлов настроек (<c>AppSettings.Upgrade</c>).
/// </summary>
public sealed class SettingsUpgradeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("elitesip-settings-").FullName;

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AppSettings Tinkered(int? revision)
    {
        var settings = new AppSettings { Revision = revision };
        settings.Audio.NoiseSuppression = false;
        settings.Audio.BackgroundVoiceSuppression = false;
        settings.Audio.AutomaticGainControl = true;
        settings.Audio.ReceiveGainControl = true;
        settings.Audio.WindowsProcessing = WindowsCaptureProcessing.On;
        settings.Audio.InputDeviceId = "гарнитура";
        settings.Audio.OutputDeviceId = "наушники";
        settings.Audio.MicrophoneGain = 1.5;
        settings.Audio.PlaybackVolume = 1.8;
        return settings;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Ревизия_2_возвращает_обработку_к_рекомендуемой(int? revision)
    {
        Tinkered(revision).Save(FilePath);

        var loaded = AppSettings.Load(FilePath);

        Assert.True(loaded.Audio.NoiseSuppression);
        Assert.True(loaded.Audio.BackgroundVoiceSuppression);
        Assert.False(loaded.Audio.AutomaticGainControl);
        Assert.False(loaded.Audio.ReceiveGainControl);
        Assert.Equal(WindowsCaptureProcessing.Auto, loaded.Audio.WindowsProcessing);
        Assert.Equal(AppSettings.CurrentRevision, loaded.Revision);
    }

    [Fact]
    public void Устройства_громкость_и_усиление_не_трогаются()
    {
        Tinkered(1).Save(FilePath);

        var loaded = AppSettings.Load(FilePath);

        Assert.Equal("гарнитура", loaded.Audio.InputDeviceId);
        Assert.Equal("наушники", loaded.Audio.OutputDeviceId);
        Assert.Equal(1.5, loaded.Audio.MicrophoneGain);
        Assert.Equal(1.8, loaded.Audio.PlaybackVolume);
    }

    [Fact]
    public void Сброс_однократный_выбор_после_него_остаётся()
    {
        Tinkered(1).Save(FilePath);
        var upgraded = AppSettings.Load(FilePath);

        upgraded.Audio.NoiseSuppression = false;
        upgraded.Audio.WindowsProcessing = WindowsCaptureProcessing.Off;
        upgraded.Save(FilePath);

        var again = AppSettings.Load(FilePath);

        Assert.False(again.Audio.NoiseSuppression);
        Assert.Equal(WindowsCaptureProcessing.Off, again.Audio.WindowsProcessing);
    }

    [Fact]
    public void Новая_машина_сразу_на_текущей_ревизии()
    {
        // Иначе её файл ушёл бы на диск без ревизии, и правки сработали бы
        // на следующем запуске поверх выбора первого дня.
        var fresh = AppSettings.Load(FilePath);
        fresh.Audio.NoiseSuppression = false;
        fresh.Save(FilePath);

        var again = AppSettings.Load(FilePath);

        Assert.Equal(AppSettings.CurrentRevision, fresh.Revision);
        Assert.False(again.Audio.NoiseSuppression);
    }
}
