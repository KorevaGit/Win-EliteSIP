using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>Approved champagne operator, drawn as a vector for every DPI.</summary>
internal static class BrandLogo
{
    internal const string SymbolData = "M7 12 L5 5 L11 8 L16 3 L21 8 L27 5 L25 12 M7 16 C7 12 25 12 25 16 L25 22 Q25 28 19 28 H17 M7 16 L7 21 Q7 27 13 29 M4 16 Q1 16 1 20 Q1 24 4 24 M28 16 Q31 16 31 20 Q31 24 28 24";
    private static readonly Lazy<DrawingImage> Source = new(Create);

    private static DrawingImage Create()
    {
        var drawing = new DrawingGroup();
        using (var canvas = drawing.Open())
        {
            canvas.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 32, 32));
            canvas.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(56, 60, 64), Color.FromRgb(35, 38, 42), 90),
                new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 0.35), new Rect(0.4, 0.4, 31.2, 31.2), 7, 7);
            canvas.PushTransform(new TranslateTransform(4, 4));
            canvas.PushTransform(new ScaleTransform(0.75, 0.75));
            canvas.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(216, 198, 165)), 2.3)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            }, Geometry.Parse(SymbolData));
            canvas.Pop();
            canvas.Pop();
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    internal static DrawingImage Image() => Source.Value;

    internal static RenderTargetBitmap Render(int size)
    {
        var visual = new DrawingVisual();
        using (var canvas = visual.RenderOpen()) { canvas.DrawImage(Image(), new Rect(0, 0, size * 4, size * 4)); }
        var large = new RenderTargetBitmap(size * 4, size * 4, 96, 96, PixelFormats.Pbgra32);
        large.Render(visual);
        var final = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(final, BitmapScalingMode.HighQuality);
        using (var canvas = final.RenderOpen()) { canvas.DrawImage(large, new Rect(0, 0, size, size)); }
        var result = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        result.Render(final);
        result.Freeze();
        return result;
    }
}
