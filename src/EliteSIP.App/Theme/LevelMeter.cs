using System.Windows;
using System.Windows.Media;

namespace EliteSIP.App.Theme;

/// <summary>
/// Сегментная шкала уровня: «Голос» и «Звук» в проверке звука.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Корень, а не линейно и не в децибелах.</b> Слух логарифмический, и
/// линейная шкала на обычной речи едва отрывается от нуля — выглядит как
/// «микрофон почти не слышит». Децибелы честнее, но тянут тишину на треть
/// шкалы. Корень от линейного уровня — середина, которой пользуется и
/// macOS-версия: речь ложится на середину, крик — в жёлтое.
/// </para>
/// <para>
/// <b>Цвета по месту, а не по уровню.</b> Сегменты до 75 % шкалы зелёные, до
/// 92 % жёлтые, выше красные — там начинается мягкое ограничение, и собеседник
/// слышит сжатый голос. Пик держится полсекунды: глаз не успевает за слогами,
/// а пиковая чёрточка показывает, до куда дошло.
/// </para>
/// </remarks>
public sealed class LevelMeter : FrameworkElement
{
    /// <summary>Уровень, линейный: 0 — тишина, 1 — полная шкала.</summary>
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLevelChanged));

    private const int Segments = 24;
    private const double GreenUntil = 0.75;
    private const double YellowUntil = 0.92;
    private const double Gap = 2;

    private static readonly TimeSpan PeakHold = TimeSpan.FromMilliseconds(500);

    private static readonly Brush Green = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush Yellow = Frozen(Color.FromRgb(0xFF, 0xCC, 0x00));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xFF, 0x3B, 0x30));

    private double _shown;
    private double _peak;
    private DateTime _peakAt;

    public LevelMeter()
    {
        Height = 10;
        SnapsToDevicePixels = true;
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static void OnLevelChanged(DependencyObject owner, DependencyPropertyChangedEventArgs change)
    {
        var meter = (LevelMeter)owner;
        meter._shown = Math.Sqrt(Math.Clamp((double)change.NewValue, 0, 1));

        var now = DateTime.UtcNow;
        if (meter._shown >= meter._peak || now - meter._peakAt > PeakHold)
        {
            meter._peak = meter._shown;
            meter._peakAt = now;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var unlit = TryFindResource("SeparatorBrush") as Brush ?? Brushes.LightGray;
        var segment = (width - (Gap * (Segments - 1))) / Segments;
        var lit = (int)Math.Round(_shown * Segments);
        var peak = Math.Clamp((int)Math.Ceiling(_peak * Segments) - 1, -1, Segments - 1);

        for (var index = 0; index < Segments; index++)
        {
            var position = (index + 1) / (double)Segments;
            var color = position <= GreenUntil ? Green : position <= YellowUntil ? Yellow : Red;
            var on = index < lit || (index == peak && _peak > 0.02);

            drawingContext.DrawRoundedRectangle(
                on ? color : unlit,
                null,
                new Rect(index * (segment + Gap), 0, segment, height),
                1.5,
                1.5);
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
