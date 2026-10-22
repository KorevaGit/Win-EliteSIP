namespace EliteSIP.PanelLink.Tests;

/// <summary>
/// Раскладка выпусков на канале. Пути — часть договора с сервером
/// <c>elitesip_update</c> и со Spark: поменять их здесь значит поменять там.
/// </summary>
public sealed class ReleaseLineTests
{
    [Fact]
    public void Windows_остаётся_на_прежних_путях()
    {
        Assert.Equal("releases/current.json", ReleaseLine.Windows.Manifest);
        Assert.Equal("EliteSIP-0.1.78.exe", ReleaseLine.Windows.FileName(new Version(0, 1, 78)));
        Assert.Equal("EliteSIP-*.exe", ReleaseLine.Windows.FilePattern);
    }

    [Fact]
    public void Linux_в_своём_каталоге()
    {
        Assert.Equal("releases/linux/current.json", ReleaseLine.Linux.Manifest);
        Assert.Equal("EliteSIP-0.1.0.deb", ReleaseLine.Linux.FileName(new Version(0, 1, 0, 7)));
        Assert.Equal("EliteSIP-*.deb", ReleaseLine.Linux.FilePattern);
    }

    [Fact]
    public void Линия_сборки_по_ОС()
    {
        Assert.Equal(OperatingSystem.IsLinux() ? ReleaseLine.Linux : ReleaseLine.Windows, ReleaseLine.Current);
    }

    [Theory]
    [InlineData("/var/lib/elitesip/updates/EliteSIP-0.1.5.deb", "0.1.5")]
    [InlineData("EliteSIP-1.2.3.deb", "1.2.3")]
    [InlineData("EliteSIP-1.2.3.exe", null)]
    [InlineData("EliteSIP-x.deb", null)]
    [InlineData("other-1.2.3.deb", null)]
    [InlineData("EliteSIP-1.2.3.deb.part", null)]
    public void Версия_из_имени_только_своего_файла(string path, string? expected)
    {
        Assert.Equal(expected is null ? null : Version.Parse(expected), ReleaseLine.Linux.VersionOf(path));
    }
}
