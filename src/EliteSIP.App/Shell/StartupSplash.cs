using System.IO;
using System.Text.Json;
using System.Windows;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Shell;

/// <summary>Ранняя нативная заставка: до загрузки словарей и создания первого окна.</summary>
internal static class StartupSplash
{
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
        var asset = ResourceFor(settings, AppearanceService.SystemPrefersDark());
        new SplashScreen(typeof(App).Assembly, asset).Show(autoClose: true);
    }

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
                    dark = theme.ToString().ToUpperInvariant() switch { "DARK" or "2" => true, "LIGHT" or "1" => false, _ => systemDark };
                }
            }
            catch (JsonException) { }
        }
        return dark ? "Assets/Splash.png" : "Assets/Splash.Light.png";
    }
}
