using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace EliteSIP.App.Theme;

/// <summary>
/// Ползунок громкости 0–200 %: метка на 100 %, к которой бегунок прилипает при
/// перетаскивании, двойной щелчок — сразу 100 %, колесо мыши — шаг 5 %.
/// </summary>
///
/// <remarks>
/// Одно поведение на панели и в настройках, как на Linux: КЦ подстраивает
/// громкость часто, и вернуться к норме должно быть делом одного движения, а
/// не прицеливания.
/// </remarks>
public static class VolumeSlider
{
    /// <summary>Насколько близко к 100 % бегунок прилипает к метке.</summary>
    internal const double Magnet = 0.075;

    public static readonly DependencyProperty IsVolumeProperty = DependencyProperty.RegisterAttached(
        "IsVolume", typeof(bool), typeof(VolumeSlider), new PropertyMetadata(false, OnIsVolumeChanged));

    public static bool GetIsVolume(DependencyObject element) => (bool)element.GetValue(IsVolumeProperty);

    public static void SetIsVolume(DependencyObject element, bool value) => element.SetValue(IsVolumeProperty, value);

    /// <summary>Значение у метки 100 % — сама метка.</summary>
    internal static double Stick(double value) => Math.Abs(value - 1) < Magnet ? 1 : value;

    /// <summary>Шаг колеса: 5 % в сторону прокрутки, в пределах ползунка.</summary>
    internal static double Wheel(double value, int delta, double minimum, double maximum)
        => Math.Clamp(Math.Round((value + (delta > 0 ? 0.05 : -0.05)) * 20) / 20, minimum, maximum);

    private static void OnIsVolumeChanged(DependencyObject element, DependencyPropertyChangedEventArgs change)
    {
        if (element is not Slider slider || change.NewValue is not true)
        {
            return;
        }

        var dragging = false;
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true), handledEventsToo: true);
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => dragging = false), handledEventsToo: true);
        slider.ValueChanged += (_, _) =>
        {
            if (dragging && Stick(slider.Value) != slider.Value)
            {
                slider.Value = Stick(slider.Value);
            }
        };
        slider.MouseDoubleClick += (_, e) =>
        {
            if (slider.IsEnabled)
            {
                slider.Value = 1;
                e.Handled = true;
            }
        };
        slider.PreviewMouseWheel += (_, e) =>
        {
            if (slider.IsEnabled)
            {
                slider.Value = Wheel(slider.Value, e.Delta, slider.Minimum, slider.Maximum);
                e.Handled = true;
            }
        };
    }
}
