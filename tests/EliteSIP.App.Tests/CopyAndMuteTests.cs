using System.IO;
using EliteSIP.App.Incoming;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Tests;

/// <summary>Копирование номера из окна входящего и микрофон, заглушённый до звонка.</summary>
public sealed class CopyAndMuteTests : IDisposable
{
    private const string Own = "176";
    private const string Mobile = "89181234567";

    private readonly string _directory = Directory.CreateTempSubdirectory("elitesip-mute-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Открытый_номер_копируется()
        => Assert.Equal(Mobile, IncomingCallSubject.Classify(Mobile, null, false, Own, masksMobileNumbers: false).CopyableNumber);

    [Fact]
    public void Городской_номер_копируется_и_под_маской()
        => Assert.Equal("84951234567", IncomingCallSubject.Classify("84951234567", "Иванов", false, Own, masksMobileNumbers: true).CopyableNumber);

    [Fact]
    public void Номер_под_маской_не_копируется()
        => Assert.Null(IncomingCallSubject.Classify(Mobile, null, false, Own, masksMobileNumbers: true).CopyableNumber);

    [Fact]
    public void Звонок_по_сделке_номер_не_копирует()
        => Assert.Null(IncomingCallSubject.Classify(Own, null, false, Own, masksMobileNumbers: false).CopyableNumber);

    [Fact]
    public void Заглушённый_микрофон_переживает_перезапуск()
    {
        var path = Path.Combine(_directory, "settings.json");
        var settings = new AppSettings();
        settings.Audio.MicrophoneMuted = true;
        settings.Save(path);

        Assert.True(AppSettings.Load(path).Audio.MicrophoneMuted);
    }

    [Fact]
    public void По_умолчанию_микрофон_включён()
        => Assert.False(new AppSettings().Audio.MicrophoneMuted);
}
