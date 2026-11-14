using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using EliteSIP.AdminAccess;
using EliteSIP.App.Admin;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Audio;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class MaximizedWindowTests
{
    [Theory]
    [InlineData("settings")]
    [InlineData("admin")]
    [InlineData("panel")]
    public void Нижние_действия_входят_в_рабочую_область_после_разворачивания(string kind) => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Dark };
        using var sounds = new SignalSoundPlayer();
        Window window = kind switch
        {
            "settings" => new SettingsWindow(new SettingsViewModel(new AppSettings()), appearance, sounds),
            "admin" => new AdministrationWindow(new AdministrationViewModel(new AppSettings(), new AdminAccessState()), appearance),
            _ => new PanelWindow(new PanelViewModel()),
        };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Opacity = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 30;
        window.Top = 30;
        try
        {
            window.Show();
            window.UpdateLayout();
            var memory = Marshal.AllocHGlobal(40);
            try
            {
                Marshal.Copy(new int[10], 0, memory, 10);
                _ = SendMessage(new WindowInteropHelper(window).Handle, 0x0024, 0, memory);
                var limits = new int[10];
                Marshal.Copy(memory, limits, 0, 10);
                var dpi = VisualTreeHelper.GetDpi(window);
                Assert.True(limits[6] >= Math.Ceiling(window.MinWidth * dpi.DpiScaleX));
                Assert.True(limits[7] >= Math.Ceiling(window.MinHeight * dpi.DpiScaleY));
            }
            finally { Marshal.FreeHGlobal(memory); }
            window.WindowState = WindowState.Maximized;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var footer = (FrameworkElement)window.FindName(kind switch
            {
                "settings" => "AdministrationButton",
                "admin" => "AdministrationFooter",
                _ => "Root",
            });
            Assert.True(footer.ActualHeight > 0);
            var bottom = footer.PointToScreen(new Point(0, footer.ActualHeight)).Y;
            var workBottom = SystemParameters.WorkArea.Bottom * VisualTreeHelper.GetDpi(window).DpiScaleY;
            Assert.True(bottom <= workBottom + 1, $"{kind}: footer bottom={bottom}, work area bottom={workBottom}; window height={window.ActualHeight}");
            if (kind == "panel") { Assert.InRange(workBottom - bottom, 0, 16); }
            window.WindowState = WindowState.Normal;
            window.UpdateLayout();
            Assert.True(footer.ActualHeight > 0);
        }
        finally
        {
            if (window is PanelWindow panel) { panel.AllowsClosing = true; }
            window.Close();
        }
    });

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
}
