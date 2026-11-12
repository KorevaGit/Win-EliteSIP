using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>Корона в границах заглавной буквы шрифта заголовка.</summary>
public sealed class CaptionCrown : Control
{
    public CaptionCrown()
    {
        Width = 13;
        Focusable = false;
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size constraint)
        => new(13, Capital(FontFamily, FontSize, VisualTreeHelper.GetDpi(this).PixelsPerDip).Height);

    protected override void OnRender(DrawingContext drawingContext)
        => Draw(drawingContext, Foreground, ActualWidth, ActualHeight, FontFamily, FontSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    internal static RenderTargetBitmap NativeIcon(Brush brush)
    {
        var visual = new DrawingVisual();
        using (var canvas = visual.RenderOpen())
        {
            canvas.PushTransform(new ScaleTransform(2, 2));
            Draw(canvas, brush, 16, 16, SystemFonts.CaptionFontFamily, SystemFonts.CaptionFontSize, 1);
            canvas.Pop();
        }
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static FormattedText Capital(FontFamily family, double fontSize, double pixelsPerDip)
        => new("H", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), fontSize, Brushes.Black, pixelsPerDip);

    private static void Draw(DrawingContext canvas, Brush brush, double width, double height, FontFamily family, double fontSize, double pixelsPerDip)
    {
        var text = Capital(family, fontSize, pixelsPerDip);
        var capital = text.BuildGeometry(new Point()).Bounds;
        var source = (Canvas)Application.Current.FindResource("settings.crown");
        var drawing = new DrawingGroup();
        var pen = new Pen(brush, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var path in source.Children.OfType<System.Windows.Shapes.Path>())
        {
            drawing.Children.Add(new GeometryDrawing(null, pen, path.Data));
        }
        var bounds = drawing.Bounds;
        var scale = capital.Height / bounds.Height;
        canvas.PushTransform(new TranslateTransform((width - bounds.Width * scale) / 2 - bounds.X * scale,
            (height - text.Height) / 2 + capital.Top - bounds.Y * scale));
        canvas.PushTransform(new ScaleTransform(scale, scale));
        canvas.DrawDrawing(drawing);
        canvas.Pop();
        canvas.Pop();
    }
}
