using System.Globalization;
using System.Windows.Data;

namespace EliteSIP.App.History;

/// <summary>Ограничивает длину бегунка, сохраняя весь диапазон перетаскивания.</summary>
public sealed class HistoryThumbViewportConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 3 || values[0] is not double viewport || values[1] is not double maximum || values[2] is not double height)
        {
            return 0d;
        }
        const double thumbHeight = 80;
        return height > thumbHeight && maximum > 0 ? Math.Min(viewport, maximum * thumbHeight / (height - thumbHeight)) : viewport;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
