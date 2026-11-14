using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>Approved artwork shared by taskbar, application and splash.</summary>
internal static class BrandLogo
{
    private static readonly Lazy<BitmapSource> Source = new(Load);
    private static CroppedBitmap Load()
    {
        using var stream = typeof(BrandLogo).Assembly.GetManifestResourceStream("EliteSIP.BrandGold.png")!;
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var insetX = (int)(image.PixelWidth * 0.10);
        var insetY = (int)(image.PixelHeight * 0.06);
        var side = Math.Min(image.PixelWidth - 2 * insetX, image.PixelHeight - insetY);
        var cropped = new CroppedBitmap(image, new Int32Rect(insetX, insetY, side, side));
        cropped.Freeze();
        return cropped;
    }
    internal static BitmapSource Image() => Source.Value;
    internal static RenderTargetBitmap Render(int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var canvas = visual.RenderOpen()) { canvas.DrawImage(Image(), new Rect(0, 0, size, size)); }
        var result = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
