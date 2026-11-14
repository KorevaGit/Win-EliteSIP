using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Audio;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class WindowBackgroundTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("elitesip-background-").FullName;

    private static BitmapSource Image()
    {
        var pixels = new byte[80 * 40 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var x = i / 4 % 80;
            pixels[i] = x < 40 ? (byte)50 : (byte)200;
            pixels[i + 1] = x < 40 ? (byte)70 : (byte)170;
            pixels[i + 2] = x < 40 ? (byte)90 : (byte)140;
            pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
        image.Freeze();
        return image;
    }

    private string SaveImage(string name)
    {
        var path = Path.Combine(_directory, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Image()));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    [Fact]
    public void Обои_задают_умолчания_но_сохраняют_явный_выбор_после_перезапуска()
    {
        var settings = new AppSettings();
        settings.Appearance.ApplyWallpaperDefaults();
        Assert.Equal(Appearance.Wallpaper, settings.Appearance.Theme);
        Assert.Equal(BackgroundTransparency.Light, settings.Appearance.BackgroundTransparency);
        settings.Appearance.Theme = Appearance.Light;
        settings.Appearance.ThemeChosenByUser = true;
        settings.Appearance.BackgroundTransparency = BackgroundTransparency.Off;
        settings.Appearance.TransparencyChosenByUser = true;
        var path = Path.Combine(_directory, "explicit.json");
        settings.Save(path);
        var loaded = AppSettings.Load(path).Appearance;
        loaded.ApplyWallpaperDefaults();
        Assert.Equal(Appearance.Light, loaded.Theme);
        Assert.Equal(BackgroundTransparency.Off, loaded.BackgroundTransparency);
        loaded.BackgroundTransparency = BackgroundTransparency.Strong;
        settings.Appearance.BackgroundTransparency = BackgroundTransparency.Strong;
        settings.Save(path);
        Assert.Equal(BackgroundTransparency.Strong, AppSettings.Load(path).Appearance.BackgroundTransparency);
        Assert.True(WindowBackgroundService.SurfaceOpacity(BackgroundTransparency.Light)
            > WindowBackgroundService.SurfaceOpacity(BackgroundTransparency.Medium));
        Assert.True(WindowBackgroundService.SurfaceOpacity(BackgroundTransparency.Medium)
            > WindowBackgroundService.SurfaceOpacity(BackgroundTransparency.Strong));
    }

    [Fact]
    public void Настройки_фона_переживают_перезапуск_и_сброс_возвращает_обычную_тему()
    {
        var path = Path.Combine(_directory, "settings.json");
        var settings = new AppSettings();
        settings.Appearance.BackgroundImagePath = "local-image.png";
        settings.Appearance.BackgroundScale = 140;
        settings.Appearance.BackgroundBlur = 12;
        settings.Appearance.BackgroundBrightness = 80;
        settings.Appearance.BackgroundTranslucent = true;
        settings.Appearance.Theme = Appearance.Wallpaper;
        settings.Save(path);
        var loaded = AppSettings.Load(path).Appearance;
        Assert.Equal("local-image.png", loaded.BackgroundImagePath);
        Assert.Equal(140, loaded.BackgroundScale);
        Assert.Equal(12, loaded.BackgroundBlur);
        Assert.Equal(80, loaded.BackgroundBrightness);
        Assert.True(loaded.BackgroundTranslucent);
        Assert.Equal(Appearance.Wallpaper, loaded.Theme);
        loaded.ResetBackground();
        Assert.False(loaded.HasBackgroundImage);
        Assert.False(loaded.BackgroundTranslucent);

        Assert.Equal(100, loaded.BackgroundBrightness);
    }

    [Fact]
    public void Сохранённая_копия_не_зависит_от_оригинала_и_не_держит_файл_открытым() => WpfHost.Run(() =>
    {
        var source = SaveImage("source.png");
        var destination = Path.Combine(_directory, "background.png");
        BackgroundImages.Import(source, destination);
        File.Delete(source);
        var copy = BackgroundImages.Read(destination);
        File.Delete(destination);
        Assert.Equal(80, copy.PixelWidth);
        Assert.Equal(40, copy.PixelHeight);
        Assert.True(copy.IsFrozen);
    });

    [Fact]
    public void Повреждённая_новая_картинка_не_заменяет_старую() => WpfHost.Run(() =>
    {
        var destination = SaveImage("background.png");
        var original = File.ReadAllBytes(destination);
        var broken = Path.Combine(_directory, "broken.png");
        File.WriteAllText(broken, "not an image");
        Assert.ThrowsAny<Exception>(() => BackgroundImages.Import(broken, destination));
        Assert.Equal(original, File.ReadAllBytes(destination));
    });

    [Fact]
    public void Яркость_и_размытие_меняют_только_изображение() => WpfHost.Run(() =>
    {
        var source = Image();
        var normal = WindowBackgroundService.Process(source, 100, 0);
        var dim = WindowBackgroundService.Process(source, 50, 0);
        Assert.Equal(Pixels(normal)[0] / 2, Pixels(dim)[0]);
        var blurred = WindowBackgroundService.Process(source, 100, 10);
        var at = (20 * source.PixelWidth + 38) * 4;
        Assert.True(Pixels(blurred)[at] > Pixels(normal)[at]);
        Assert.Equal(50, Pixels(source)[0]);
    });

    [Fact]
    public void Изменение_размеров_и_повторное_применение_не_пересобирают_фон_и_палитру() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Dark };
        var settings = new AppearanceSettings { BackgroundImagePath = SaveImage("cached.png"), BackgroundTranslucent = true };
        using var backgrounds = new WindowBackgroundService(Application.Current, settings, appearance, _ => { });
        var brush = Assert.IsType<ImageBrush>(Application.Current.FindResource("PanelWindowBackgroundBrush"));
        var surface = Application.Current.FindResource("PanelMacroBrush");
        var border = new Border { Background = brush };
        for (var i = 0; i < 40; i++)
        {
            border.Measure(new Size(300 + i * 10, 400 + i * 10));
            border.Arrange(new Rect(0, 0, 300 + i * 10, 400 + i * 10));
            border.UpdateLayout();
            backgrounds.ApplyNow();
        }
        Assert.Equal(1, backgrounds.CompositionCount);
        Assert.Same(brush, Application.Current.FindResource("PanelWindowBackgroundBrush"));
        Assert.Same(surface, Application.Current.FindResource("PanelMacroBrush"));
        Assert.True(brush.ImageSource.IsFrozen);
        Assert.All(Pixels((BitmapSource)brush.ImageSource).Where((_, i) => i % 4 == 3), alpha => Assert.Equal(255, alpha));
        settings.BackgroundBrightness = 80;
        backgrounds.ApplyNow();
        Assert.Equal(2, backgrounds.CompositionCount);
        Assert.NotSame(brush, Application.Current.FindResource("PanelWindowBackgroundBrush"));
        Assert.Same(surface, Application.Current.FindResource("PanelMacroBrush"));
    });

    [Fact]
    public void Фон_адаптируется_к_теме_а_прозрачность_обратима() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Dark };
        var settings = new AppSettings();
        settings.Appearance.BackgroundImagePath = SaveImage("background.png");
        settings.Appearance.BackgroundBlur = 0;
        using var backgrounds = new WindowBackgroundService(Application.Current, settings.Appearance, appearance, _ => { });
        var dark = (ImageBrush)Application.Current.FindResource("PanelWindowBackgroundBrush");
        appearance.Appearance = Appearance.Light;
        var light = (ImageBrush)Application.Current.FindResource("PanelWindowBackgroundBrush");
        Assert.True(Centre(light) > Centre(dark));
        Assert.True(light.IsFrozen);
        settings.Appearance.BackgroundTranslucent = true;
        backgrounds.ApplyNow();
        Assert.Equal(0.96, ((Brush)Application.Current.FindResource("PanelMacroBrush")).Opacity);
        Assert.Equal(245, ((Color)Application.Current.FindResource("SettingsSurfaceTopColor")).A);
        var neutral = ((Color)Application.Current.FindResource("SettingsSurfaceTopColor"));
        appearance.Appearance = Appearance.Wallpaper;
        backgrounds.ApplyNow();
        Assert.NotEqual(neutral, (Color)Application.Current.FindResource("SettingsSurfaceTopColor"));
        appearance.Appearance = Appearance.Light;
        backgrounds.ApplyNow();
        Assert.Equal(neutral, (Color)Application.Current.FindResource("SettingsSurfaceTopColor"));
        settings.Appearance.ResetBackground();
        backgrounds.ApplyNow();
        Assert.IsType<SolidColorBrush>(Application.Current.FindResource("PanelWindowBackgroundBrush"));
        Assert.Equal(1, ((Brush)Application.Current.FindResource("PanelMacroBrush")).Opacity);
        Assert.Equal(255, ((Color)Application.Current.FindResource("SettingsSurfaceTopColor")).A);
    });

    [Fact]
    public void Отсутствующая_картинка_оставляет_обычный_фон() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Light };
        var settings = new AppearanceSettings { BackgroundImagePath = Path.Combine(_directory, "missing.png") };
        var logs = new List<string>();
        using var backgrounds = new WindowBackgroundService(Application.Current, settings, appearance, logs.Add);
        Assert.IsType<SolidColorBrush>(Application.Current.FindResource("PanelWindowBackgroundBrush"));
        Assert.Single(logs);
    });

    [Fact]
    public void Предпросмотр_и_карточки_показывают_фон_без_прозрачности_текста() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Dark };
        using var sounds = new SignalSoundPlayer();
        var settings = new AppSettings();
        settings.Appearance.BackgroundImagePath = SaveImage("background.png");
        settings.Appearance.BackgroundTranslucent = true;
        using var backgrounds = new WindowBackgroundService(Application.Current, settings.Appearance, appearance, _ => { });
        var window = new SettingsWindow(new SettingsViewModel(settings) { Section = SettingsSectionKind.Appearance }, appearance, sounds);
        var root = WpfHost.Layout(window, 720, 460);
        var preview = (Border)window.FindName("BackgroundPreview");
        Assert.IsType<ImageBrush>(preview.Background);
        Assert.Equal(245, ((LinearGradientBrush)window.FindResource("SettingsSurfaceBrush")).GradientStops[0].Color.A);
        Assert.Equal(1, root.Opacity);
        appearance.Appearance = Appearance.Light;
        root.UpdateLayout();
        Assert.True(((LinearGradientBrush)window.FindResource("SettingsSurfaceBrush")).GradientStops[0].Color.R > 220);
        Assert.IsType<ImageBrush>(preview.Background);
        var scroller = (ScrollViewer)window.FindName("SettingsScroller");
        scroller.ScrollToEnd();
        root.UpdateLayout();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinEliteSIP.slnx"))) { directory = directory.Parent; }
        Assert.NotNull(directory);
        var output = Path.Combine(directory.FullName, "artifacts", "background-checks", "preview.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var screenshot = new RenderTargetBitmap(720, 460, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen()) { context.DrawRectangle(window.Background, null, new Rect(0, 0, 720, 460)); }
        screenshot.Render(backdrop);
        screenshot.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(screenshot));
        using (var file = File.Create(output)) { encoder.Save(file); }
        window.Close();
    });

    private static byte Centre(Brush brush)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) { context.DrawRectangle(brush, null, new Rect(0, 0, 80, 40)); }
        var bitmap = new RenderTargetBitmap(80, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Pixels(bitmap)[(20 * 80 + 40) * 4];
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
