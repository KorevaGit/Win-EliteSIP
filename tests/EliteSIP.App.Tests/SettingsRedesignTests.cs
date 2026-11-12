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
    [Fact]
    public void Значок_находит_ресурс_после_присоединения_к_окну() => WpfHost.Run(() =>
    {
        var icon = new Icon { Glyph = "local.settings.test" };
        Assert.Null(icon.Shape);
        var shape = new Canvas { Width = 24, Height = 24 };
        shape.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M2 12L12 2L22 12"), Stroke = Brushes.White });
        var window = new Window { Content = icon, Width = 80, Height = 80, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.Add("local.settings.test", shape);
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(icon.IsLoaded);
            Assert.Same(shape, icon.Shape);
            Assert.True(shape.ActualWidth > 0);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Проверка_меняет_обе_кнопки_на_остановку_и_сбрасывает_уровни() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Dark };
        using var sounds = new SignalSoundPlayer();
        var stopped = 0;
        var model = new SettingsViewModel(new AppSettings()) { Section = SettingsSectionKind.Audio, OnStopSelfTest = () => stopped++ };
        var window = new SettingsWindow(model, appearance, sounds);
        try
        {
            var root = WpfHost.Layout(window, 848, 460);
            var mic = (Button)window.FindName("MicrophoneTestButton");
            var record = (Button)window.FindName("SelfTestRecordButton");
            model.ReportSelfTestRunning(true);
            root.UpdateLayout();
            Assert.Contains(Descendants(mic).OfType<TextBlock>(), text => text.Text == Resources.Strings.Get("AudioSelfTestStop"));
            Assert.Contains(Descendants(record).OfType<TextBlock>(), text => text.Text == Resources.Strings.Get("AudioSelfTestStop"));
            model.RunSelfTest.Execute(null);
            Assert.Equal(1, stopped);
            model.ReportSelfTest("готово");
            model.ReportSelfTestRunning(false);
            root.UpdateLayout();
            Assert.False(model.IsSelfTestRunning);
            Assert.Equal(0, model.VoiceLevel);
            Assert.Equal(0, model.SoundLevel);
            Assert.Equal("0:00", model.SelfTestElapsed);
            Assert.Equal("готово", model.SelfTestResult);
            Assert.DoesNotContain(Descendants(mic).OfType<TextBlock>(), text => text.Text == Resources.Strings.Get("AudioSelfTestStop"));
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(Appearance.Light)]
    [InlineData(Appearance.Dark)]
    public void При_смене_вкладок_иконки_видны_а_навигация_не_сдвигается(Appearance theme) => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = theme };
        using var sounds = new SignalSoundPlayer();
        var model = new SettingsViewModel(new AppSettings());
        var window = new SettingsWindow(model, appearance, sounds) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            var nav = Descendants(root).OfType<ListBox>().Single();
            var first = (ListBoxItem)nav.ItemContainerGenerator.ContainerFromIndex(0);
            var label = Descendants(first).OfType<TextBlock>().Single();
            var before = label.TranslatePoint(new Point(), root);
            foreach (var section in new[] { SettingsSectionKind.Audio, SettingsSectionKind.Support, SettingsSectionKind.Appearance, SettingsSectionKind.Work })
            {
                model.Section = section;
                window.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                var icons = Descendants(root).OfType<Icon>().Where(icon => icon.IsVisible).ToArray();
                Assert.NotEmpty(icons);
                Assert.Equal(icons.Length, icons.Select(icon => icon.Shape).Distinct(ReferenceEqualityComparer.Instance).Count());
                Assert.All(icons, icon =>
                {
                    var shape = Assert.IsAssignableFrom<FrameworkElement>(icon.Shape);
                    Assert.True(shape.ActualWidth > 0, icon.Glyph);
                    Assert.True(shape.ActualHeight > 0, icon.Glyph);
                    Assert.NotNull(VisualTreeHelper.GetParent(shape));
                    Assert.All(Descendants(shape).OfType<System.Windows.Shapes.Path>(), path => Assert.True(path.Stroke is not null || path.Fill is not null, icon.Glyph));
                });
                var after = label.TranslatePoint(new Point(), root);
                Assert.Equal(before, after);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void После_остановки_шкала_не_оставляет_горящий_пик() => WpfHost.Run(() =>
    {
        var meter = new LevelMeter { ActiveBrush = Brushes.White, TrackBrush = Brushes.Black, Level = 1 };
        meter.Measure(new Size(240, 10));
        meter.Arrange(new Rect(0, 0, 240, 10));
        meter.Level = 0;
        meter.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(240, 10, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(meter);
        var pixels = new byte[240 * 10 * 4];
        bitmap.CopyPixels(pixels, 240 * 4, 0);
        Assert.DoesNotContain(Enumerable.Range(0, 240 * 10), pixel => pixels[pixel * 4] > 0);
    });

    [Theory]
    [InlineData(Appearance.Light, "#FF242424", "#FF686868", "#FFFFFFFF", "#FFE9E9E9", "#FFF9F9F9", "#FF168016")]
    [InlineData(Appearance.Dark, "#FFF2F2F2", "#FFBCBCBC", "#FF363636", "#FF2B2B2B", "#FF202020", "#FF468D35")]
    public void Цвета_настроек_совпадают_с_макетом(Appearance theme, string ink, string secondary, string top, string bottom, string field, string toggle) => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = theme };
        using var sounds = new SignalSoundPlayer();
        var settings = new AppSettings();
        settings.Account.Username = "87654321";
        settings.Account.DisplayName = "Не показывать профиль";
        var window = new SettingsWindow(new SettingsViewModel(settings), appearance, sounds);
        var root = WpfHost.Layout(window, 848, 460);
        Assert.Equal(500, window.Height);
        Assert.DoesNotContain(Descendants(root).OfType<TextBlock>(), block => block.Text == "87654321" || block.Text == "Не показывать профиль");
        Assert.Equal(ink, ((SolidColorBrush)window.FindResource("TextPrimaryBrush")).Color.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(secondary, ((SolidColorBrush)window.FindResource("TextSecondaryBrush")).Color.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var surface = (LinearGradientBrush)window.FindResource("SettingsSurfaceBrush");
        Assert.Equal(top, surface.GradientStops[0].Color.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(bottom, surface.GradientStops[1].Color.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(field, ((SolidColorBrush)window.FindResource("SettingsFieldBrush")).Color.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(toggle, ((SolidColorBrush)window.FindResource("SettingsSwitchBrush")).Color.ToString(System.Globalization.CultureInfo.InvariantCulture));
        window.Close();
    });

    [Fact]
    public void Компактное_окно_прокручивается_а_вывод_мелодии_сохраняет_выбор() => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = Appearance.Light };
        using var sounds = new SignalSoundPlayer();
        var settings = new AppSettings();
        var model = new SettingsViewModel(settings) { Section = SettingsSectionKind.Audio };
        var played = 0;
        var playbackModel = new SettingsViewModel(new AppSettings()) { OnPlayRecording = () => played++ };
        playbackModel.PlayRecording.Execute(null);
        Assert.Equal(0, played);
        playbackModel.ReportRecording(true);
        playbackModel.PlayRecording.Execute(null);
        Assert.Equal(1, played);
        var window = new SettingsWindow(model, appearance, sounds);
        var root = WpfHost.Layout(window, 718, 400);
        root.UpdateLayout();
        var scroll = (ScrollViewer)window.FindName("SettingsScroller");
        Assert.True(scroll.ScrollableHeight > 0);
        scroll.ScrollToEnd();
        root.UpdateLayout();
        Assert.True(scroll.VerticalOffset > 0);
        var output = (ComboBox)window.FindName("RingtoneOutputChoice");
        output.SelectedValue = RingtoneOutput.CallDevice;
        Assert.Equal(RingtoneOutput.CallDevice, settings.Ringtone.Output);
        window.Close();
    });

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
