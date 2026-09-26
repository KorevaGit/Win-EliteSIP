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

        // Несколько номеров — у macOS это профили с переключением. У Windows
        // профиль один, и раскладывать дополнительные номера пока некуда:
        // берётся основной (он же первый в списке и он же в number), а
        // остальные называются в журнале, чтобы «где мой второй номер» было
        // видно по нему.
        if (config.Lines.Count > 1)
        {
            log($"конфигурация: номеров {config.Lines.Count}, на этой машине работает только основной "
                + $"{config.Number}; остальные ({string.Join(", ", config.Lines.Skip(1).Select(line => line.Number))}) "
                + "ждут поддержки профилей");
        }

        if (config.Number.Length > 0 && config.Number != settings.Account.Username)
        {
            // «Сменил» — только если номер уже был: первая настройка после
            // привязки не смена.
            if (settings.Account.Username.Length > 0)
            {
                newNumber = config.Number;
            }

            settings.Account.Username = config.Number;
            registrationChanged = true;
        }

        if (config.SipPassword.Length > 0 && config.SipPassword != settings.Credentials.Password())
        {
            settings.Credentials.SetPassword(config.SipPassword);
            registrationChanged = true;
        }

        // Пустое имя не затирает прежнее: Spark мог его просто не знать.
        if (config.Employee.Length > 0)
        {
            settings.Account.DisplayName = config.Employee;
        }

        // Площадка — до выбора адреса АТС: адрес берётся из пары адресов
        // предустановки по ней. Неизвестное значение площадку не трогает.
        WorkplaceSite? site = config.WorkFormat switch
        {
            "office" => WorkplaceSite.Office,
            "remote" => WorkplaceSite.Remote,
            _ => null,
        };

        if (site is { } wanted && wanted != settings.Account.Site)
        {
            settings.Account.Site = wanted;
            registrationChanged = true;
        }

        var address = settings.Account.Site is WorkplaceSite.Remote
            ? settings.Pbx.RemoteAddress
            : settings.Pbx.OfficeAddress;

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
        settings.Panel.LastContactAt = DateTimeOffset.UtcNow;

        if (newNumber is not null)
        {
            settings.Panel.LastNumberNotice = newNumber;
        }

        return new ConfigChange(registrationChanged, newNumber, presetChanged);
    }

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
