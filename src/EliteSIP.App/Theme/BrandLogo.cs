using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>Векторный золотой знак: один источник для ICO, панели задач и загрузки.</summary>
internal static class BrandLogo
{
    private static readonly Geometry Crown = Geometry.Parse("M112 156 L182 298 L256 133 L330 298 L400 156 L256 263 Z");
    private static readonly Geometry NumberField = Geometry.Parse("M182 344H330");
    // Дополнительный просвет остаётся различимым и после уменьшения до 16–32 px.
    private static readonly Geometry Actions = Geometry.Parse("M182 378H252 M312 378H330");

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
                    new GradientStop(Color.FromRgb(52, 52, 62), 0),
                    new GradientStop(Color.FromRgb(37, 37, 46), 0.5),
                    new GradientStop(Color.FromRgb(24, 24, 30), 1),
                ],
            };
            canvas.DrawRoundedRectangle(surface, null, new Rect(8, 8, 496, 496), 106, 106);
            // Единый масштаб сохраняет исходные пропорции короны; композиция центрирована.
            canvas.PushTransform(new TranslateTransform(-76.8, -76.8));
            canvas.PushTransform(new ScaleTransform(1.3, 1.3));
            DrawPart(canvas, Crown, 18);
            DrawPart(canvas, NumberField, 22);
            DrawPart(canvas, Actions, 22);
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
