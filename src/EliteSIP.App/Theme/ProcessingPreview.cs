using System.Windows;
using System.Windows.Media;

namespace EliteSIP.App.Theme;

/// <summary>Схематический эффект обработки; это иллюстрация, а не измеритель сигнала.</summary>
public sealed class ProcessingPreview : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(ProcessingPreview), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProcessedBrushProperty = DependencyProperty.Register(
        nameof(ProcessedBrush), typeof(Brush), typeof(ProcessingPreview), new FrameworkPropertyMetadata(Brushes.Green, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ProcessingPreview), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(ProcessingPreview), new FrameworkPropertyMetadata("Level", FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush ProcessedBrush { get => (Brush)GetValue(ProcessedBrushProperty); set => SetValue(ProcessedBrushProperty, value); }

    public ProcessingPreview()
    {
        Width = 102;
        Height = 24;
        IsHitTestVisible = false;
        SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        SetResourceReference(ProcessedBrushProperty, "OutcomeAnsweredBrush");
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var secondary = Foreground;
        var active = IsActive ? ProcessedBrush : secondary;
        var pen = new Pen(secondary, 1);
        drawingContext.DrawLine(pen, new Point(44, 12), new Point(56, 12));
        drawingContext.DrawLine(pen, new Point(52, 8), new Point(56, 12));
        drawingContext.DrawLine(pen, new Point(52, 16), new Point(56, 12));
        if (Kind == "Voices")
        {
            for (var side = 0; side < 2; side++)
            {
                for (var person = 0; person < 3; person++)
                {
                    drawingContext.PushOpacity(side == 1 && IsActive && person != 1 ? 0.15 : 1);
                    var x = side * 64 + person * 11 + 5;
                    var brush = side == 0 ? secondary : active;
                    drawingContext.DrawEllipse(brush, null, new Point(x, 7), 3, 3);
                    drawingContext.DrawRoundedRectangle(brush, null, new Rect(x - 4, 12, 8, 9), 3, 3);
                    drawingContext.Pop();
                }
            }
            return;
        }
        double[] raw = Kind == "Noise" ? [7, 5, 21, 8, 6] : [5, 21, 9, 18, 6];
        for (var index = 0; index < raw.Length; index++)
        {
            var before = raw[index];
            var after = !IsActive ? before : Kind == "Noise" ? index == 2 ? before : 2 : 14 + index % 2 * 2;
            drawingContext.DrawRoundedRectangle(secondary, null, new Rect(index * 7, (24 - before) / 2, 4, before), 1, 1);
            drawingContext.DrawRoundedRectangle(active, null, new Rect(64 + index * 7, (24 - after) / 2, 4, after), 1, 1);
        }
    }
}
