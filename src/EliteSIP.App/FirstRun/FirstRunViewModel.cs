using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using AppearanceMode = EliteSIP.App.Theme.Appearance;

namespace EliteSIP.App.FirstRun;

/// <summary>Экран мастера. Порядок объявления — порядок показа.</summary>
///
/// <remarks>
/// Язык спрашивается первым, потому что всё после него читается на выбранном.
/// Оформление — последним содержательным экраном: это единственное, что можно
/// поменять когда угодно, и ставить его раньше учётки значило бы спросить про
/// цвет прежде, чем про телефон.
/// </remarks>
public enum FirstRunStep
{
    Welcome,
    User,
    Appearance,
    Finale,
}

/// <summary>
/// Черновик мастера первоначальной настройки.
/// </summary>
///
/// <remarks>
/// <b>Черновик, а не правка на месте</b> — то же устройство, что у
/// «Управления», и по той же причине: у мастера есть «Назад», и шаг назад с уже
/// применённым снимком означал бы откат записанного. На диск до самого конца не
/// уходит ничего.
///
/// <b>Ветка ключа активации сюда не вошла.</b> В оригинале это основной путь:
/// номер, пароль SIP и административный пароль приезжают одним пакетом, а
/// сотрудник вводит только ключ. Пакет открывает <c>PanelLink</c>, который
/// переносится этапом W10, — и до него ключ вводить некуда. Пока остаётся вторая
/// ветка оригинала, ручная: она заводит машину целиком и ничего от панели не
/// ждёт.
/// </remarks>
public sealed class FirstRunViewModel : Observable
{
    private readonly AppSettings _settings;
    private readonly AdminAccessState _access;

    private FirstRunStep _step = FirstRunStep.Welcome;
    private LanguageSetting _language;
    private AppearanceMode _theme;
    private string _username = string.Empty;
    private string _displayName = string.Empty;
    private string _sipPassword = string.Empty;
    private string _officeAddress = string.Empty;
    private string _adminPassword = string.Empty;
    private string _repeatedAdminPassword = string.Empty;

    public FirstRunViewModel(AppSettings settings, AdminAccessState access)
    {
        _settings = settings;
        _access = access;
        _language = settings.Appearance.Language;
        _theme = settings.Appearance.Theme;
    }

    public FirstRunStep Step
    {
        get => _step;
        private set
        {
            Set(ref _step, value);
            foreach (var name in new[]
            {
                nameof(ShowsWelcome), nameof(ShowsUser), nameof(ShowsAppearance), nameof(ShowsFinale),
                nameof(CanGoBack), nameof(CanGoForward), nameof(IsLastStep),
            })
            {
                NotifyChanged(name);
            }
        }
    }

    public bool ShowsWelcome => _step is FirstRunStep.Welcome;

    public bool ShowsUser => _step is FirstRunStep.User;

    public bool ShowsAppearance => _step is FirstRunStep.Appearance;

    public bool ShowsFinale => _step is FirstRunStep.Finale;

    public bool IsLastStep => _step is FirstRunStep.Finale;

    public bool CanGoBack => _step is not FirstRunStep.Welcome;

    /// <summary>Можно ли шагнуть дальше.</summary>
    ///
    /// <remarks>
    /// Дальше не пускает только незаполненная учётка: цвет и язык испортить
    /// нельзя, а машина без добавочного и пароля — это машина, которая не
    /// зазвонит, и узнавать об этом на четвёртом экране поздно.
    /// </remarks>
    public bool CanGoForward => _step is not FirstRunStep.User || IsUserComplete;

    public bool IsUserComplete
        => _username.Trim().Length > 0
            && _sipPassword.Length > 0
            && _officeAddress.Trim().Length > 0
            && (_adminPassword.Length == 0 || _adminPassword == _repeatedAdminPassword);

    public bool AdminPasswordsDiffer
        => _repeatedAdminPassword.Length > 0 && _adminPassword != _repeatedAdminPassword;

    public LanguageSetting Language
    {
        get => _language;
        set => Set(ref _language, value);
    }

    public AppearanceMode Theme
    {
        get => _theme;
        set => Set(ref _theme, value);
    }

    public string Username
    {
        get => _username;
        set
        {
            Set(ref _username, value);
            NotifyUser();
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string SipPassword
    {
        get => _sipPassword;
        set
        {
            Set(ref _sipPassword, value);
            NotifyUser();
        }
    }

    /// <summary>Адрес АТС из офиса. Второй адрес заводит администратор позже.</summary>
    ///
    /// <remarks>
    /// Мастер спрашивает один адрес, а не пару: человек, заводящий машину,
    /// знает тот, из которого он сейчас работает. Вторую площадку добавляют в
    /// «Управлении» — тогда, когда она появляется.
    /// </remarks>
    public string OfficeAddress
    {
        get => _officeAddress;
        set
        {
            Set(ref _officeAddress, value);
            NotifyUser();
        }
    }

    /// <summary>Административный пароль. Пустой — машина остаётся открытой.</summary>
    ///
    /// <remarks>
    /// Не обязателен, и это решение: незащищённая машина — законное состояние
    /// (пароль снимают руками), а мастер, который не пускает дальше без
    /// пароля, заставляет придумать его на месте — то есть придумать плохой.
    /// </remarks>
    public string AdminPassword
    {
        get => _adminPassword;
        set
        {
            Set(ref _adminPassword, value);
            NotifyUser();
        }
    }

    public string RepeatedAdminPassword
    {
        get => _repeatedAdminPassword;
        set
        {
            Set(ref _repeatedAdminPassword, value);
            NotifyUser();
        }
    }

    /// <summary>Меняется ли язык — от этого зависит, нужен ли перезапуск.</summary>
    public bool LanguageChanges => _language != _settings.Appearance.Language;

    public void Forward()
    {
        if (!CanGoForward || _step is FirstRunStep.Finale)
        {
            return;
        }

        Step = _step + 1;
    }

    public void Back()
    {
        if (CanGoBack)
        {
            Step = _step - 1;
        }
    }

    /// <summary>Применяет черновик одним махом и помечает машину настроенной.</summary>
    ///
    /// <remarks>
    /// Порядок здесь важен так же, как в «Управлении»: пароли уходят последними,
    /// потому что они превращают машину в защищённую, и делать это раньше, чем
    /// записана сама учётка, значит запереть полупустое рабочее место.
    /// </remarks>
    public void Complete()
    {
        _settings.Account.Username = _username.Trim();
        _settings.Account.DisplayName = _displayName.Trim();
        _settings.Account.Site = WorkplaceSite.Office;
        _settings.Pbx.OfficeAddress = _officeAddress.Trim();
        _settings.Account.Domain = _officeAddress.Trim();

        _settings.Appearance.Theme = _theme;
        _settings.Appearance.Language = _language;

        _settings.Credentials.SetPassword(_sipPassword);

        if (_adminPassword.Length > 0)
        {
            _access.SetPassword(_adminPassword);
            _settings.Admin.From(_access.Credential);
        }

        // Пометка «настроено» — последней: до неё запись могла оборваться, и
        // машина с половиной настроек обязана встретить мастер снова, а не
        // панель без телефона.
        _settings.Setup.IsCompleted = true;
    }

    private void NotifyUser()
    {
        NotifyChanged(nameof(IsUserComplete));
        NotifyChanged(nameof(CanGoForward));
        NotifyChanged(nameof(AdminPasswordsDiffer));
    }
}
