using EliteSIP.App.Platform;

namespace EliteSIP.App.Tests;

/// <summary>Система машины, которую видит Spark под версией клиента.</summary>
public sealed class MachinePlatformTests
{
    [Fact]
    public void Название_берётся_из_os_release()
        => Assert.Equal("Linux Mint 22.3", MachinePlatform.PrettyName(
            "NAME=\"Linux Mint\"\nVERSION=\"22.3 (Zena)\"\nPRETTY_NAME=\"Linux Mint 22.3\"\nID=linuxmint\n"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NAME=Debian\nPRETTY_NAME=\"\"\n")]
    public void Без_PRETTY_NAME_названия_нет(string? osRelease)
        => Assert.Null(MachinePlatform.PrettyName(osRelease));

    [Fact]
    public void В_заголовок_уходит_только_печатный_ASCII_до_64_знаков()
    {
        Assert.Equal("Astra Linux 1.8X-Evil: 1", MachinePlatform.HeaderSafe("Astra Linux 1.8\r\nX-Evil: 1"));
        Assert.Equal("Linux  (x64)", MachinePlatform.HeaderSafe("Linux Сборка (x64)\n"));
        Assert.Equal(64, MachinePlatform.HeaderSafe(new string('a', 100)).Length);
    }

    [Fact]
    public void Семейство_этой_сборки_windows()
        => Assert.Equal("windows", MachinePlatform.Family);

    [Theory]
    [InlineData("Windows 10 Pro", "24H2", "26100", "Windows 11 Pro 24H2")]
    [InlineData("Windows 10 Pro", "22H2", "19045", "Windows 10 Pro 22H2")]
    [InlineData("Windows 10 Enterprise LTSC 2021", null, "19044", "Windows 10 Enterprise LTSC 2021")]
    [InlineData(null, "24H2", "26100", null)]
    public void Название_Windows_из_реестра(string? product, string? display, string? build, string? expected)
        => Assert.Equal(expected, MachinePlatform.WindowsName(product, display, build));
}
