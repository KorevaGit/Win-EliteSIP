using EliteSIP.AdminAccess;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.Tests;

/// <summary>
/// Раскладка конфигурации Spark по профилям — как на macOS 0.1.51.
/// </summary>
public sealed class ConfigApplyTests
{
    private const string InstallationID = "4def1c28841b17be078d88f914636d04";

    private readonly List<string> _log = [];

    [Fact]
    public void Один_номер_ложится_в_прежний_профиль_машины()
    {
        var settings = Machine();
        var original = settings.Account.ProfileId;

        var change = Apply(settings, Config(1, number: "172", employee: "Смирнов П."));

        Assert.Equal(original, settings.Account.ProfileId);
        Assert.Equal(original, settings.Panel.MainProfileId);
        Assert.Equal("172", settings.Account.Username);
        Assert.Equal("Смирнов П.", settings.Account.DisplayName);
        Assert.Equal("пароль-172", settings.Credentials.Password());
        Assert.Empty(settings.Profiles);
        Assert.True(change.RegistrationChanged);

        // Первая настройка — не смена номера.
        Assert.Null(change.NewNumber);
    }

    [Fact]
    public void Три_номера_дают_три_профиля_со_стабильными_идентификаторами()
    {
        var settings = Machine();
        var original = settings.Account.ProfileId;

        Apply(settings, Lines(1, ("main", "300", "Колл-центр"), ("line-17", "301", "Линия 2"), ("line-18", "302", "Линия 3")));

        Assert.Equal(original, settings.Account.ProfileId);
        Assert.Equal("300", settings.Account.Username);
        Assert.Equal("Колл-центр", settings.Account.DisplayName);

        Assert.Equal(2, settings.Profiles.Count);
        var second = settings.Profiles.Single(profile => profile.Username == "301");
        Assert.Equal(MachineConfig.LineProfileID(InstallationID, "line-17"), second.ProfileId);
        Assert.Equal("Линия 2", second.DisplayName);
        Assert.Equal("пароль-301", second.Password());

        // Новые профили наследуют адрес АТС и площадку.
        Assert.Equal("192.168.1.2", second.Domain);
        Assert.Equal(WorkplaceSite.Office, second.Site);
    }

    [Fact]
    public void Переименование_номера_сохраняет_его_профиль_а_удалённый_уходит()
    {
        var settings = Machine();
        Apply(settings, Lines(1, ("main", "300", "К"), ("line-17", "301", "Линия 2"), ("line-18", "302", "Линия 3")));
        var kept = MachineConfig.LineProfileID(InstallationID, "line-17");

        Apply(settings, Lines(2, ("main", "300", "К"), ("line-17", "401", "Продажи")));

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(kept, profile.ProfileId);
        Assert.Equal("401", profile.Username);
        Assert.Equal("Продажи", profile.DisplayName);
        Assert.Contains(_log, line => line.Contains("302", StringComparison.Ordinal));
    }

    [Fact]
    public void Активный_дополнительный_номер_остаётся_активным()
    {
        var settings = Machine();
        Apply(settings, Lines(1, ("main", "300", "К"), ("line-17", "301", "Линия 2")));
        settings.SwitchProfile(MachineConfig.LineProfileID(InstallationID, "line-17"));
        Assert.Equal("301", settings.Account.Username);

        var change = Apply(settings, Lines(2, ("main", "300", "К"), ("line-17", "301", "Линия 2")));

        Assert.Equal("301", settings.Account.Username);
        Assert.False(change.RegistrationChanged);
    }

    [Fact]
    public void Убранный_активный_номер_уступает_место_основному_и_перерегистрирует()
    {
        var settings = Machine();
        var main = settings.Account.ProfileId;
        Apply(settings, Lines(1, ("main", "300", "К"), ("line-17", "301", "Линия 2")));
        settings.SwitchProfile(MachineConfig.LineProfileID(InstallationID, "line-17"));

        var change = Apply(settings, Config(2, number: "300", employee: "К"));

        Assert.Equal(main, settings.Account.ProfileId);
        Assert.Equal("300", settings.Account.Username);
        Assert.Empty(settings.Profiles);
        Assert.True(change.RegistrationChanged);
    }

    [Fact]
    public void Смена_основного_номера_сообщается_и_перерегистрирует()
    {
        var settings = Machine();
        Apply(settings, Config(1, number: "172", employee: "С"));

        var change = Apply(settings, Config(2, number: "205", employee: "С"));

        Assert.Equal("205", change.NewNumber);
        Assert.True(change.RegistrationChanged);
        Assert.Equal("205", settings.Panel.LastNumberNotice);
    }

    [Fact]
    public void Та_же_ревизия_без_правок_регистрацию_не_трогает()
    {
        var settings = Machine();
        Apply(settings, Config(1, number: "172", employee: "С"));

        var change = Apply(settings, Config(2, number: "172", employee: "С"));

        Assert.False(change.RegistrationChanged);
        Assert.Null(change.NewNumber);
    }

    [Fact]
    public void Формат_работы_меняет_площадку_и_адрес_у_всех_профилей()
    {
        var settings = Machine();
        Apply(settings, Lines(1, ("main", "300", "К"), ("line-17", "301", "Л")));

        Apply(settings, Lines(2, ("main", "300", "К"), ("line-17", "301", "Л")) with { WorkFormat = "remote" });

        Assert.Equal(WorkplaceSite.Remote, settings.Account.Site);
        Assert.Equal("crm.example.com", settings.Account.Domain);
        Assert.Equal(WorkplaceSite.Remote, settings.Profiles[0].Site);
        Assert.Equal("crm.example.com", settings.Profiles[0].Domain);
    }

    [Fact]
    public void Переключение_профиля_меняет_местами_активный_и_сохранённый()
    {
        var settings = Machine();
        Apply(settings, Lines(1, ("main", "300", "К"), ("line-17", "301", "Л")));
        var main = settings.Account.ProfileId;
        var second = MachineConfig.LineProfileID(InstallationID, "line-17");

        Assert.True(settings.SwitchProfile(second));

        Assert.Equal(second, settings.Account.ProfileId);
        Assert.Equal("пароль-301", settings.Credentials.Password());
        Assert.Equal(main, Assert.Single(settings.Profiles).ProfileId);
        Assert.False(settings.SwitchProfile(second));
    }

    private ConfigChange Apply(AppSettings settings, MachineConfig config)
        => settings.Apply(config, new AdminAccessState(), _log.Add);

    private static AppSettings Machine()
    {
        var settings = new AppSettings();
        settings.Pbx.OfficeAddress = "192.168.1.2";
        settings.Pbx.RemoteAddress = "crm.example.com";
        settings.Panel.InstallationID = InstallationID;
        return settings;
    }

    private static MachineConfig Config(int revision, string number, string employee) => new()
    {
        InstallationID = InstallationID,
        Revision = revision,
        Employee = employee,
        Number = number,
        SipPassword = "пароль-" + number,
        WorkFormat = "office",
    };

    private static MachineConfig Lines(int revision, params (string ID, string Number, string Label)[] lines) => new()
    {
        InstallationID = InstallationID,
        Revision = revision,
        Number = lines[0].Number,
        SipPassword = "пароль-" + lines[0].Number,
        WorkFormat = "office",
        Lines = [.. lines.Select(line => new MachineConfig.Line(line.ID, line.Number, "пароль-" + line.Number, line.Label))],
    };
}
