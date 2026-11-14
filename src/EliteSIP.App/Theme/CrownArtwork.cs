using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>Контурный оператор в короне; в трее весь символ окрашивается статусом.</summary>
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
                var pen = new Pen(emphasizeStatus ? status : glyph, paths[index].StrokeThickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                drawing.Children.Add(new GeometryDrawing(null, pen, paths[index].Data));
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
