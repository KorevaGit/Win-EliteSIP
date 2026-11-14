using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EliteSIP.App.Theme;
using EliteSIP.App.Shell;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class TrayMenuThemeTests
{
    [Fact]
    public void Одна_и_та_же_всплывашка_перекрашивает_фон_и_текст_вместе() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Dark };
        var menu = new ContextMenu();
        menu.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/EliteSIP.App;component/Shell/TrayMenu.xaml", UriKind.Relative),
        });
        menu.SetResourceReference(FrameworkElement.StyleProperty, "TrayMenuStyle");
        TrayIcon.RefreshMenuPalette(menu);
        void Refresh() => TrayIcon.RefreshMenuPalette(menu);
        AppearanceService.PaletteChanged += Refresh;
        try
        {
        menu.Items.Add(new MenuItem { Header = "Настройки" });
        menu.Measure(new Size(320, double.PositiveInfinity));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        var border = (Border)VisualTreeHelper.GetChild(menu, 0);
        var dark = (LinearGradientBrush)border.Background;
        Assert.True(dark.GradientStops[0].Color.R < 90);
        appearance.Appearance = Appearance.Light;
        menu.UpdateLayout();
        var light = (LinearGradientBrush)border.Background;
        Assert.True(light.GradientStops[0].Color.R > 220);
        Assert.True(((SolidColorBrush)menu.Foreground).Color.R < 70);
        appearance.Appearance = Appearance.Dark;
        menu.UpdateLayout();
        Assert.True(((LinearGradientBrush)border.Background).GradientStops[0].Color.R < 90);
        Assert.True(((SolidColorBrush)menu.Foreground).Color.R > 220);
        }
        finally { AppearanceService.PaletteChanged -= Refresh; }
    });
}
