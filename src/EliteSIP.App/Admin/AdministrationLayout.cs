using System.Collections.ObjectModel;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Panel;

namespace EliteSIP.App.Admin;

public enum AdminIncomingPage { Queues, Answer, Guard }
public enum AdminSystemPage { Data, Access, Updates, Reset }

/// <summary>Черновик профиля. Открытый пароль живёт только в защищённом окне управления.</summary>
public sealed class AdminAccountDraft : Observable
{
    private string _username = string.Empty;
    private string _displayName = string.Empty;
    private string _password = string.Empty;
    public Guid ProfileId { get; init; } = Guid.NewGuid();
    public bool IsActive { get; init; }
    public bool IsNew { get; init; }
    public string Domain { get; init; } = string.Empty;
    public WorkplaceSite Site { get; init; }
    internal string? OriginalProtectedPassword { get; init; }
    internal string OriginalPassword { get; init; } = string.Empty;
    public string Username { get => _username; set => Set(ref _username, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
}

public sealed partial class AdministrationViewModel
{
    private AdminIncomingPage _incomingPage;
    private AdminSystemPage _systemPage;
    private bool _watchesAccountSelection;
    public ObservableCollection<AdminAccountDraft> Accounts { get; } = [];
    public AdminIncomingPage IncomingPage { get => _incomingPage; set { Set(ref _incomingPage, value); NotifyRedesign(); } }
    public AdminSystemPage SystemPage { get => _systemPage; set { Set(ref _systemPage, value); NotifyRedesign(); } }
    public string SectionTitle => Sections.Single(item => item.Kind == Section).Title;
    public bool ShowsSystem => Section is AdminSectionKind.History;
    public bool ShowsIncomingAnswer => ShowsIncoming && IncomingPage is AdminIncomingPage.Answer;
    public bool ShowsIncomingGuard => ShowsIncoming && IncomingPage is AdminIncomingPage.Guard;
    public bool ShowsReset => ShowsSystem && SystemPage is AdminSystemPage.Reset;
    public bool CanSave => IsDirty && !HasAccountError;
    public bool HasSupport => Support is not null;
    public bool ManagedSaveWarning => _settings.Panel.Mode is PanelMode.Managed && _settings.Panel.HasChannelKey;
    public string MobilePreview => Guard.MasksMobileNumbers ? "+7 ••• •••-••-67" : "+7 900 123-45-67";
    public string IncomingPreviewTitle => Queues.FirstOrDefault()?.Title is { Length: > 0 } title ? title : Strings.Get("AdminSectionIncoming");
    public string AutoAnswerPreview => Strings.Get(Guard.AutoAnswer switch
    {
        AutoAnswerModes.Always => "AdminAnswerAlwaysShort",
        AutoAnswerModes.Header => "AdminAnswerHeaderShort",
        AutoAnswerModes.List => "AdminAnswerListShort",
        _ => "AdminAnswerOperatorShort",
    });
    public bool ShowsDigitExample => Guard.AutoAnswerOff && Guard.IsEnabled && Guard.AsksForDigit;
    public bool ShowsAnswerExample => Guard.AutoAnswerOff && !ShowsDigitExample;
    public bool ShowsAutomaticExample => !Guard.AutoAnswerOff;
    public string AccessStatus => Strings.Get(IsProtected ? "AdminPasswordPresentShort" : "AdminPasswordMissingShort");
    public IReadOnlyList<string> PreviewDigits => Enumerable.Range(1, Guard.TargetCount)
        .Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    public bool HasAccountError => AccountError.Length > 0;
    public string AccountError
    {
        get
        {
            if (Accounts.FirstOrDefault(account => account.IsActive) is { } active && active.ProfileId != _settings.Account.ProfileId)
            {
                return Strings.Get("AdminProfileChanged");
            }
            if (Accounts.Any(account => !account.IsActive && string.IsNullOrWhiteSpace(account.Username)))
            {
                return Strings.Get("AdminProfileNumberRequired");
            }
            var numbers = Accounts.Where(account => !account.IsActive).Select(account => account.Username.Trim())
                .Append(Username.Trim()).Where(number => number.Length > 0).ToArray();
            return numbers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != numbers.Length
                ? Strings.Get("AdminProfileDuplicate") : string.Empty;
        }
    }

    internal void WatchAccountSelection(bool watch)
    {
        _watchesAccountSelection = watch;
        _settings.Account.PropertyChanged -= OnAccountSelectionChanged;
        if (watch) { _settings.Account.PropertyChanged += OnAccountSelectionChanged; }
    }

    private void OnAccountSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
    {
        if (change.PropertyName != nameof(AccountSettings.ProfileId)) { return; }
        if (IsDirty) { NotifyRedesign(); }
        else
        {
            // ProfileSwitch.Load writes ID before number and credentials.
            // Reload after that synchronous transaction has finished.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                if (_watchesAccountSelection && !IsDirty) { Revert(); }
            });
        }
    }

