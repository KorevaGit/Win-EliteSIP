using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Settings;

internal static class SettingsDialogChrome
{
    internal static void Apply(Window window)
    {
        window.Style = (Style)window.FindResource("SettingsDialogWindowStyle");
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 32,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => window.Close()));
        window.SourceInitialized += (_, _) => WindowCaption.Apply(window,
            window.Background is SolidColorBrush brush && brush.Color.R < 128);
    }
}
