using System.IO;
using System.Windows.Media.Imaging;
using EliteSIP.App.History;
using EliteSIP.App.Theme;
using EliteSIP.CallHistory;

namespace EliteSIP.App.Tests;

public sealed class HistorySnapshotTests
{
    [Theory]
    [InlineData(Appearance.Light)]
    [InlineData(Appearance.Dark)]
    public void Снимок_содержит_шапку_и_сохраняет_нижние_скругления(Appearance theme) => WpfHost.Run(() =>
    {
        WpfHost.UseTheme(theme);
        var path = Path.Combine(Path.GetTempPath(), $"elitesip-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new CallHistoryStore(new CallHistoryStore.Settings(path));
            var model = new CallHistoryViewModel(store, Guid.NewGuid(), "176", true, () => 30);
            using var appearance = new AppearanceService(System.Windows.Application.Current) { Appearance = theme };
            var window = new CallHistoryWindow(model, appearance);
            WpfHost.Layout(window, 700, 500);
            BitmapSource image = window.CreateSnapshot();
            var stride = image.PixelWidth * 4;
            var pixels = new byte[stride * image.PixelHeight];
            image.CopyPixels(pixels, stride, 0);
            int Difference(int x, int y)
            {
                var offset = y * stride + x * 4;
                return Math.Abs(pixels[offset] - pixels[0]) + Math.Abs(pixels[offset + 1] - pixels[1]) + Math.Abs(pixels[offset + 2] - pixels[2]);
            }
            var textPixels = 0;
            for (var y = 12; y < 110; y++)
            {
                for (var x = 12; x < image.PixelWidth - 12; x++)
                {
                    if (Difference(x, y) > 60)
                    {
                        textPixels++;
                    }
                }
            }
            Assert.True(textPixels > 100, "Шапка снимка пустая или не читается");
            Assert.InRange(Difference(25, image.PixelHeight - 25), 0, 3);
            Assert.InRange(Difference(image.PixelWidth - 25, image.PixelHeight - 25), 0, 3);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    });
}
