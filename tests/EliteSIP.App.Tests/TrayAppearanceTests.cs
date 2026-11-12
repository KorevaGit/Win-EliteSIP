using System.Windows.Media;
using System.Windows;
using System.Globalization;
using EliteSIP.App.Theme;
using EliteSIP.App.Shell;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class TrayAppearanceTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(14)]
    public void Корона_совпадает_по_высоте_с_заглавной_буквой(int fontSize) => WpfHost.Run(() =>
    {
        var family = new FontFamily("Segoe UI");
        var crown = new CaptionCrown { FontFamily = family, FontSize = fontSize, Foreground = Brushes.White };
        crown.Measure(new Size(100, 100));
        crown.Arrange(new Rect(new Point(), crown.DesiredSize));
        crown.UpdateLayout();
        var width = (int)Math.Ceiling(crown.ActualWidth);
        var height = (int)Math.Ceiling(crown.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(crown);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var rows = Enumerable.Range(0, height).Where(y => Enumerable.Range(0, width).Any(x => pixels[(y * width + x) * 4 + 3] > 128)).ToArray();
        Assert.NotEmpty(rows);
        var text = new FormattedText("H", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), fontSize, Brushes.White, 1);
        var capital = text.BuildGeometry(new Point()).Bounds;
        capital.Offset(0, -1.5);
        Assert.InRange(Math.Abs(rows[0] - capital.Top), 0, 1);
        Assert.InRange(Math.Abs(rows[^1] + 1 - capital.Bottom), 0, 1);
    });

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
        var line = ((int)(size * 21d / 24) * size + (int)(size * 0.35)) * 4;
        Assert.True(a[line + 1] > a[line + 2]);
        Assert.True(b[line + 2] > b[line + 1]);
        var body = size * (int)(size * 18d / 24) * 4;
        Assert.Equal(a.AsSpan(0, body).ToArray(), b.AsSpan(0, body).ToArray());
        var gap = ((int)(size * 21d / 24) * size + size / 2) * 4;
        Assert.True(a[gap + 3] < 96);
        var centre = (size / 2 * size + size / 2) * 4;
        Assert.Equal(0, a[centre + 3]);
    });
}
