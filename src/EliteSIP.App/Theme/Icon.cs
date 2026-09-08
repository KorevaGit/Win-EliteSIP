using System.Windows;
using System.Windows.Controls;

namespace EliteSIP.App.Theme;

/// <summary>
/// Значок из своего комплекта. Имя — ключ ресурса в <c>Theme/Icons.xaml</c>,
/// совпадающий с именем SVG в комплекте macOS-версии (<c>phone.down.fill</c>).
/// </summary>
///
/// <remarks>
/// Своё, а не <c>Image</c> с растром и не шрифт со значками, ровно по той
/// причине, по которой комплект вообще появился: значки перекрашиваются под
/// место, где стоят, и должны оставаться резкими на любом масштабе экрана.
/// Растр в 12 точек на 150% превращается в кашу, а шрифт означал бы третий
/// формат тех же фигур.
///
/// Размер задаётся стороной квадрата, а не шириной и высотой порознь: в макете
/// значки квадратные все до одного, и две величины позволяли бы задать
/// раздавленный.
/// </remarks>
public sealed class Icon : Control
{
    /// <summary>Имя значка — оно же ключ ресурса.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(Icon),
        new PropertyMetadata(defaultValue: null, OnGlyphChanged));

    /// <summary>Сторона квадрата. По умолчанию средняя из трёх ступеней шкалы.</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size),
        typeof(double),
        typeof(Icon),
        new PropertyMetadata(IconSizes.Medium));

    private static readonly DependencyPropertyKey ShapeKey = DependencyProperty.RegisterReadOnly(
        nameof(Shape),
        typeof(object),
        typeof(Icon),
        new PropertyMetadata(defaultValue: null));

    /// <summary>Фигура значка. Читается шаблоном, снаружи не задаётся.</summary>
    public static readonly DependencyProperty ShapeProperty = ShapeKey.DependencyProperty;

    static Icon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(typeof(Icon)));
    }

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public object? Shape => GetValue(ShapeProperty);

    private static void OnGlyphChanged(DependencyObject sender, DependencyPropertyChangedEventArgs change)
    {
        var icon = (Icon)sender;
        var name = (string?)change.NewValue;

        // Ресурс ищется от самого значка, а не от приложения: тема — словарь,
        // и подменённый по месту значок должен находиться там же, где его
        // подменили.
        var shape = name is null ? null : icon.TryFindResource(name);

        // Опечатка в имени не должна давать молчаливую пустоту: невидимый
        // значок читается как «кнопка без иконки по замыслу», и такое живёт в
        // сборке месяцами. В отладке это остановка, в бою — пустое место:
        // ронять софтфон на кнопке, где не нашлась картинка, нельзя.
        System.Diagnostics.Debug.Assert(name is null || shape is not null, $"нет значка {name}");

        icon.SetValue(ShapeKey, shape);
    }
}
