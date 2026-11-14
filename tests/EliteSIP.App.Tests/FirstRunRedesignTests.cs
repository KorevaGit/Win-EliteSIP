using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EliteSIP.AdminAccess;
using EliteSIP.App.FirstRun;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class FirstRunRedesignTests
{
    [Fact]
    public void Ручная_настройка_остаётся_черновиком_до_завершения()
    {
        var settings = new AppSettings();
        using var model = new FirstRunViewModel(settings, new AdminAccessState());
        model.Begin();
        Assert.True(model.ShowsPairing);
        Assert.False(model.CanGoForward);
        model.UseManualSetup();
        model.Username = "176";
        model.DisplayName = "Office";
        model.OfficeAddress = "pbx.test";
        model.SipPassword = "test-secret";
        Assert.True(model.CanGoForward);
        Assert.False(settings.Setup.IsCompleted);
        Assert.NotEqual("176", settings.Account.Username);
        model.AdminPassword = "admin-test";
        model.RepeatedAdminPassword = "different";
        Assert.False(model.CanGoForward);
        model.AdminPassword = model.RepeatedAdminPassword = string.Empty;
        model.Forward();
        Assert.True(model.ShowsAppearance);
        model.Back();
        Assert.True(model.ShowsUser);
        Assert.Equal("test-secret", model.SipPassword);
        model.Forward();
        model.Forward();
        Assert.True(model.ShowsFinale);
        Assert.Equal("176", model.ProfileNumber);
        model.Language = settings.Appearance.Language is LanguageSetting.English ? LanguageSetting.Russian : LanguageSetting.English;
        model.Complete();
        Assert.True(settings.Setup.IsCompleted);
        Assert.Equal("test-secret", settings.Credentials.Password());
        Assert.True(model.LanguageChanges);
    }

    [Theory]
    [InlineData(Appearance.Light, "ru")]
    [InlineData(Appearance.Dark, "ru")]
    [InlineData(Appearance.Light, "en")]
    [InlineData(Appearance.Dark, "en")]
    public void Шаги_помещаются_без_прокрутки_и_не_двигают_нижние_кнопки(Appearance theme, string language) => WpfHost.Run(() =>
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        try
        {
            WpfHost.UseTheme(theme);
            Point? footerPosition = null;
            foreach (var (stage, paired) in new[] { (FirstRunStep.Pair, false), (FirstRunStep.Pair, true),
                (FirstRunStep.User, false), (FirstRunStep.Appearance, false), (FirstRunStep.Finale, false) })
            {
                using var model = new FirstRunViewModel(new AppSettings(), new AdminAccessState())
                {
                    Username = "176", DisplayName = "Office", AdminPassword = "one", RepeatedAdminPassword = "two",
                };
                WpfHost.SetPrivate(model, nameof(FirstRunViewModel.PairedNumber), "176");
                WpfHost.SetPrivate(model, nameof(FirstRunViewModel.PairedEmployee), "Office");
                WpfHost.SetPrivate(model, nameof(FirstRunViewModel.IsPaired), paired);
                WpfHost.SetPrivate(model, nameof(FirstRunViewModel.Step), stage);
                if (stage is FirstRunStep.Pair && !paired)
                {
                    WpfHost.SetPrivate(model, nameof(FirstRunViewModel.PairCode), "948-661");
                    WpfHost.SetPrivate(model, nameof(FirstRunViewModel.PairQr), QrImage.Render("elitesip://pair?test=preview"));
                    WpfHost.SetPrivate(model, nameof(FirstRunViewModel.PairStatus), EliteSIP.App.Resources.Strings.Get("FirstRunPairWaiting"));
                }
                var window = new FirstRunWindow(model, null!);
                var root = WpfHost.Layout(window, 558, 406);
                var button = (Button)window.FindName("ForwardButton");
                var point = button.TranslatePoint(default, root);
                if (footerPosition is { } expected) { Assert.Equal(expected, point); }
                footerPosition = point;
                Assert.Null(window.FindName("StepScroll"));
                var content = (Grid)window.FindName("StepContent");
                Assert.True(content.DesiredSize.Height <= content.ActualHeight + 0.5);
                var progress = (Grid)window.FindName("WizardProgress");
                var left = progress.TranslatePoint(default, root).X;
                Assert.InRange(Math.Abs(left + progress.ActualWidth / 2 - root.ActualWidth / 2), 0, 1);
                if (stage is FirstRunStep.Pair && !paired)
                {
                    var status = (TextBlock)window.FindName("PairingStatus");
                    var statusBottom = status.TranslatePoint(new Point(0, status.ActualHeight), content).Y;
                    Assert.True(statusBottom <= content.ActualHeight + 0.5);
                    var qr = (Image)window.FindName("PairingQr");
                    Assert.True(qr.ActualHeight >= 120 && qr.ActualHeight <= 121,
                        $"QR={qr.ActualHeight}, model code={model.HasPairCode}, paired={model.IsPaired}, pairing={model.ShowsPairing}, card={((Border)window.FindName("PairingCard")).Visibility}");
                    var card = (Border)window.FindName("PairingCard");
                    Assert.True(card.ActualWidth > 500);
                    if (language == "ru" && theme == Appearance.Dark)
                    {
                        var directory = new DirectoryInfo(AppContext.BaseDirectory);
                        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinEliteSIP.slnx"))) { directory = directory.Parent; }
                        Assert.NotNull(directory);
                        var output = Path.Combine(directory.FullName, "artifacts", "wizard-checks");
                        Directory.CreateDirectory(output);
                        var bitmap = new RenderTargetBitmap(558, 406, 96, 96, PixelFormats.Pbgra32);
                        var backdrop = new DrawingVisual();
                        using (var canvas = backdrop.RenderOpen()) { canvas.DrawRectangle((Brush)window.FindResource("PanelWindowBackgroundBrush"), null, new Rect(0, 0, 558, 406)); }
                        bitmap.Render(backdrop);
                        bitmap.Render(root);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(output, "pairing.png"));
                        encoder.Save(file);
                    }
                }
                Assert.True(point.Y + button.ActualHeight <= root.ActualHeight);
            }
        }
        finally { CultureInfo.CurrentUICulture = originalCulture; }
    });
}
