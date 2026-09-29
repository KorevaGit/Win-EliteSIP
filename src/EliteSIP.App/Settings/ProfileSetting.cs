using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;

namespace EliteSIP.App.Settings;

/// <summary>
/// Неактивный профиль: номер со своим паролем, подписью и площадкой.
/// </summary>
///
/// <remarks>
/// Пароль — под DPAPI, как и у активного (<see cref="SipCredentials"/>).
/// Идентификатор держит историю звонков: у дополнительного номера из Spark он
/// выводится из идентификатора номера и не меняется при правках.
/// </remarks>
public sealed class ProfileSetting : Observable
{
    private Guid _profileId = Guid.NewGuid();
    private string _username = string.Empty;
    private string _displayName = string.Empty;
    private string _domain = string.Empty;
    private WorkplaceSite _site = WorkplaceSite.Office;
    private string? _protectedPassword;

    public Guid ProfileId
    {
        get => _profileId;
        set => Set(ref _profileId, value);
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string Domain
    {
        get => _domain;
        set => Set(ref _domain, value);
    }

    public WorkplaceSite Site
    {
        get => _site;
        set => Set(ref _site, value);
    }

    public string? ProtectedPassword
    {
        get => _protectedPassword;
        set => Set(ref _protectedPassword, value);
    }

    public void SetPassword(string password)
        => ProtectedPassword = string.IsNullOrEmpty(password) ? null : ProtectedSecret.Protect(password);

    public string? Password()
        => _protectedPassword is null ? null : ProtectedSecret.Unprotect(_protectedPassword);
}

/// <summary>Переключение профилей: активный в <c>Account</c>, прочие в списке.</summary>
public static class ProfileSwitch
{
    /// <summary>
    /// Делает профиль активным: прежний уходит в список, выбранный — в
    /// <c>Account</c> и <c>Credentials</c>.
    /// </summary>
    ///
    /// <returns><c>false</c>, если такого профиля нет или он уже активен.</returns>
    public static bool SwitchProfile(this AppSettings settings, Guid profileId)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Account.ProfileId == profileId)
        {
            return false;
        }

        var target = settings.Profiles.FirstOrDefault(profile => profile.ProfileId == profileId);
        if (target is null)
        {
            return false;
        }

        var current = Snapshot(settings);
        settings.Profiles.Remove(target);
        settings.Profiles.Add(current);

        Load(settings, target);
        return true;
    }

    /// <summary>Активный профиль списком — для меню и раскладки номеров.</summary>
    public static ProfileSetting Snapshot(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ProfileSetting
        {
            ProfileId = settings.Account.ProfileId,
            Username = settings.Account.Username,
            DisplayName = settings.Account.DisplayName,
            Domain = settings.Account.Domain,
            Site = settings.Account.Site,
            ProtectedPassword = settings.Credentials.ProtectedPassword,
        };
    }

    /// <summary>Кладёт профиль в активные разделы.</summary>
    public static void Load(AppSettings settings, ProfileSetting profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);

        settings.Account.ProfileId = profile.ProfileId;
        settings.Account.Username = profile.Username;
        settings.Account.DisplayName = profile.DisplayName;
        settings.Account.Site = profile.Site;
        settings.Account.Domain = profile.Domain;
        settings.Credentials.ProtectedPassword = profile.ProtectedPassword;
    }
}
