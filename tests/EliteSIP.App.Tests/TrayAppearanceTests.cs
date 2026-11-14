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
    public void Контур_короны_не_зависит_от_цвета_полосы(int size) => WpfHost.Run(() =>
    {
        var green = TrayIcon.RenderIcon(Brushes.White, Brushes.Lime, size);
        var red = TrayIcon.RenderIcon(Brushes.White, Brushes.Red, size);
        var a = new byte[size * size * 4];
        var b = new byte[a.Length];
        green.CopyPixels(a, size * 4, 0);
        red.CopyPixels(b, size * 4, 0);
        bool Colored(byte[] pixels, int x, int y, bool isGreen)
        {
            var offset = (y * size + x) * 4;
            return pixels[offset + 3] > 96 && (isGreen
                ? pixels[offset + 1] > pixels[offset + 2] + 32
                : pixels[offset + 2] > pixels[offset + 1] + 32);
        }
        var statusPixels = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (Colored(a, x, y, true))
                {
                    statusPixels++;
                    Assert.True(Colored(b, x, y, false));
                    Assert.True(y >= size * 0.65, "Цвет статуса не должен затрагивать корону");
                }
                var offset = (y * size + x) * 4;
                if (!a.AsSpan(offset, 4).SequenceEqual(b.AsSpan(offset, 4)))
                {
                    Assert.True(y >= size * 0.65);
                    Assert.Equal(a[offset + 3], b[offset + 3]);
                    Assert.True(a[offset + 1] >= a[offset + 2]);
                    Assert.True(b[offset + 2] >= b[offset + 1]);
                }
            }
        }
        Assert.True(statusPixels >= size * size * 0.05, "Статус должен оставаться заметным в малом размере");
        var row = Enumerable.Range(0, size).MaxBy(y => Enumerable.Range(0, size).Count(x => Colored(a, x, y, true)));
        var segments = 0;
        var previous = false;
        for (var x = 0; x < size; x++)
        {
            var current = Colored(a, x, row, true);
            if (current && !previous) { segments++; }
            previous = current;
        }
        Assert.Equal(2, segments);
    });
}
