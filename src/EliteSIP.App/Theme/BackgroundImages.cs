using System.IO;
using System.Windows.Media.Imaging;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Theme;

internal static class BackgroundImages
{
    internal static string StoredPath => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "window-background.png");

    internal static BitmapSource Read(string path)
    {
        var file = new FileInfo(path);
        if (file.Length > 30 * 1024 * 1024) { throw new InvalidDataException("Image exceeds 30 MB"); }
        using var stream = file.OpenRead();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000) { throw new InvalidDataException("Image dimensions exceed limit"); }
        var wide = frame.PixelWidth >= frame.PixelHeight;
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (wide) { image.DecodePixelWidth = Math.Min(1600, frame.PixelWidth); }
        else { image.DecodePixelHeight = Math.Min(1600, frame.PixelHeight); }
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static void Import(string source, string destination)
    {
        // Декодируем до замены файла: повреждённая картинка не уничтожает прежний фон.
        var image = Read(source);
        var temporary = destination + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(temporary)) { encoder.Save(stream); }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }
}
