using System.Windows;
using System.Windows.Media;

namespace EliteSIP.App.Theme;

/// <summary>История реального уровня самопроверки, без демонстрационных данных.</summary>
public sealed class RecordingWaveform : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(RecordingWaveform), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnLevelChanged));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(RecordingWaveform), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RecordingWaveform), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly Queue<double> _levels = new();

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public RecordingWaveform()
    {
        Height = 25;
        MinWidth = 60;
    }

    private static void OnLevelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (RecordingWaveform)sender;
        control._levels.Enqueue(Math.Sqrt(Math.Clamp((double)e.NewValue, 0, 1)));
        while (control._levels.Count > 24)
        {
            _ = control._levels.Dequeue();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var count = Math.Min(24, Math.Max(1, (int)(ActualWidth / 5)));
        var levels = _levels.ToArray();
        for (var index = 0; index < count; index++)
        {
            var sample = index - count + levels.Length;
            var level = sample >= 0 ? levels[sample] : 0;
            var height = Math.Max(3, level * ActualHeight);
            drawingContext.DrawRoundedRectangle(level > 0 ? Foreground : TrackBrush, null,
                new Rect(index * 5, (ActualHeight - height) / 2, 3, height), 1.5, 1.5);
        }
    }
}
