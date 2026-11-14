using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Shell;

/// <summary>Ранняя нативная заставка: до загрузки словарей и создания первого окна.</summary>
internal static class StartupSplash
{
    private static readonly JsonSerializerOptions AppearanceFormat = new() { Converters = { new JsonStringEnumConverter() } };
    internal static void Show()
    {
        string? settings = null;
        try
        {
            if (File.Exists(AppSettings.DefaultPath) && new FileInfo(AppSettings.DefaultPath).Length <= 1024 * 1024)
            {
                settings = File.ReadAllText(AppSettings.DefaultPath);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (TryShowWallpaper(settings, AppearanceService.SystemPrefersDark())) { return; }
        var asset = ResourceForDpi(settings, AppearanceService.SystemPrefersDark(), GetDpiForSystem());
        new SplashScreen(typeof(App).Assembly, asset).Show(autoClose: true);
    }

    private static bool TryShowWallpaper(string? json, bool systemDark)
    {
        if (json is null) { return false; }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Appearance", out var section)) { return false; }
            var appearance = section.Deserialize<AppearanceSettings>(AppearanceFormat);
            if (appearance is null || !appearance.HasBackgroundImage) { return false; }
            var dark = appearance.Theme is Appearance.Dark or Appearance.Wallpaper
                || appearance.Theme == Appearance.System && systemDark;
            var image = WindowBackgroundService.Process(BackgroundImages.Read(appearance.BackgroundImagePath),
                appearance.BackgroundBrightness, appearance.BackgroundBlur);
            var content = new Grid { Background = WindowBackgroundService.Compose(image, appearance.BackgroundScale, dark) };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new Image { Source = BrandLogo.Image(), Width = 108, Height = 108 });
            stack.Children.Add(new TextBlock { Text = "EliteSIP", FontSize = 25, FontWeight = FontWeights.SemiBold,
                Foreground = dark ? Brushes.White : Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0) });
            content.Children.Add(stack);
            var splash = new Window { Width = 440, Height = 280, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = content };
            splash.Show();
            splash.UpdateLayout();
            splash.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            splash.Dispatcher.BeginInvoke(() =>
            {
                splash.Close();
                if (Application.Current.MainWindow == splash) { Application.Current.MainWindow = null; }
            }, DispatcherPriority.ApplicationIdle);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    internal static string ResourceForDpi(string? settings, bool systemDark, uint dpi)
    {
        var resource = ResourceFor(settings, systemDark);
        var percent = Math.Clamp((int)Math.Round((dpi == 0 ? 96 : dpi) * 100d / 96 / 25) * 25, 100, 400);
        return percent == 100 ? resource : resource.Replace(".png", $".{percent}.png", StringComparison.Ordinal);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    internal static string ResourceFor(string? settings, bool systemDark)
    {
        var dark = systemDark;
        if (settings is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(settings);
                if (document.RootElement.ValueKind is JsonValueKind.Object
                    && document.RootElement.TryGetProperty("Appearance", out var appearance)
                    && appearance.ValueKind is JsonValueKind.Object && appearance.TryGetProperty("Theme", out var theme))
                {
                    dark = theme.ToString().ToUpperInvariant() switch { "DARK" or "2" or "WALLPAPER" or "3" => true, "LIGHT" or "1" => false, _ => systemDark };
                }
            }
            catch (JsonException) { }
        }
        return dark ? "Assets/Splash.png" : "Assets/Splash.Light.png";
    }
}
