using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EliteSIP.PanelLink;

namespace EliteSIP.App.FirstRun;

/// <summary>
/// QR-картинка для WPF из <see cref="QrCode"/>.
///
/// Конвертер, а не свойство модели: с 20 октября 2026 модель мастера общая с
/// Linux-версией и отдаёт сам код, а картинку рисует окно своей платформы.
/// </summary>
public sealed class QrImageConverter : IValueConverter
{
    public static QrImageConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is QrCode qr ? Render(qr) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>
    /// Рисует код чёрным по белому с полем в четыре модуля — независимо от
    /// темы: сканеры читают тёмное на светлом, а на тёмной теме инвертированный
    /// код часть телефонов не видит.
    /// </summary>
    internal static ImageSource Render(QrCode qr)
    {
        const int quiet = 4;
        var size = qr.Size + (quiet * 2);

        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[(y * size) + x] = qr[x - quiet, y - quiet] ? (byte)0 : (byte)255;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bitmap.Freeze();
        return bitmap;
    }
}
