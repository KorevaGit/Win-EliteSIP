using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EliteSIP.App.Theme;

/// <summary>
/// Ужимает вложенную подпись кеглем, когда та не влезает в отведённую ширину, —
/// но не ниже заданного предела.
/// </summary>
///
/// <remarks>
/// В SwiftUI это был <c>minimumScaleFactor</c>, и панель пользовалась им в
/// нескольких местах: подписи макросов, имя собеседника, надпись беды. В WPF
/// такого нет, а обходные пути дают не то:
///
/// <list type="bullet">
/// <item><c>Viewbox</c> уменьшает всё вместе с переносами. Подпись, которой
/// задана ширина, сообщает наружу ровно эту ширину — уменьшать <c>Viewbox</c>
/// нечего, и длинное слово он молча обрезает.</item>
/// <item><c>TextTrimming</c> обрезает хвост. Для названия отдела это худший
/// исход: «Бухгалтер» вместо «Бухгалтерии» читается как другое слово, а не как
/// обрезанное. Ровно это и показал первый живой прогон сетки.</item>
/// </list>
///
/// Обёрткой, а не наследником <c>TextBlock</c>, потому что у того
/// <c>MeasureOverride</c> запечатан.
///
/// Предел обязателен: ниже 0.6 подпись перестаёт читаться, и честнее показать
/// её обрезанной, чем нечитаемой. То же число стояло в оригинале.
/// </remarks>
public sealed class ShrinkBox : Decorator
{
    /// <summary>До какой доли кегля разрешено ужимать.</summary>
    public static readonly DependencyProperty MinimumScaleProperty = DependencyProperty.Register(
        nameof(MinimumScale),
        typeof(double),
        typeof(ShrinkBox),
        new FrameworkPropertyMetadata(0.6, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Кегль, с которого начинается подбор.</summary>
    ///
    /// <remarks>
    /// Задаётся здесь, а не подписи, потому что подбор сам этот кегль и меняет:
    /// без исходного значения вторая клавиша начинала бы подбор с того, чем
    /// закончила первая, и сетка получалась бы разного кегля в зависимости от
    /// порядка раскладки.
    /// </remarks>
    public static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.Register(
        nameof(BaseFontSize),
        typeof(double),
        typeof(ShrinkBox),
        new FrameworkPropertyMetadata(15.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinimumScale
    {
        get => (double)GetValue(MinimumScaleProperty);
        set => SetValue(MinimumScaleProperty, value);
    }

    public double BaseFontSize
    {
        get => (double)GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    /// <summary>
    /// Растить ли кегль вместе с высотой, которую дали подписи, — до
    /// <see cref="MaximumScale"/> от исходного.
    /// </summary>
    ///
    /// <remarks>
    /// Для клавиш панели: с 0.1.60 растянутое окно увеличивает только клавиши,
    /// и подпись в 16 точек посреди клавиши высотой в сотню терялась.
    /// </remarks>
    public static readonly DependencyProperty GrowsWithHeightProperty = DependencyProperty.Register(
        nameof(GrowsWithHeight),
        typeof(bool),
        typeof(ShrinkBox),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>До какой доли кегля разрешено расти.</summary>
    public static readonly DependencyProperty MaximumScaleProperty = DependencyProperty.Register(
        nameof(MaximumScale),
        typeof(double),
        typeof(ShrinkBox),
        new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// Высота подписи, когда разметка даёт бесконечную (окно по содержимому).
    /// </summary>
    public static readonly DependencyProperty FallbackHeightProperty = DependencyProperty.Register(
        nameof(FallbackHeight),
        typeof(double),
        typeof(ShrinkBox),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool GrowsWithHeight
    {
        get => (bool)GetValue(GrowsWithHeightProperty);
        set => SetValue(GrowsWithHeightProperty, value);
    }

    public double MaximumScale
    {
        get => (double)GetValue(MaximumScaleProperty);
        set => SetValue(MaximumScaleProperty, value);
    }

    public double FallbackHeight
    {
        get => (double)GetValue(FallbackHeightProperty);
        set => SetValue(FallbackHeightProperty, value);
    }

    /// <summary>Доля высоты подписи, которую занимает кегль: две строки с полями.</summary>
    private const double HeightToFont = 0.3;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        var height = double.IsInfinity(constraint.Height) ? FallbackHeight : constraint.Height;
        var start = BaseFontSize;
        if (GrowsWithHeight && !double.IsInfinity(height))
        {
            start = Math.Clamp(height * HeightToFont, BaseFontSize, BaseFontSize * MaximumScale);
        }

        var size = ScaledSize(child, constraint.Width, start);
        child.SetValue(TextElement.FontSizeProperty, size);
        child.Measure(new Size(constraint.Width, double.PositiveInfinity));

        // По высоте — тоже. Клавиша, которой окно убавило высоту, чтобы сетка
        // влезла в экран, срезала вторую строку подписи («Кнопка для
        // промаха» без низа — 0.1.58 на живой машине). Ужимается ступенями до
        // того же предела: ниже честнее срезать, чем показать нечитаемое.
        var floor = BaseFontSize * MinimumScale;
        while (!double.IsInfinity(height)
               && child.DesiredSize.Height > height
               && size > floor)
        {
            size = Math.Max(floor, size * 0.92);
            child.SetValue(TextElement.FontSizeProperty, size);
            child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        }

        child.Measure(new Size(constraint.Width, Math.Min(constraint.Height, height)));
        return child.DesiredSize;
    }

    /// <summary>Кегль, при котором самое длинное слово влезает в ширину.</summary>
    ///
    /// <remarks>
    /// Считается по самому длинному **слову**, а не по всей подписи, и подбором
    /// это не заменить. Подпись с переносом сообщает наружу ширину, в которую её
    /// втиснули, а не ту, которая ей нужна: «Бухгалтерия» в клавише на 78 точек
    /// отвечает «мне нужно 78» и молча теряет хвост. Спрашивать бесполезно —
    /// надо мерить самим.
    ///
    /// Слово, а не строка, потому что перенос по пробелам делает своё дело сам:
    /// «Отдел продаж» ужимать не нужно вовсе, он переносится. Ужимать надо
    /// ровно то, что не переносится ни при какой ширине.
    /// </remarks>
    private double ScaledSize(UIElement child, double available, double start)
    {
        if (child is not TextBlock text
            || string.IsNullOrEmpty(text.Text)
            || double.IsInfinity(available)
            || available <= 0)
        {
            return start;
        }

        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var longest = 0d;

        foreach (var word in text.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var measured = new FormattedText(
                word,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                start,
                Brushes.Black,
                dpi);

            longest = Math.Max(longest, measured.WidthIncludingTrailingWhitespace);
        }

        if (longest <= available || longest <= 0)
        {
            return start;
        }

        // Предел — от исходного кегля, а не от выросшего: выросшая подпись
        // ужимается обратно хоть до исходного, но не ниже его доли.
        return Math.Max(start * available / longest, BaseFontSize * MinimumScale);
    }
}
