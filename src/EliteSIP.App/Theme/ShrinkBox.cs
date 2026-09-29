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

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        var size = ScaledSize(child, constraint.Width);
        child.SetValue(TextElement.FontSizeProperty, size);
        child.Measure(new Size(constraint.Width, double.PositiveInfinity));

        // По высоте — тоже. Клавиша, которой окно убавило высоту, чтобы сетка
        // влезла в экран, срезала вторую строку подписи («Кнопка для
        // промаха» без низа — 0.1.58 на живой машине). Ужимается ступенями до
        // того же предела: ниже честнее срезать, чем показать нечитаемое.
        var floor = BaseFontSize * MinimumScale;
        while (!double.IsInfinity(constraint.Height)
               && child.DesiredSize.Height > constraint.Height
               && size > floor)
        {
            size = Math.Max(floor, size * 0.92);
            child.SetValue(TextElement.FontSizeProperty, size);
            child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        }

        child.Measure(constraint);
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
    private double ScaledSize(UIElement child, double available)
    {
        if (child is not TextBlock text
            || string.IsNullOrEmpty(text.Text)
            || double.IsInfinity(available)
            || available <= 0)
        {
            return BaseFontSize;
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
                BaseFontSize,
                Brushes.Black,
                dpi);

            longest = Math.Max(longest, measured.WidthIncludingTrailingWhitespace);
        }

        if (longest <= available || longest <= 0)
        {
            return BaseFontSize;
        }

        return BaseFontSize * Math.Max(available / longest, MinimumScale);
    }
}
