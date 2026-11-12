using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EliteSIP.App.Resources;

namespace EliteSIP.App.Theme;

/// <summary>Показывает выбранный маршрут, не выдавая схему за проверку соединения.</summary>
public sealed class ConnectionPreview : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(ConnectionPreview), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsRemoteProperty = DependencyProperty.Register(
        nameof(IsRemote), typeof(bool), typeof(ConnectionPreview), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsRemote { get => (bool)GetValue(IsRemoteProperty); set => SetValue(IsRemoteProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public ConnectionPreview()
    {
        Width = 270;
        Height = 64;
        HorizontalAlignment = HorizontalAlignment.Center;
        IsHitTestVisible = false;
        SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var ink = Foreground;
        var pen = new Pen(ink, 1.4);
        for (var node = 0; node < 3; node++)
        {
            var x = 30 + node * 100;
            if (node == 0)
            {
                drawingContext.DrawRoundedRectangle(null, pen, new Rect(x - 13, 7, 26, 18), 2, 2);
                drawingContext.DrawLine(pen, new Point(x, 26), new Point(x, 30));
                drawingContext.DrawLine(pen, new Point(x - 8, 31), new Point(x + 8, 31));
            }
            else if (node == 1 && IsRemote)
            {
                drawingContext.DrawGeometry(null, pen, Geometry.Parse($"M{x - 14} 17L{x} 5L{x + 14} 17M{x - 10} 14V31H{x + 10}V14"));
            }
            else
            {
                drawingContext.DrawRoundedRectangle(null, pen, new Rect(x - 11, 5, 22, 26), 2, 2);
                for (var line = 0; line < 3; line++)
                {
                    drawingContext.DrawLine(pen, new Point(x - 6, 11 + line * 7), new Point(x + 6, 11 + line * 7));
                }
            }
            var title = node == 0 ? Strings.Get("SettingsThisPc") : node == 1 ? Strings.Get(IsRemote ? "WorkRemote" : "WorkOffice") : Strings.Get("SettingsPbx");
            var text = new FormattedText(title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(text, new Point(x - text.Width / 2, 40));
            if (node < 2)
            {
                drawingContext.DrawLine(pen, new Point(x + 25, 18), new Point(x + 69, 18));
                drawingContext.DrawLine(pen, new Point(x + 65, 14), new Point(x + 69, 18));
                drawingContext.DrawLine(pen, new Point(x + 65, 22), new Point(x + 69, 18));
            }
        }
    }
}
