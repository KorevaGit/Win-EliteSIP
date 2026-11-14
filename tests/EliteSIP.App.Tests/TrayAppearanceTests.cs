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
        var text = new FormattedText("E", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), fontSize, Brushes.White, 1);
        var capital = text.BuildGeometry(new Point()).Bounds;
        Assert.InRange(Math.Abs(rows[0] - (capital.Top - 1.5)), 0, 1);
        Assert.InRange(Math.Abs(rows[^1] + 1 - capital.Bottom), 0, 1);
    });

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void Весь_символ_трея_окрашен_статусом_без_подложки(int size) => WpfHost.Run(() =>
    {
        var green = TrayIcon.RenderIcon(Brushes.White, Brushes.Lime, size);
        var red = TrayIcon.RenderIcon(Brushes.White, Brushes.Red, size);
        var a = new byte[size * size * 4];
        var b = new byte[a.Length];
        green.CopyPixels(a, size * 4, 0);
        red.CopyPixels(b, size * 4, 0);
        var visible = 0;
        var transparent = 0;
        for (var offset = 0; offset < a.Length; offset += 4)
        {
            Assert.Equal(a[offset + 3], b[offset + 3]);
            if (a[offset + 3] == 0) { transparent++; continue; }
            visible++;
            Assert.Equal(0, a[offset]);
            Assert.Equal(0, a[offset + 2]);
            Assert.Equal(a[offset + 3], a[offset + 1]);
            Assert.Equal(0, b[offset]);
            Assert.Equal(0, b[offset + 1]);
            Assert.Equal(b[offset + 3], b[offset + 2]);
        }
        Assert.True(visible > size * size * 0.2);
        Assert.True(transparent > size * size * 0.3);
    });
}