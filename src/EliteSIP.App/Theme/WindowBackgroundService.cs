using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Theme;

/// <summary>Обрабатывает только изображение фона, без эффектов над текстом и кнопками.</summary>
internal sealed class WindowBackgroundService : IDisposable
{
    private static readonly string[] SurfaceKeys = ["PanelCallBrush", "PanelMacroBrush", "PanelControlBrush", "HistoryFilterBackgroundBrush", "SurfaceBrush", "SidebarBrush"];
    private static readonly string[] ColorKeys = ["SettingsSurfaceTopColor", "SettingsSurfaceBottomColor"];
    private readonly Application _application;
    private readonly AppearanceSettings _settings;
    private readonly AppearanceService _appearance;
    private readonly Action<string> _log;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _overrides = [];
    private BitmapSource? _source;
    private BitmapSource? _processed;
    private (int Brightness, int Blur) _processing;
    private ImageBrush? _composed;
    private (int Scale, bool Dark) _composition;
    private Color? _dominant;
    private (bool Dark, bool Wallpaper, BackgroundTransparency Transparency, Color? Ambient)? _surfaceState;
    private bool _paletteChanged = true;
    internal int CompositionCount { get; private set; }
    private bool _disposed;

    internal WindowBackgroundService(Application application, AppearanceSettings settings, AppearanceService appearance, Action<string> log)
    {
        _application = application;
        _settings = settings;
        _appearance = appearance;
        _log = log;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _timer.Tick += (_, _) => ApplyNow();
        _settings.PropertyChanged += OnChanged;
        AppearanceService.PaletteChanged += OnPaletteChanged;
        ApplyNow();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName?.StartsWith("Background", StringComparison.Ordinal) is not true) { return; }
        if (change.PropertyName == nameof(AppearanceSettings.BackgroundImagePath))
        {
            _source = null; _processed = null; _composed = null; _dominant = null;
        }
        _timer.Stop();
        _timer.Start();
    }

    private void OnPaletteChanged()
    {
        _paletteChanged = true;
        ApplyNow();
    }

    internal void ApplyNow()
    {
        if (_disposed) { return; }
        _timer.Stop();
        if (!_settings.HasBackgroundImage)
        {
            ClearOverrides();
            _source = null;
            _processed = null;
            _composed = null;
            _dominant = null;
            _surfaceState = null;
            SystemCaption.ApplyToOpenWindows(_application, _appearance.IsDark);
            return;
        }
        try
        {
            _source ??= BackgroundImages.Read(_settings.BackgroundImagePath);
            var processing = (_settings.BackgroundBrightness, _settings.BackgroundBlur);
            if (_processed is null || _processing != processing)
            {
                _processed = Process(_source, processing.BackgroundBrightness, processing.BackgroundBlur);
                _processing = processing;
                _composed = null;
            }
            var composition = (_settings.BackgroundScale, _appearance.IsDark);
            if (_composed is null || _composition != composition)
            {
                _composed = Compose(_processed, composition.BackgroundScale, composition.IsDark);
                _composition = composition;
                CompositionCount++;
            }
            var brush = _composed;
            var wallpaper = _appearance.Appearance == Appearance.Wallpaper;
            var ambient = _settings.BackgroundTranslucent || wallpaper ? _dominant ??= SampleColor(_source) : (Color?)null;
            var surfaces = (_appearance.IsDark, wallpaper, _settings.BackgroundTransparency, ambient);
            var updateSurfaces = _paletteChanged || _surfaceState != surfaces;
            if (!updateSurfaces)
            {
                if (!ReferenceEquals(_application.TryFindResource("PanelWindowBackgroundBrush"), brush))
                {
                    Put("PanelWindowBackgroundBrush", brush);
                    Put("WindowBackgroundBrush", brush);
                }
                return;
            }
            ClearOverrides();
            Put("PanelWindowBackgroundBrush", brush);
            Put("WindowBackgroundBrush", brush);
            Put("PersonalizationCaptionBrush", new SolidColorBrush(_appearance.IsDark
                ? Color.FromRgb(25, 25, 25) : Color.FromRgb(241, 241, 241)));
            if (wallpaper) { ApplyWallpaperPalette(ambient!.Value); }
            if (!_appearance.IsDark)
            {
                Put("TextSecondaryBrush", new SolidColorBrush(Color.FromRgb(65, 65, 65)));
                Put("TextTertiaryBrush", new SolidColorBrush(Color.FromRgb(87, 87, 87)));
                Put("SettingsSubColor", Color.FromRgb(65, 65, 65));
            }
            else
            {
                Put("TextSecondaryBrush", new SolidColorBrush(Color.FromRgb(225, 225, 225)));
                Put("TextTertiaryBrush", new SolidColorBrush(Color.FromRgb(195, 195, 195)));
                Put("SettingsSubColor", Color.FromRgb(225, 225, 225));
            }
            if (_settings.BackgroundTranslucent || ambient.HasValue)
            {
                foreach (var key in SurfaceKeys)
                {
                    if (_application.TryFindResource(key) is not Brush source) { continue; }
                    var translucent = source.CloneCurrentValue();
                    if (translucent is SolidColorBrush solid) { solid.Color = SurfaceColor(solid.Color, ambient, _appearance.IsDark); }
                    if (translucent is GradientBrush gradient)
                    {
                        foreach (var stop in gradient.GradientStops) { stop.Color = SurfaceColor(stop.Color, ambient, _appearance.IsDark); }
                    }
                    translucent.Opacity = SurfaceOpacity(_settings.BackgroundTransparency);
                    translucent.Freeze();
                    Put(key, translucent);
                }
                foreach (var key in ColorKeys)
                {
                    if (_application.TryFindResource(key) is not Color color) { continue; }
                    var tinted = SurfaceColor(color, ambient, _appearance.IsDark);
                    tinted.A = (byte)Math.Round(255 * SurfaceOpacity(_settings.BackgroundTransparency));
                    Put(key, tinted);
                }
            }
            _surfaceState = surfaces;
            _paletteChanged = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Runtime.InteropServices.COMException or ArgumentException or FileFormatException)
        {
            _source = null;
            _processed = null;
            _composed = null;
            ClearOverrides();
            _surfaceState = null;
            _log($"фоновое изображение недоступно: {error.GetType().Name}; используется обычная тема");
        }
        SystemCaption.ApplyToOpenWindows(_application, _appearance.IsDark);
    }

    internal static double SurfaceOpacity(BackgroundTransparency level) => level switch
    {
        BackgroundTransparency.Light => 0.96,
        BackgroundTransparency.Medium => 0.90,
        BackgroundTransparency.Strong => 0.82,
        _ => 1,
    };

    private void ApplyWallpaperPalette(Color hue)
    {
        // Keep text and semantic call/status colors intact; recolor all neutral chrome.
        string[] keys = ["PanelCallBrush", "PanelMacroBrush", "PanelControlBrush", "PanelHoverBrush",
            "PanelProfileBrush", "HistoryFilterBackgroundBrush", "SurfaceBrush", "SidebarBrush",
            "ControlBackgroundBrush", "ControlHoverBrush", "ControlPressedBrush", "NavHoverBrush",
            "PanelPressedBrush", "PanelSelectedBrush", "PanelProfileHoverBrush", "PanelMacroHoverBrush"];
        foreach (var key in keys)
        {
            if (_application.TryFindResource(key) is not Brush original) { continue; }
            var copy = original.CloneCurrentValue();
            if (copy is SolidColorBrush solid) { solid.Color = WallpaperColor(solid.Color, hue); }
            if (copy is GradientBrush gradient)
            {
                foreach (var stop in gradient.GradientStops) { stop.Color = WallpaperColor(stop.Color, hue); }
            }
            copy.Freeze(); Put(key, copy);
        }
        string[] colors = ["SettingsSurfaceTopColor", "SettingsSurfaceBottomColor", "SettingsFieldColor",
            "SettingsTrackColor", "SettingsLineColor", "SettingsEdgeColor"];
        foreach (var key in colors)
        {
            if (_application.TryFindResource(key) is Color color) { Put(key, WallpaperColor(color, hue)); }
        }
        Put("AccentBrush", new SolidColorBrush(Color.FromRgb((byte)(140 + hue.R * .4), (byte)(140 + hue.G * .4), (byte)(140 + hue.B * .4))));
    }

    internal static Color WallpaperColor(Color color, Color hue)
    {
        var level = Math.Clamp((color.R + color.G + color.B) / 3d, 28, 90);
        var max = Math.Max(1, (int)Math.Max(hue.R, Math.Max(hue.G, hue.B)));
        byte Channel(byte value) => (byte)Math.Clamp(level * (.35 + 1.1 * value / max), 0, 150);
        return Color.FromArgb(color.A, Channel(hue.R), Channel(hue.G), Channel(hue.B));
    }

    internal static Color SurfaceColor(Color color, Color? ambient, bool dark)
    {
        if (!ambient.HasValue) { return Color.FromRgb(color.R, color.G, color.B); }
        var tint = ambient.Value;
        var amount = dark ? 0.22 : 0.12;
        byte Mix(byte value, byte target) => (byte)Math.Round(value * (1 - amount) + target * amount);
        return Color.FromRgb(Mix(color.R, tint.R), Mix(color.G, tint.G), Mix(color.B, tint.B));
    }

    internal static Color SampleColor(BitmapSource image)
    {
        var tiny = new TransformedBitmap(image, new ScaleTransform(16d / image.PixelWidth, 16d / image.PixelHeight));
        var bitmap = new FormatConvertedBitmap(tiny, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var groups = new Dictionary<int, (long R, long G, long B, long Weight)>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            var key = (pixels[i + 2] / 32 << 6) | (pixels[i + 1] / 32 << 3) | pixels[i] / 32;
            groups.TryGetValue(key, out var group);
            groups[key] = (group.R + pixels[i + 2] * a, group.G + pixels[i + 1] * a,
                group.B + pixels[i] * a, group.Weight + a);
        }
        var dominant = groups.Values.OrderByDescending(group => group.Weight).FirstOrDefault();
        return dominant.Weight == 0 ? Colors.Gray : Color.FromRgb((byte)(dominant.R / dominant.Weight),
            (byte)(dominant.G / dominant.Weight), (byte)(dominant.B / dominant.Weight));
    }

    private void Put(string key, object value)
    {
        _application.Resources[key] = value;
        _overrides.Add(key);
    }

    private void ClearOverrides()
    {
        foreach (var key in _overrides) { _application.Resources.Remove(key); }
        _overrides.Clear();
    }

    internal static BitmapSource Process(BitmapSource source, int brightness, int blur)
    {
        var scale = Math.Min(1, 1024d / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource sized = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var converted = new FormatConvertedBitmap(sized, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var factor = Math.Clamp(brightness, 30, 170) / 100d;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                pixels[offset + channel] = (byte)Math.Clamp((int)Math.Round(pixels[offset + channel] * factor), 0, 255);
            }
        }
        var bitmap = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        if (blur <= 0) { return bitmap; }
        var image = new Image
        {
            Source = bitmap, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight,
            Effect = new BlurEffect { Radius = Math.Clamp(blur, 0, 24), RenderingBias = RenderingBias.Performance },
        };
        image.Measure(new Size(image.Width, image.Height));
        image.Arrange(new Rect(0, 0, image.Width, image.Height));
        var result = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(image);
        result.Freeze();
        return result;
    }

    internal static ImageBrush Compose(BitmapSource image, int scale, bool dark)
    {
        var imageBrush = new ImageBrush(image)
        {
            Stretch = Stretch.UniformToFill,
            RelativeTransform = new ScaleTransform(Math.Clamp(scale, 50, 200) / 100d, Math.Clamp(scale, 50, 200) / 100d, 0.5, 0.5),
        };
        var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(dark ? Brushes.Black : Brushes.White, null, rect);
            context.DrawRectangle(imageBrush, null, rect);
            context.DrawRectangle(new SolidColorBrush(dark ? Color.FromArgb(175, 0, 0, 0) : Color.FromArgb(195, 255, 255, 255)), null, rect);
        }
        // Flatten the image and theme veil once. During resize WPF only scales
        // one opaque bitmap instead of replaying a layered DrawingBrush per frame.
        var bitmap = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.LowQuality);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _timer.Stop();
        _settings.PropertyChanged -= OnChanged;
        AppearanceService.PaletteChanged -= OnPaletteChanged;
        ClearOverrides();
    }
}
