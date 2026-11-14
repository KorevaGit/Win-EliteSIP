using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>Общая контурная корона для трея и штатных заголовков окон.</summary>
internal static class CrownArtwork
{
    internal static RenderTargetBitmap Render(Brush glyph, Brush status, int size, bool emphasizeStatus)
    {
        var source = (Canvas)Application.Current.FindResource("settings.crown-split");
        var paths = source.Children.OfType<System.Windows.Shapes.Path>().ToArray();
        var visual = new DrawingVisual();
        using (var canvas = visual.RenderOpen())
        {
            var drawing = new DrawingGroup();
            for (var index = 0; index < paths.Length; index++)
            {
                var isBase = index == paths.Length - 1;
                var pen = new Pen(isBase ? status : glyph, paths[index].StrokeThickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                if (isBase && emphasizeStatus)
                {
                    // На 16 px каждая половина статуса остаётся почти 4 px высотой.
                    drawing.Children.Add(new GeometryDrawing(status, null,
                        new RectangleGeometry(new Rect(2.3, 19, 8.1, 5.5), 1.2, 1.2)));
                    drawing.Children.Add(new GeometryDrawing(status, null,
                        new RectangleGeometry(new Rect(13.6, 19, 8.1, 5.5), 1.2, 1.2)));
                }
                else
                {
                    drawing.Children.Add(new GeometryDrawing(null, pen, paths[index].Data));
                }
            }
            var bounds = drawing.Bounds;
            var scale = (size - 1d) / Math.Max(bounds.Width, bounds.Height);
            canvas.PushTransform(new TranslateTransform((size - bounds.Width * scale) / 2 - bounds.X * scale,
                (size - bounds.Height * scale) / 2 - bounds.Y * scale));
            canvas.PushTransform(new ScaleTransform(scale, scale));
            canvas.DrawDrawing(drawing);
            canvas.Pop();
            canvas.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
}
