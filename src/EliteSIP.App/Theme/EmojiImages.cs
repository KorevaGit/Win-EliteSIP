using System.IO;
using System.Windows.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>
/// Картинки смайликов для WPF: PNG из общего <see cref="EmojiPack"/> →
/// <see cref="BitmapSource"/>, с кэшем по имени.
/// </summary>
///
/// <remarks>
/// До 21 октября 2026 это было частью <c>EmojiPack</c>; пакет и разбор
/// уехали в App.Core, а здесь осталось то, что от WPF: декодер и замороженная
/// картинка, которую можно отдавать любому окну.
/// </remarks>
internal static class EmojiImages
{
    private static readonly Dictionary<string, BitmapSource?> Images = [];

    internal static BitmapSource? Get(EmojiPack pack, string name)
    {
        if (Images.TryGetValue(name, out var cached))
        {
            return cached;
        }

        BitmapSource? image = null;
        try
        {
            if (pack.OpenImage(name) is { } stream)
            {
                using (stream)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
            }
        }
        catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException)
        {
            // Битая картинка — пустое место, а не падение подписи.
            image = null;
        }

        Images[name] = image;
        return image;
    }
}
