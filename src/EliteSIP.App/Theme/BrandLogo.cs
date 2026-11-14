using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>Векторный золотой знак: один источник для ICO, панели задач и загрузки.</summary>
internal static class BrandLogo
{
    private static readonly Geometry Crown = Geometry.Parse("M112 156 L182 298 L256 133 L330 298 L400 156 L256 263 Z");
    private static readonly Geometry Base = Geometry.Parse("M155 333H233 M279 333H357");

    internal static DrawingImage Image()
    {
        var drawing = new DrawingGroup();
        using (var canvas = drawing.Open())
        {
            // Фиксированный квадрат исключает растягивание широкого знака в Image.
            canvas.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 512, 512));
            var surface = new LinearGradientBrush
            {
                StartPoint = new Point(0.2, 0), EndPoint = new Point(0.8, 1),
                GradientStops =
                [
                    new GradientStop(Color.FromRgb(116, 117, 130), 0),
                    new GradientStop(Color.FromRgb(60, 61, 72), 0.32),
                    new GradientStop(Color.FromRgb(21, 22, 29), 1),
                ],
            };
            canvas.DrawRoundedRectangle(surface, new Pen(new SolidColorBrush(Color.FromRgb(139, 138, 142)), 2),
                new Rect(6, 6, 500, 500), 102, 102);
            canvas.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 11, 11, 15)), 4),
                new Rect(9, 9, 494, 494), 100, 100);
            canvas.PushTransform(new TranslateTransform(-92, -63));
            canvas.PushTransform(new ScaleTransform(1.36, 1.36));
            DrawPart(canvas, Crown, 24);
            DrawPart(canvas, Base, 32);
            canvas.Pop();
            canvas.Pop();
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static void DrawPart(DrawingContext canvas, Geometry geometry, double width)
    {
        var outline = geometry.GetWidenedPathGeometry(new Pen(Brushes.Black, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }).GetOutlinedPathGeometry();
        // Небольшое тёмное ребро снизу и светлая грань сверху дают объём без ореола.
        canvas.PushTransform(new TranslateTransform(0, 1.8));
        canvas.DrawGeometry(new SolidColorBrush(Color.FromArgb(115, 92, 58, 12)), null, outline);
        canvas.Pop();
        var gold = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            [
                new GradientStop(Color.FromRgb(245, 215, 151), 0),
                new GradientStop(Color.FromRgb(223, 182, 98), 0.35),
                new GradientStop(Color.FromRgb(207, 162, 73), 0.65),
                new GradientStop(Color.FromRgb(180, 131, 44), 1),
            ],
        };
        canvas.DrawGeometry(gold, null, outline);
        canvas.PushClip(outline);
        canvas.PushTransform(new TranslateTransform(-1.2, -1.6));
        canvas.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 244, 202)), 1.2), outline);
        canvas.Pop();
        canvas.Pop();
    }

    internal static RenderTargetBitmap Render(int size)
    {
        // Сначала вектор в 4×, затем качественное уменьшение в физический размер.
        var largeVisual = new DrawingVisual();
        using (var canvas = largeVisual.RenderOpen())
        {
            canvas.DrawImage(Image(), new Rect(0, 0, size * 4, size * 4));
        }
        var large = new RenderTargetBitmap(size * 4, size * 4, 96, 96, PixelFormats.Pbgra32);
        large.Render(largeVisual);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var canvas = visual.RenderOpen())
        {
            canvas.DrawImage(large, new Rect(0, 0, size, size));
        }
        var result = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
