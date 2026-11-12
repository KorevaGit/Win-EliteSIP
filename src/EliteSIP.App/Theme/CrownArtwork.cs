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
        var source = (Canvas)Application.Current.FindResource("settings.crown");
        var paths = source.Children.OfType<System.Windows.Shapes.Path>().ToArray();
        var visual = new DrawingVisual();
        using (var canvas = visual.RenderOpen())
        {
            canvas.PushTransform(new TranslateTransform(-size * 0.03, -size * 0.03));
            canvas.PushTransform(new ScaleTransform(size * 1.06 / 24, size * 1.06 / 24));
            for (var index = 0; index < paths.Length; index++)
            {
                var isBase = index == paths.Length - 1;
                var pen = new Pen(isBase ? status : glyph, 2.2)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                if (isBase && emphasizeStatus)
                {
                    // Края совпадают с основанием контура (x=5.81..18.19
                    // плюс половина толщины), а верх примыкает к его низу.
                    canvas.DrawRoundedRectangle(status, null, new Rect(4.7, 18.1, 14.6, 5), 2, 2);
                }
                else
                {
                    canvas.DrawGeometry(null, pen, paths[index].Data);
                }
            }
            canvas.Pop();
            canvas.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
}
