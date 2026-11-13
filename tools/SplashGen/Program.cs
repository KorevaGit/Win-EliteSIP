using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var assets = Path.GetFullPath(args.Length > 0 ? args[0] : "src/EliteSIP.App/Assets");
        var logo = new BitmapImage(new Uri(Path.Combine(assets, "AppIcon", "icon_512x512.png")));
        foreach (var dark in new[] { false, true })
        {
            var foreground = new SolidColorBrush(dark ? Color.FromRgb(242, 242, 242) : Color.FromRgb(35, 35, 35));
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new Image { Source = logo, Width = 78, Height = 78, Margin = new Thickness(0, 0, 0, 13) });
            content.Children.Add(new TextBlock { Text = "EliteSIP", FontFamily = new FontFamily("Segoe UI"), FontSize = 22,
                FontWeight = FontWeights.SemiBold, Foreground = foreground, HorizontalAlignment = HorizontalAlignment.Center });
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
            foreach (var opacity in new[] { 0.7, 0.4, 0.2 })
            {
                dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = foreground, Opacity = opacity, Margin = new Thickness(3, 0, 3, 0) });
            }
            content.Children.Add(dots);
            var surface = new Border
            {
                Width = 300, Height = 210, CornerRadius = new CornerRadius(16), Child = content,
                Background = new LinearGradientBrush(dark ? Color.FromRgb(53, 53, 53) : Colors.White,
                    dark ? Color.FromRgb(30, 30, 30) : Color.FromRgb(237, 237, 237), 90),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(dark ? Color.FromRgb(67, 67, 67) : Color.FromRgb(222, 222, 222)),
            };
            TextOptions.SetTextFormattingMode(surface, TextFormattingMode.Display);
            surface.Measure(new Size(300, 210));
            surface.Arrange(new Rect(0, 0, 300, 210));
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(300, 210, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(assets, dark ? "Splash.png" : "Splash.Light.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"300×210: {path}"));
        }
    }
}
