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
