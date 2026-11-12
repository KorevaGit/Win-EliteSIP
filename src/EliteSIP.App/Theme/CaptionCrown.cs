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
        => new("E", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), fontSize, Brushes.Black, pixelsPerDip);

    private static void Draw(DrawingContext canvas, Brush brush, double width, double height, FontFamily family, double fontSize, double pixelsPerDip)
    {
        var text = Capital(family, fontSize, pixelsPerDip);
        var capital = text.BuildGeometry(new Point()).Bounds;
        var source = (Canvas)Application.Current.FindResource("settings.crown-split");
        var drawing = new DrawingGroup();
        var pen = new Pen(brush, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var paths = source.Children.OfType<System.Windows.Shapes.Path>().ToArray();
        for (var index = 0; index < paths.Length; index++)
        {
            drawing.Children.Add(new GeometryDrawing(null, pen, paths[index].Data));
        }
        var bounds = drawing.Bounds;
        // Верх уже выровнен оптически; добавляем высоту вниз до основания E.
        var scale = (capital.Height + 1.5) / bounds.Height;
        canvas.PushTransform(new TranslateTransform((width - bounds.Width * scale) / 2 - bounds.X * scale,
            (height - text.Height) / 2 + capital.Top - bounds.Y * scale - 1.5));
        canvas.PushTransform(new ScaleTransform(scale, scale));
        var shadow = drawing.CloneCurrentValue();
        foreach (var part in shadow.Children.Cast<GeometryDrawing>())
        {
            part.Pen = new Pen(Brushes.Black, 2.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        }
        canvas.PushOpacity(0.2);
        canvas.PushTransform(new TranslateTransform(0, 0.7 / scale));
        canvas.DrawDrawing(shadow);
        canvas.Pop();
        canvas.Pop();
        canvas.DrawDrawing(drawing);
        canvas.Pop();
        canvas.Pop();
    }
}
