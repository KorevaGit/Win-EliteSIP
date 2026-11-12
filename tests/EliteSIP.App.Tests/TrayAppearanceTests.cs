using System.Windows.Media;
using EliteSIP.App.Shell;

namespace EliteSIP.App.Tests;

public sealed class TrayAppearanceTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void Контур_короны_не_зависит_от_цвета_полосы(int size) => WpfHost.Run(() =>
    {
        var green = TrayIcon.RenderIcon(Brushes.White, Brushes.Lime, size);
        var red = TrayIcon.RenderIcon(Brushes.White, Brushes.Red, size);
        var a = new byte[size * size * 4];
        var b = new byte[a.Length];
        green.CopyPixels(a, size * 4, 0);
        red.CopyPixels(b, size * 4, 0);
        var line = ((int)(size * 21d / 24) * size + size / 2) * 4;
        Assert.True(a[line + 1] > a[line + 2]);
        Assert.True(b[line + 2] > b[line + 1]);
        var body = size * (int)(size * 18d / 24) * 4;
        Assert.Equal(a.AsSpan(0, body).ToArray(), b.AsSpan(0, body).ToArray());
        var centre = (size / 2 * size + size / 2) * 4;
        Assert.Equal(0, a[centre + 3]);
    });
}
