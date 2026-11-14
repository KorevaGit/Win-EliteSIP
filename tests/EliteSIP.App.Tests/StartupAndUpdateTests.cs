using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Shell;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class StartupAndUpdateTests
{
    [Theory]
    [InlineData(96u, 300, 210)]
    [InlineData(120u, 375, 262)]
    [InlineData(144u, 450, 315)]
    [InlineData(192u, 600, 420)]
    [InlineData(288u, 900, 630)]
    [InlineData(384u, 1200, 840)]
    public void Заставки_обеих_тем_существуют_для_физического_DPI(uint dpi, int width, int height) => WpfHost.Run(() =>
    {
        for (var theme = 0; theme < 2; theme++)
        {
            var path = StartupSplash.ResourceForDpi(null, theme != 0, dpi);
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/EliteSIP.App;component/" + path));
            Assert.NotNull(resource);
            using var stream = resource.Stream;
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = Assert.Single(decoder.Frames);
            Assert.Equal(width, frame.PixelWidth);
            Assert.Equal(height, frame.PixelHeight);
        }
    });

    [Theory]
    [InlineData("{\"Appearance\":{\"Theme\":\"Dark\"}}", false, "Assets/Splash.png")]
    [InlineData("{\"Appearance\":{\"Theme\":\"dark\"}}", false, "Assets/Splash.png")]
    [InlineData("{\"Appearance\":{\"Theme\":\"Light\"}}", true, "Assets/Splash.Light.png")]
    [InlineData("{\"Appearance\":{\"Theme\":\"System\"}}", true, "Assets/Splash.png")]
    [InlineData("{\"Appearance\":{\"Theme\":\"System\"}}", false, "Assets/Splash.Light.png")]
    [InlineData("broken", false, "Assets/Splash.Light.png")]
    [InlineData(null, true, "Assets/Splash.png")]
    public void Ранняя_заставка_учитывает_настройку_или_системную_тему(string? json, bool systemDark, string expected)
        => Assert.Equal(expected, StartupSplash.ResourceFor(json, systemDark));

    [Fact]
    public void Заметки_обновления_сохраняют_текст_и_разделяются_по_строкам()
    {
        var content = new UpdateOfferContent(new Version(0, 1, 86), new Version(0, 1, 87), "- First\r\n\r\n• Second\n<script>literal</script>");
        Assert.Equal("EliteSIP 0.1.86 → 0.1.87", content.VersionTransition);
        Assert.Equal(["First", "Second", "<script>literal</script>"], content.Changes);
        Assert.Single(new UpdateOfferContent(new Version(0, 1, 86), new Version(0, 1, 87), " \r\n").Changes);
    }

    [Theory]
    [InlineData(Appearance.Light)]
    [InlineData(Appearance.Dark)]
    public void Длинные_заметки_прокручиваются_отдельно_от_действий(Appearance theme) => WpfHost.Run(() =>
    {
        WpfHost.UseTheme(theme);
        var notes = string.Join('\n', Enumerable.Repeat("A long release note about changes to the user interface and call handling.", 35));
        var window = new UpdateOffer(new Version(0, 1, 86), new Version(0, 1, 87), notes, theme is Appearance.Dark);
        var root = WpfHost.Layout(window, 508, double.PositiveInfinity);
        var scroll = (ScrollViewer)window.FindName("NotesScroll");
        Assert.True(scroll.ScrollableHeight > 0);
        Assert.InRange(scroll.ActualHeight, 0, 224);
        var buttons = Descendants(root).OfType<Button>().ToArray();
        Assert.Contains(buttons, button => button.IsCancel);
        Assert.Contains(buttons, button => button.IsDefault);
        foreach (var button in buttons)
        {
            Assert.True(button.TranslatePoint(default, root).Y >= scroll.TranslatePoint(default, root).Y + scroll.ActualHeight);
        }
        window.Close();
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) { yield return nested; }
        }
    }
}
