using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using EliteSIP.AdminAccess;
using EliteSIP.App.Admin;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Audio;

namespace EliteSIP.App.Tests;

[CollectionDefinition("Settings popup windows", DisableParallelization = true)]
public sealed class SettingsPopupIsolation;

// Native windows and password derivation must not block the shared STA host
// while call lifecycle tests wait for dispatcher events with a deadline.
[Collection("Settings popup windows")]
public sealed class SettingsPopupTests
{
    [Theory]
    [InlineData(Appearance.Light)]
    [InlineData(Appearance.Dark)]
    public void Мелодия_доступна_целиком_а_строки_обработки_выровнены(Appearance theme) => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = theme };
        using var sounds = new SignalSoundPlayer();
        var model = new SettingsViewModel(new AppSettings()) { Section = SettingsSectionKind.Audio };
        var window = new SettingsWindow(model, appearance, sounds);
        var root = WpfHost.Layout(window, 718, 400);
        var processors = Walk(root).OfType<CheckBox>().Where(check => check.Tag is "Processor" or "ProcessorFirst").ToArray();
        var texts = processors.Select(check => Walk(check).OfType<TextBlock>().Single()).ToArray();
        var icons = processors.Select(check => Walk(check).OfType<Icon>().Single()).ToArray();
        Assert.All(texts, text => Assert.InRange(Math.Abs(text.TranslatePoint(new Point(), root).X - texts[0].TranslatePoint(new Point(), root).X), 0, 1));
        Assert.All(icons, icon => Assert.InRange(Math.Abs(icon.TranslatePoint(new Point(), root).X - icons[0].TranslatePoint(new Point(), root).X), 0, 1));
        var scroll = (ScrollViewer)window.FindName("SettingsScroller");
        scroll.ScrollToEnd();
        root.UpdateLayout();
        var melody = Walk(root).OfType<SettingsSection>().Single(section => section.Tag is "settings.bell");
        var surface = (Border)melody.Template.FindName("GroupSurface", melody);
        var last = Walk(melody).OfType<Button>().Last();
        Assert.True(last.TranslatePoint(new Point(0, last.ActualHeight), surface).Y <= surface.ActualHeight - 10);
        Assert.True(surface.TranslatePoint(new Point(0, surface.ActualHeight), scroll).Y <= scroll.ActualHeight);
        var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        Assert.Equal(12, bar.Width);
        window.Close();
    });

    [Theory]
    [InlineData(Appearance.Light)]
    [InlineData(Appearance.Dark)]
    public void Список_оформлен_как_меню_капсулы_и_сохраняет_выбор(Appearance theme) => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = theme };
        using var sounds = new SignalSoundPlayer();
        var model = new SettingsViewModel(new AppSettings()) { Section = SettingsSectionKind.Audio };
        var window = new SettingsWindow(model, appearance, sounds) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var combo = (ComboBox)window.FindName("RingtoneOutputChoice");
            ((ScrollViewer)window.FindName("SettingsScroller")).ScrollToEnd();
            window.UpdateLayout();
            combo.IsDropDownOpen = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var border = Assert.IsType<Border>(popup.Child);
            Assert.Equal(new CornerRadius(12), border.CornerRadius);
            Assert.Equal(new Thickness(6), border.Padding);
            combo.SelectedValue = RingtoneOutput.CallDevice;
            window.UpdateLayout();
            Assert.Equal(RingtoneOutput.CallDevice, model.Settings.Ringtone.Output);
            var selected = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(1);
            Assert.True(selected.IsSelected);
            var row = (Border)selected.Template.FindName("Row", selected);
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(row.Background).Color);
            Assert.Equal(Visibility.Visible, ((Icon)selected.Template.FindName("Tick", selected)).Visibility);
            combo.IsDropDownOpen = false;
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(Appearance.Light, AdminUnlockPurpose.Administration)]
    [InlineData(Appearance.Dark, AdminUnlockPurpose.Administration)]
    [InlineData(Appearance.Light, AdminUnlockPurpose.Workplace)]
    [InlineData(Appearance.Dark, AdminUnlockPurpose.Workplace)]
    public void Диалог_пароля_показывает_последствия_и_сохраняет_защиту(Appearance theme, AdminUnlockPurpose purpose) => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = theme };
        var settings = new AppSettings();
        settings.Admin.From(AdminCredential.Create("settings-dialog-test"));
        var access = new AdminAccessState(settings.Admin.ToCredential());
        var window = new AdminUnlockWindow(settings, access, appearance, purpose) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.False(access.IsUnlocked);
            var enter = (Button)window.FindName("EnterButton");
            Assert.False(enter.IsEnabled);
            var password = (PasswordBox)window.FindName("PasswordField");
            password.Password = "wrong-password";
            Assert.True(enter.IsEnabled);
            enter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(access.IsUnlocked);
            Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("ProblemRow")).Visibility);
            var steps = (ItemsControl)window.FindName("Consequences");
            Assert.Equal(purpose is AdminUnlockPurpose.Workplace ? 3 : 1, steps.Items.Count);
            var icons = Walk((FrameworkElement)window.Content).OfType<Icon>().Where(icon => icon.IsVisible).ToArray();
            Assert.NotEmpty(icons);
            Assert.All(icons, icon => Assert.NotNull(icon.Shape));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Подтверждение_языка_показывает_шаги_вместо_абзаца() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Dark };
        var visual = new DialogVisual("settings.languages", [new("settings.phone-off", "call"), new("settings.rotate-cw", "restart"), new("settings.server", "registration")]);
        var window = new Dialog("language", "long body", visual);
        try
        {
            var root = WpfHost.Layout(window, 458, 300);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("BodyText")).Visibility);
            Assert.Equal(3, ((ItemsControl)window.FindName("StepsHost")).Items.Count);
            Assert.DoesNotContain(Walk(root).OfType<TextBlock>(), text => text.IsVisible && text.Text == "long body");
        }
        finally { window.Close(); }
    });

    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var item in Walk(child)) { yield return item; }
        }
    }
}
