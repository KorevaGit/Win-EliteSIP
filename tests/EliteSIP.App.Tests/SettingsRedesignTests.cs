using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Audio;

namespace EliteSIP.App.Tests;

public sealed class SettingsRedesignTests
{
    [Theory]
    [InlineData(Appearance.Light)]
    [InlineData(Appearance.Dark)]
    public void Вкладки_заполняют_ширину_при_минимальном_размере_окна(Appearance theme) => WpfHost.Run(() =>
    {
        WpfHost.UseTheme(theme);
        using var appearance = new AppearanceService(Application.Current) { Appearance = theme };
        using var sounds = new SignalSoundPlayer();
        var model = new SettingsViewModel(new AppSettings());
        Assert.Equal(4, model.Sections.Count);
        foreach (var section in model.Sections)
        {
            model.Section = section.Kind;
            var window = new SettingsWindow(model, appearance, sounds);
            var root = WpfHost.Layout(window, window.MinWidth - 2, 460);
            root.InvalidateMeasure();
            root.Measure(new Size(window.MinWidth - 2, 460));
            root.Arrange(new Rect(0, 0, window.MinWidth - 2, 460));
            root.UpdateLayout();
            var groups = Descendants(root).OfType<SettingsSection>().Where(group => group.ActualWidth > 0 && IsExpanded(group)).ToArray();
            Assert.NotEmpty(groups);
            Assert.All(groups, group => Assert.InRange(group.ActualWidth, 500, 560));
            Assert.True(groups.Max(group => group.ActualWidth) - groups.Min(group => group.ActualWidth) < 1,
                string.Join(", ", groups.Select(group => $"{group.Header}: {group.ActualWidth}")));
            window.Close();
        }
        model.Section = SettingsSectionKind.Ringtone;
        Assert.Equal(SettingsSectionKind.Audio, model.Section);
        Assert.True(model.ShowsAudio);
    });

    [Fact]
    public void Иллюстрации_обработки_перекрашиваются_при_смене_темы() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Light };
        var preview = new ProcessingPreview { IsActive = true };
        var window = new Window { Content = preview };
        preview.Measure(new Size(102, 24));
        preview.Arrange(new Rect(0, 0, 102, 24));
        preview.UpdateLayout();
        var light = ((SolidColorBrush)preview.Foreground).Color;
        appearance.Appearance = Appearance.Dark;
        preview.UpdateLayout();
        Assert.NotEqual(light, ((SolidColorBrush)preview.Foreground).Color);
        window.Close();
    });

    [Fact]
    public void Значок_панели_задач_сохраняется_при_смене_короны_заголовка() => WpfHost.Run(() =>
    {
        var window = new Window();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            SystemCaption.Apply(window, false);
            var taskbarIcon = SendMessage(handle, 0x007F, 1, 0);
            var lightCaption = window.Icon;
            Assert.NotEqual(0, taskbarIcon);
            Assert.True(GetIconInfo(taskbarIcon, out var iconInfo));
            try
            {
                Assert.NotEqual(0, GetObject(iconInfo.ColorBitmap, Marshal.SizeOf<NativeBitmap>(), out var bitmap));
                Assert.True(SystemCaption.TaskbarSourceSize >= 256);
                Assert.InRange(bitmap.Width, 32, 256);
                Assert.Equal(bitmap.Width, bitmap.Height);
            }
            finally
            {
                _ = DeleteObject(iconInfo.ColorBitmap);
                _ = DeleteObject(iconInfo.MaskBitmap);
            }
            SystemCaption.Apply(window, true);
            Assert.Equal(taskbarIcon, SendMessage(handle, 0x007F, 1, 0));
            Assert.NotSame(lightCaption, window.Icon);
            Assert.NotEqual(0, SendMessage(handle, 0x007F, 0, 0));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Управление_в_боковом_меню_а_закрепление_справа_от_адреса_АТС() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Light };
        using var sounds = new SignalSoundPlayer();
        var settings = new AppSettings();
        settings.Account.Username = "176";
        settings.Account.Domain = "pbx.example";
        settings.Panel.Mode = PanelMode.Managed;
        settings.Panel.LockedWorkFormat = "office";
        var model = new SettingsViewModel(settings);
        var window = new SettingsWindow(model, appearance, sounds);
        var requested = false;
        window.AdministrationRequested += () => requested = true;
        var root = WpfHost.Layout(window, 780, 600);
        var admin = (Button)window.FindName("AdministrationButton");
        var unlock = (Button)window.FindName("UnlockWorkplaceButton");
        Assert.True(admin.TranslatePoint(new Point(), root).X < 146);
        admin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(requested);
        Assert.Same(model.UnlockWorkplace, unlock.Command);
        var domain = Descendants(root).OfType<TextBlock>().Single(block => block.Text == "pbx.example");
        Assert.True(unlock.TranslatePoint(new Point(), root).X > domain.TranslatePoint(new Point(), root).X);
        Assert.Equal(Visibility.Visible, unlock.Visibility);
        Assert.InRange(Math.Abs(domain.TranslatePoint(new Point(0, domain.ActualHeight / 2), root).Y
            - unlock.TranslatePoint(new Point(0, unlock.ActualHeight / 2), root).Y), 0, 1);
        window.Close();
    });

    private static bool IsExpanded(DependencyObject child)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint icon, out NativeIconInfo info);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetObject(nint bitmap, int size, out NativeBitmap info);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIconInfo
    {
        public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }
}
