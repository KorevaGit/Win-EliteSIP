using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EliteSIP.AdminAccess;
using EliteSIP.App.Admin;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class AdministrationRedesignTests
{
    private static AppSettings Configured()
    {
        var settings = new AppSettings();
        settings.Account.Username = "176";
        settings.Account.Domain = settings.Pbx.OfficeAddress = "pbx.test";
        settings.Credentials.SetPassword("active-test-secret");
        var extra = new ProfileSetting { Username = "172", DisplayName = "Second", Domain = "pbx.test" };
        extra.SetPassword("other-test-secret");
        settings.Profiles.Add(extra);
        return settings;
    }

    [Fact]
    public void Учётки_добавляются_черновиком_и_сохраняются_под_DPAPI() => WpfHost.Run(() =>
    {
        var settings = Configured();
        var activeId = settings.Account.ProfileId;
        var oldCipher = settings.Profiles[0].ProtectedPassword;
        var model = new AdministrationViewModel(settings, new AdminAccessState());
        Assert.False(model.IsDirty);
        Assert.Equal("active-test-secret", model.Accounts.Single(account => account.IsActive).Password);
        model.AddAccount();
        Assert.True(model.HasAccountError);
        Assert.False(model.CanSave);
        Assert.Single(settings.Profiles);
        model.Revert();
        Assert.Equal(2, model.Accounts.Count);
        model.AddAccount();
        var draft = model.Accounts.Last();
        draft.Username = "174";
        draft.DisplayName = "New account";
        draft.Password = "new-draft-password";
        Assert.True(model.CanSave);
        var reconnects = 0;
        var saving = new AdministrationViewModel(settings, new AdminAccessState()) { OnConnectionChanged = () => reconnects++ };
        saving.AddAccount();
        var toSave = saving.Accounts.Last();
        toSave.Username = draft.Username;
        toSave.DisplayName = draft.DisplayName;
        toSave.Password = draft.Password;
        saving.Save();
        Assert.Equal(0, reconnects);
        Assert.Equal(activeId, settings.Account.ProfileId);
        Assert.Equal(oldCipher, settings.Profiles.Single(profile => profile.Username == "172").ProtectedPassword);
        var added = settings.Profiles.Single(profile => profile.Username == "174");
        Assert.Equal("new-draft-password", added.Password());
        var path = Path.Combine(Path.GetTempPath(), $"admin-accounts-{Guid.NewGuid():N}.json");
        try
        {
            settings.Save(path);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("new-draft-password", json, StringComparison.Ordinal);
            Assert.DoesNotContain("active-test-secret", json, StringComparison.Ordinal);
            var restored = AppSettings.Load(path);
            Assert.Equal(added.ProfileId, restored.Profiles.Single(profile => profile.Username == "174").ProfileId);
            Assert.True(restored.SwitchProfile(added.ProfileId));
            Assert.Equal("174", restored.Account.Username);
            Assert.Equal("new-draft-password", restored.Credentials.Password());
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public void Дубликаты_и_смена_активного_профиля_не_портят_учётки() => WpfHost.Run(() =>
    {
        var settings = Configured();
        var model = new AdministrationViewModel(settings, new AdminAccessState());
        model.AddAccount();
        model.Accounts.Last().Username = "176";
        Assert.True(model.HasAccountError);
        Assert.Throws<InvalidOperationException>(model.Save);
        Assert.Single(settings.Profiles);
        model.Revert();
        model.Accounts.Single(account => account.IsActive).DisplayName = "edited";
        Assert.True(settings.SwitchProfile(settings.Profiles[0].ProfileId));
        Assert.True(model.HasAccountError);
        Assert.Throws<InvalidOperationException>(model.Save);
        Assert.Equal("172", settings.Account.Username);
        model.Revert();
        Assert.Equal("172", model.Accounts.Single(account => account.IsActive).Username);
        Assert.Equal("other-test-secret", model.SipPassword);
    });

    [Fact]
    public void Видимый_пароль_можно_изменить_и_явно_очистить() => WpfHost.Run(() =>
    {
        var settings = Configured();
        var calls = 0;
        var model = new AdministrationViewModel(settings, new AdminAccessState()) { OnConnectionChanged = () => calls++ };
        model.Accounts.Single(account => account.IsActive).Password = string.Empty;
        model.Save();
        Assert.False(settings.Credentials.HasPassword);
        Assert.Equal(1, calls);
        model.Revert();
        Assert.Equal(string.Empty, model.Accounts.Single(account => account.IsActive).Password);
    });

    [Theory]
    [InlineData(Appearance.Light)]
    [InlineData(Appearance.Dark)]
    public void Все_разделы_управления_доступны_при_минимальной_ширине(Appearance theme) => WpfHost.Run(() =>
    {
        using var appearance = new AppearanceService(Application.Current) { Appearance = theme };
        var model = new AdministrationViewModel(Configured(), new AdminAccessState());
        Assert.Equal(5, model.Sections.Count);
        var window = new AdministrationWindow(model, appearance) { Width = 740, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            foreach (var section in model.Sections)
            {
                model.Section = section.Kind;
                window.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("AdminScroller");
                Assert.True(scroll.ViewportWidth > 480);
                var root = (FrameworkElement)window.Content;
                Assert.All(Walk(root).OfType<TextBox>().Where(box => box.IsVisible), box =>
                {
                    var x = box.TranslatePoint(new Point(box.ActualWidth, 0), root).X;
                    Assert.True(x <= root.ActualWidth + 1, $"Text field extends beyond the window: {x}");
                });
            }
            model.Section = AdminSectionKind.Account;
            window.UpdateLayout();
            var accountItems = (ItemsControl)window.FindName("AccountItems");
            Assert.Equal(2, accountItems.Items.Count);
            Assert.Contains(Walk(accountItems).OfType<TextBox>(), box => box.Text == "active-test-secret");
            model.Section = AdminSectionKind.Access;
            Assert.True(model.ShowsSystem && model.ShowsAccess);
            model.Section = AdminSectionKind.Queues;
            Assert.True(model.ShowsIncoming && model.ShowsQueues);
        }
        finally { model.Revert(); window.Close(); }
    });

    [Fact]
    public void Нижние_кнопки_панели_равны_полю_номера() => WpfHost.Run(() =>
    {
        WpfHost.UseTheme(Appearance.Dark);
        var window = new PanelWindow(new PanelViewModel { CanPlaceCall = true });
        var root = WpfHost.Layout(window, 320, 700);
        root.UpdateLayout();
        var input = (Grid)window.FindName("NumberInputArea");
        var call = (Button)window.FindName("PrimaryCallButton");
        var history = (Button)window.FindName("HistoryActionButton");
        Assert.Equal(72, input.ActualHeight);
        Assert.Equal(input.ActualHeight, call.ActualHeight);
        Assert.Equal(input.ActualHeight, history.ActualHeight);
        window.Close();
    });

    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Walk(child)) { yield return nested; }
        }
    }
}