    private AdminSectionKind NormalizeSection(AdminSectionKind section)
    {
        switch (section)
        {
            case AdminSectionKind.Pbx: return AdminSectionKind.Account;
            case AdminSectionKind.Queues: IncomingPage = AdminIncomingPage.Queues; return AdminSectionKind.Incoming;
            case AdminSectionKind.Access: SystemPage = AdminSystemPage.Access; return AdminSectionKind.History;
            case AdminSectionKind.Diagnostics: SystemPage = AdminSystemPage.Updates; return AdminSectionKind.History;
            case AdminSectionKind.Maintenance: SystemPage = AdminSystemPage.Data; return AdminSectionKind.History;
            default: return section;
        }
    }

    private void NotifyRedesign()
    {
        foreach (var property in new[] { nameof(SectionTitle), nameof(ShowsSystem), nameof(ShowsQueues),
            nameof(ShowsHistory), nameof(ShowsAccess), nameof(ShowsDiagnostics), nameof(ShowsMaintenance),
            nameof(ShowsIncomingAnswer), nameof(ShowsIncomingGuard), nameof(ShowsReset), nameof(AccountError),
            nameof(HasAccountError), nameof(CanSave), nameof(MobilePreview), nameof(IncomingPreviewTitle), nameof(AutoAnswerPreview),
            nameof(ShowsDigitExample), nameof(ShowsAnswerExample), nameof(ShowsAutomaticExample), nameof(PreviewDigits), nameof(AccessStatus) }) { NotifyChanged(property); }
    }

    public void AddAccount()
    {
        WatchAccount(new AdminAccountDraft { IsNew = true, Domain = _settings.Account.Domain, Site = _settings.Account.Site });
        MarkDirty();
    }

    public void RemoveAccount(AdminAccountDraft account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.IsActive && Accounts.Remove(account)) { MarkDirty(); }
    }

    private void WatchAccount(AdminAccountDraft account)
    {
        account.PropertyChanged += (_, _) =>
        {
            if (account.IsActive)
            {
                Username = account.Username;
                DisplayName = account.DisplayName;
                SipPassword = account.Password;
            }
            MarkDirty();
        };
        Accounts.Add(account);
    }

    private void LoadAccounts()
    {
        Accounts.Clear();
        WatchAccount(new AdminAccountDraft { ProfileId = _settings.Account.ProfileId, IsActive = true,
            Username = Username, DisplayName = DisplayName, Password = SipPassword,
            Domain = _settings.Account.Domain, Site = _settings.Account.Site });
        foreach (var profile in _settings.Profiles)
        {
            var password = profile.Password() ?? string.Empty;
            WatchAccount(new AdminAccountDraft { ProfileId = profile.ProfileId, Username = profile.Username,
                DisplayName = profile.DisplayName, Password = password, Domain = profile.Domain, Site = profile.Site,
                OriginalPassword = password, OriginalProtectedPassword = profile.ProtectedPassword });
        }
        NotifyRedesign();
    }

    private void SaveAccounts()
    {
        _settings.Profiles.Clear();
        foreach (var draft in Accounts.Where(account => !account.IsActive))
        {
            var profile = new ProfileSetting { ProfileId = draft.ProfileId, Username = draft.Username.Trim(),
                DisplayName = draft.DisplayName, Site = draft.Site,
                Domain = draft.IsNew ? draft.Site is WorkplaceSite.Office ? OfficeAddress : RemoteAddress : draft.Domain,
                ProtectedPassword = draft.OriginalProtectedPassword };
            if (draft.IsNew || draft.Password != draft.OriginalPassword) { profile.SetPassword(draft.Password); }
            _settings.Profiles.Add(profile);
        }
    }
}
