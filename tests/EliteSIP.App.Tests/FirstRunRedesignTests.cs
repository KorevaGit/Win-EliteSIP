using System.Windows;
using System.Windows.Controls;
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
    [InlineData(Appearance.Light)]
    [InlineData(Appearance.Dark)]
    public void Подтверждение_привязки_и_ошибка_пароля_не_двигают_нижние_кнопки(Appearance theme) => WpfHost.Run(() =>
    {
        WpfHost.UseTheme(theme);
        Point? footerPosition = null;
        foreach (var stage in new[] { FirstRunStep.Pair, FirstRunStep.User, FirstRunStep.Appearance, FirstRunStep.Finale })
        {
            using var model = new FirstRunViewModel(new AppSettings(), new AdminAccessState())
            {
                Username = "176", DisplayName = "Office", AdminPassword = "one", RepeatedAdminPassword = "two",
            };
            WpfHost.SetPrivate(model, nameof(FirstRunViewModel.PairedNumber), "176");
            WpfHost.SetPrivate(model, nameof(FirstRunViewModel.PairedEmployee), "Office");
            WpfHost.SetPrivate(model, nameof(FirstRunViewModel.IsPaired), stage is FirstRunStep.Pair);
            WpfHost.SetPrivate(model, nameof(FirstRunViewModel.Step), stage);
            var window = new FirstRunWindow(model, null!);
            var root = WpfHost.Layout(window, 558, 406);
            var button = (Button)window.FindName("ForwardButton");
            var point = button.TranslatePoint(default, root);
            if (footerPosition is { } expected) { Assert.Equal(expected, point); }
            footerPosition = point;
            var scroll = (ScrollViewer)window.FindName("StepScroll");
            Assert.InRange(scroll.ScrollableHeight, 0, 0.5);
            Assert.True(point.Y + button.ActualHeight <= root.ActualHeight);
        }
    });
}
