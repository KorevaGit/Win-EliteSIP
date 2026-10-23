using System.IO;
using EliteSIP.App.Settings;
using EliteSIP.CallGuard;

namespace EliteSIP.App.Tests;

/// <summary>Место карточки входящего в файле настроек.</summary>
public sealed class IncomingPlacementSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("elitesip-placement-").FullName;

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Место_переживает_перезапуск()
    {
        var settings = new AppSettings();
        settings.IncomingPlacement.Origin = new ScreenPoint(1930, 412);
        settings.Save(FilePath);

        Assert.Equal(new ScreenPoint(1930, 412), AppSettings.Load(FilePath).IncomingPlacement.Origin);
    }

    [Fact]
    public void Старый_файл_без_раздела_читается_без_места()
    {
        File.WriteAllText(FilePath, "{}");

        Assert.Null(AppSettings.Load(FilePath).IncomingPlacement.Origin);
    }

    [Fact]
    public void Нечисловая_точка_не_запоминается()
    {
        var placement = new IncomingWindowPlacementSettings { Origin = new ScreenPoint(double.NaN, 10) };

        Assert.Null(placement.Origin);
    }
}
