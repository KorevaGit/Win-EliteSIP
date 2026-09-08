using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EliteSIP.App.Theme;

/// <summary>Показать, когда ложь. Обратный к встроенному.</summary>
///
/// <remarks>
/// Встроенный <c>BooleanToVisibilityConverter</c> умеет только прямое
/// направление, а половина условий панели читается наоборот: поле набора
/// видно, когда разговора нет.
/// </remarks>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Наоборот. Нужен там, где «можно» выражено через «идёт».</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>Показать, когда есть что показывать.</summary>
///
/// <remarks>
/// Пустая строка здесь тоже «нечего»: номер под именем, которого сервер не
/// прислал, — это не пустая строка на экране, а отсутствующая строка. Иначе в
/// карточке остаётся висеть разделительная точка без номера перед ней.
/// </remarks>
public sealed class PresenceToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => Visibility.Collapsed,
            string text when string.IsNullOrWhiteSpace(text) => Visibility.Collapsed,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Доля в проценты: «0,7» рядом с ползунком не говорит ничего.</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double share ? Math.Round(share * 100).ToString("0", culture) + " %" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Сегмент выбран, когда значение равно его доводу. Двусторонний: нажатие
/// сегмента кладёт в свойство его же значение.
/// </summary>
///
/// <remarks>
/// Сегментированного переключателя в WPF нет, а в макете он есть — тема, язык,
/// рабочее место и выход рингтона выбираются именно им. Собран он из
/// переключателей с общим родителем, и каждому нужен ответ на вопрос «это про
/// меня?».
///
/// Обратное преобразование отдаёт значение только при выборе: снятие галочки
/// приходит от соседнего сегмента, который в этот же миг сообщает своё, — и
/// записать на него <c>null</c> значило бы стереть только что сделанный выбор.
/// </remarks>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null
            && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null
            ? Enum.Parse(Nullable.GetUnderlyingType(targetType) ?? targetType, parameter.ToString()!)
            : Binding.DoNothing;
}
