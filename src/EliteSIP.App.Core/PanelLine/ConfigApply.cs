using EliteSIP.AdminAccess;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>Что поменяла конфигурация — для тех, кто должен на это отозваться.</summary>
/// <param name="RegistrationChanged">Сменились номер, SIP-пароль или площадка: перерегистрироваться.</param>
/// <param name="NewNumber">Номер сменился на этот — сказать в панели. <c>null</c> — не сменился.</param>
/// <param name="PresetChanged">Другая предустановка: запросить файл предустановок сейчас, не ждать такта.</param>
internal readonly record struct ConfigChange(bool RegistrationChanged, string? NewNumber, bool PresetChanged);

/// <summary>
/// Накладывает конфигурацию машины из Spark на настройки.
/// </summary>
///
/// <remarks>
/// Одна дорога и для первой настройки после привязки, и для каждой правки
/// сотрудника в Spark. Порядок полей взят из macOS (0.1.51) вместе с его
/// ошибками, которые там уже исправлены: перерегистрация по смене номера,
/// пароль настроек на закрытой машине, снятие пароля пустым значением.
/// </remarks>
internal static class ConfigApply
{
    internal static ConfigChange Apply(
        this AppSettings settings,
        MachineConfig config,
        AdminAccessState adminAccess,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(config);

        var registrationChanged = false;
        string? newNumber = null;

        // Площадка одна на все номера сотрудника: формат работы — его, а не
        // номера. Неизвестное значение площадку не трогает.
        WorkplaceSite? site = config.WorkFormat switch
        {
            "office" => WorkplaceSite.Office,
            "remote" => WorkplaceSite.Remote,
            _ => null,
        };

        var lines = config.EffectiveLines;
        if (lines.Count > 0)
        {
            (registrationChanged, newNumber) = ApplyLines(settings, config, lines, site, log);
        }
        else if (site is { } only && only != settings.Account.Site)
        {
            settings.Account.Site = only;
            registrationChanged = true;
        }

        // Адрес регистрации активного профиля — из пары адресов предустановки
        // по площадке.
        var address = AddressFor(settings, settings.Account.Site);
        if (address.Length > 0 && address != settings.Account.Domain)
        {
            settings.Account.Domain = address;
            registrationChanged = true;
        }

        // Другая предустановка: прежняя ревизия к ней не относится, и новая
        // должна лечь целиком, даже если её номер меньше.
        var presetChanged = config.PresetID.Length > 0 && config.PresetID != settings.Panel.PresetID;
        if (presetChanged)
        {
            settings.Panel.PresetID = config.PresetID;
            settings.Panel.AppliedRevision = 0;
        }

        if (config.PresetName.Length > 0)
        {
            settings.Panel.PresetName = config.PresetName;
        }

        ApplyAdminPassword(settings, config.AdminPassword, adminAccess, log);

        settings.Panel.Mode = PanelMode.Managed;
        settings.Panel.AppliedConfigRevision = config.Revision;
        settings.Panel.AppliedConfigFingerprint = config.Fingerprint();
        settings.Panel.LastContactAt = DateTimeOffset.UtcNow;

        if (newNumber is not null)
        {
            settings.Panel.LastNumberNotice = newNumber;
        }

        return new ConfigChange(registrationChanged, newNumber, presetChanged);
    }

    /// <summary>
    /// Раскладывает номера сотрудника по профилям — как на macOS.
    /// </summary>
    ///
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>Основной номер (<c>main</c>) живёт в профиле, который
    ///   был у машины всегда: так сохраняется её история звонков.</description></item>
    ///   <item><description>Остальные — в профилях со стабильным идентификатором
    ///   (<see cref="MachineConfig.LineProfileID"/>): переименование номера в
    ///   Spark не теряет историю.</description></item>
    ///   <item><description>Профили, которых нет в конфигурации, на управляемой
    ///   машине удаляются.</description></item>
    ///   <item><description>Активный остаётся активным, если его номер ещё есть;
    ///   иначе активным становится основной.</description></item>
    ///   <item><description>Новые профили наследуют адрес текущего; площадка у
    ///   всех — из формата работы.</description></item>
    /// </list>
    /// </remarks>
    /// <returns>Сменилась ли регистрация активного профиля и на какой номер.</returns>
    private static (bool RegistrationChanged, string? NewNumber) ApplyLines(
        AppSettings settings,
        MachineConfig config,
        IReadOnlyList<MachineConfig.Line> lines,
        WorkplaceSite? site,
        Action<string> log)
    {
        var panel = settings.Panel;
        panel.MainProfileId ??= settings.Account.ProfileId;
        var mainId = panel.MainProfileId.Value;

        Guid ProfileOf(MachineConfig.Line line) => line.ID == MachineConfig.MainLineID || line.ID.Length == 0
            ? mainId
            : MachineConfig.LineProfileID(config.InstallationID, line.ID);

        var wanted = lines
            .GroupBy(ProfileOf)
            .Select(group => (ProfileId: group.Key, Line: group.First()))
            .ToList();
        var wantedIds = wanted.Select(item => item.ProfileId).ToHashSet();

        var activeBefore = settings.Account.ProfileId;
        var numberBefore = settings.Account.Username;
        var passwordBefore = settings.Credentials.ProtectedPassword;
        var siteBefore = settings.Account.Site;

        // Активного профиля в конфигурации нет — его место занимает основной.
        if (!wantedIds.Contains(activeBefore))
        {
            var main = settings.Profiles.FirstOrDefault(profile => profile.ProfileId == mainId);
            if (main is not null)
            {
                settings.Profiles.Remove(main);
                ProfileSwitch.Load(settings, main);
            }
            else
            {
                settings.Account.ProfileId = mainId;
            }

            log($"профиль {numberBefore} убран в Spark — активным стал основной");
        }

        var template = settings.Account.Domain;

        foreach (var (profileId, line) in wanted)
        {
            if (profileId == settings.Account.ProfileId)
            {
                settings.Account.Username = line.Number;
                if (line.SipPassword.Length > 0 && line.SipPassword != settings.Credentials.Password())
                {
                    settings.Credentials.SetPassword(line.SipPassword);
                }

                // Пустая подпись не затирает прежнюю: Spark мог её просто не знать.
                if (line.Label.Length > 0)
                {
                    settings.Account.DisplayName = line.Label;
                }

                if (site is { } active)
                {
                    settings.Account.Site = active;
                }

                continue;
            }

            var profile = settings.Profiles.FirstOrDefault(item => item.ProfileId == profileId);
            if (profile is null)
            {
                profile = new ProfileSetting
                {
                    ProfileId = profileId,
                    Domain = template,
                    Site = site ?? settings.Account.Site,
                };
                settings.Profiles.Add(profile);
            }

            profile.Username = line.Number;
            if (line.SipPassword.Length > 0 && line.SipPassword != profile.Password())
            {
                profile.SetPassword(line.SipPassword);
            }

            if (line.Label.Length > 0)
            {
                profile.DisplayName = line.Label;
            }

            if (site is { } other)
            {
                profile.Site = other;
            }

            var address = AddressFor(settings, profile.Site);
            if (address.Length > 0)
            {
                profile.Domain = address;
            }
        }

        foreach (var stale in settings.Profiles.Where(profile => !wantedIds.Contains(profile.ProfileId)).ToList())
        {
            settings.Profiles.Remove(stale);
            log($"профиль {stale.Username} убран: номера больше нет в Spark");
        }

        var registrationChanged = settings.Account.ProfileId != activeBefore
            || settings.Account.Username != numberBefore
            || settings.Credentials.ProtectedPassword != passwordBefore
            || settings.Account.Site != siteBefore;

        // «Сменил номер» — только если номер уже был: первая настройка не смена.
        var newNumber = numberBefore.Length > 0 && settings.Account.Username != numberBefore
            ? settings.Account.Username
            : null;

        return (registrationChanged, newNumber);
    }

    private static string AddressFor(AppSettings settings, WorkplaceSite site)
        => site is WorkplaceSite.Remote ? settings.Pbx.RemoteAddress : settings.Pbx.OfficeAddress;

    /// <summary>
    /// Пароль настроек из Spark: непустой ставится, пустой снимает прежний.
    /// </summary>
    ///
    /// <remarks>
    /// Без проверки «Управление открыто» — у сотрудника за закрытой машиной
    /// старого пароля нет, и первая версия на macOS именно на этом и не
    /// ставила пароль. И только если он действительно сменился: считать хеш
    /// заново на каждом опросе незачем.
    /// </remarks>
    private static void ApplyAdminPassword(
        AppSettings settings, string password, AdminAccessState adminAccess, Action<string> log)
    {
        var current = settings.Admin.ToCredential();

        if (password.Length == 0)
        {
            if (current is null)
            {
                return;
            }

            settings.Admin.From(credential: null);
            adminAccess.Restore(null);
            log("пароль настроек снят: у предустановки в Spark его нет");
            return;
        }

        if (current?.Matches(password) is true)
        {
            return;
        }

        try
        {
            var credential = AdminCredential.Create(password);
            settings.Admin.From(credential);
            adminAccess.Restore(credential);
            log("пароль настроек пришёл из Spark");
        }
        catch (AdminAccessException error)
        {
            log($"пароль настроек из Spark не применён: {error.Message}");
        }
    }
}
