using System.Net.Http;
using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;
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

    /// <summary>
    /// Ключ активации: основной путь, которым заводят рабочее место.
    /// </summary>
    ///
    /// <remarks>
    /// Сразу за приветствием, потому что удачный ключ отменяет следующий экран
    /// целиком: номер, пароль SIP и адрес АТС приезжают пакетом, и спрашивать их
    /// после этого не о чем.
    /// </remarks>
    Key,

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
/// <b>Путей два, и это оригинальные два.</b> Основной — ключ активации: номер,
/// пароль SIP и настройки конторы приезжают одним пакетом, а сотрудник вводит
/// двенадцать знаков. Запасной — ручной: он заводит машину целиком и ничего от
/// панели не ждёт. Ручным пользуются там, где канала нет вовсе, — и он же
/// остаётся единственным, пока в приложение не положили заводскую настройку.
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

    private string _key = string.Empty;
    private bool _isCheckingKey;
    private string? _keyFailure;
    private ActivationPackage? _package;
    private MachineAccess? _machineAccess;

    public FirstRunViewModel(AppSettings settings, AdminAccessState access)
    {
        _settings = settings;
        _access = access;
        _language = settings.Appearance.Language;
        _theme = settings.Appearance.Theme;

        // Черновик прошлого захода: ключ уже сгорел, а мастер закрыли. Поднять
        // его — единственный способ не просить новый ключ у того, кто ничего не
        // сделал не так.
        if (ActivationDraftStore.Load() is { } draft)
        {
            _key = draft.Key;
            _package = draft.Package;
            _machineAccess = draft.Access;
        }
    }

    public FirstRunStep Step
    {
        get => _step;
        private set
        {
            Set(ref _step, value);
            foreach (var name in new[]
            {
                nameof(ShowsWelcome), nameof(ShowsKey), nameof(ShowsUser), nameof(ShowsAppearance),
                nameof(ShowsFinale),
                nameof(CanGoBack), nameof(CanGoForward), nameof(IsLastStep),
            })
            {
                NotifyChanged(name);
            }
        }
    }

    public bool ShowsWelcome => _step is FirstRunStep.Welcome;

    public bool ShowsKey => _step is FirstRunStep.Key;

    // MARK: - Ключ активации

    /// <summary>Двенадцать знаков, которые сотруднику продиктовали.</summary>
    ///
    /// <remarks>
    /// Разбор терпимый — им занимается <c>ActivationKey</c>: ключ вставляют из
    /// мессенджера вместе с пробелами и переносами. Здесь поле хранится как
    /// набрано, чтобы человек видел то, что ввёл.
    /// </remarks>
    public string Key
    {
        get => _key;
        set
        {
            Set(ref _key, value);

            // Прошлый отказ снимается на первом же нажатии клавиши: надпись
            // «ключ не подошёл» под полем, которое уже правят, относится к
            // прошлому и мешает читать настоящее.
            KeyFailure = null;
            NotifyChanged(nameof(CanCheckKey));
        }
    }

    /// <summary>Идёт заход на канал.</summary>
    public bool IsCheckingKey
    {
        get => _isCheckingKey;
        private set
        {
            Set(ref _isCheckingKey, value);
            NotifyChanged(nameof(CanCheckKey));
            NotifyChanged(nameof(CanGoForward));
        }
    }

    /// <summary>Чем кончился прошлый заход. <c>null</c> — не кончился ничем.</summary>
    public string? KeyFailure
    {
        get => _keyFailure;
        private set
        {
            Set(ref _keyFailure, value);
            NotifyChanged(nameof(HasKeyFailure));
        }
    }

    public bool HasKeyFailure => _keyFailure is not null;

    /// <summary>Ключ открыл пакет — машина заведена панелью.</summary>
    public bool IsActivated => _package is not null;

    /// <summary>Кому выписан пакет: показывается человеку вместо «готово».</summary>
    public string ActivatedEmployee => _package?.Employee ?? string.Empty;

    public string ActivatedNumber => _package?.Number ?? string.Empty;

    public string ActivatedPreset => _package?.Preset.Name ?? string.Empty;

    /// <summary>
    /// Есть ли куда ходить за пакетом.
    /// </summary>
    ///
    /// <remarks>
    /// Без заводской настройки экран ключа не показывается вовсе: поле, которое
    /// на любой ключ отвечает «не удалось связаться», хуже отсутствующего.
    /// Машина тогда заводится вручную — так же, как заводилась до этого этапа.
    /// </remarks>
    public static bool HasChannel => PanelLine.Provisioning.Current?.Updates is not null;

    public bool CanCheckKey => !_isCheckingKey && _key.Trim().Length > 0;

    /// <summary>
    /// Проверяет ключ и забирает всё, что к нему полагается.
    /// </summary>
    ///
    /// <remarks>
    /// Двумя заходами, а не одним: пакет открывается ключом, а административный
    /// пароль приезжает отдельным подписанным объектом — у техподдержки своя
    /// предустановка со своим паролем, и в общий файл он не едет.
    ///
    /// <b>Второй заход не может отменить первый.</b> Ключ сгорел на первом, и
    /// отказ за паролем означает машину без пароля, а не машину без настроек:
    /// пароль приедет следующим тактом линии. Поэтому его отказ только пишется в
    /// черновик пустотой и наружу не выходит.
    ///
    /// Черновик сохраняется сразу за успехом — до того, как человек нажмёт
    /// «Далее»: ровно между этими двумя мгновениями и терялись ключи.
    /// </remarks>
    public async Task CheckKeyAsync()
    {
        if (!CanCheckKey)
        {
            return;
        }

        IsCheckingKey = true;
        KeyFailure = null;

        try
        {
            var key = ActivationKey.Parse(_key);
            var package = await ActivationService.FetchAsync(key).ConfigureAwait(true);

            MachineAccess? access = null;
            try
            {
                access = await MachineService
                    .FetchAccessAsync(package.InstallationID, package.ChannelKey)
                    .ConfigureAwait(true);
            }
            catch (Exception error) when (error is PanelLinkException or HttpRequestException
                                              or TaskCanceledException)
            {
                // Молча: см. выше. Пароль приедет тактом линии.
            }

            _package = package;
            _machineAccess = access;

            ActivationDraftStore.Save(_key, package, access);

            NotifyActivated();
        }
        catch (PanelLinkException error)
        {
            KeyFailure = error.Message;
        }
        catch (ActivationChannelException error)
        {
            KeyFailure = error.Message;
        }
        finally
        {
            IsCheckingKey = false;
        }
    }

    /// <summary>Человек выбрал завести машину руками.</summary>
    ///
    /// <remarks>
    /// Не «пропустить»: пропущенный шаг возвращаются доделать, а этот выбор —
    /// вторая дорога целиком, и назад с неё ведёт кнопка «Назад», а не смысл.
    /// </remarks>
    public void UseManualSetup() => Step = FirstRunStep.User;

    private void NotifyActivated()
    {
        foreach (var name in new[]
        {
            nameof(IsActivated), nameof(ActivatedEmployee), nameof(ActivatedNumber),
            nameof(ActivatedPreset), nameof(CanGoForward),
        })
        {
            NotifyChanged(name);
        }
    }

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
    public bool CanGoForward => _step switch
    {
        // С экрана ключа дальше пускает только открытый пакет. Не потому, что
        // ручной путь хуже, а потому, что уходят с него не «Далее», а «Настроить
        // вручную»: две кнопки, ведущие в разные места, не должны выглядеть
        // одной.
        FirstRunStep.Key => IsActivated && !IsCheckingKey,
        FirstRunStep.User => IsUserComplete,
        _ => true,
    };

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

        Step = _step switch
        {
            // Удачный ключ отменяет экран учётки целиком: номер, пароль и адрес
            // приехали пакетом, и показать их можно разве что для любования.
            FirstRunStep.Key => FirstRunStep.Appearance,

            // Канала нет — экран ключа не показывается вовсе.
            FirstRunStep.Welcome when !HasChannel => FirstRunStep.User,

            _ => _step + 1,
        };
    }

    public void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        Step = _step switch
        {
            FirstRunStep.Appearance when IsActivated => FirstRunStep.Key,
            FirstRunStep.User when !HasChannel => FirstRunStep.Welcome,

            // С экрана учётки назад — на ключ: человек мог уйти сюда
            // «настроить вручную» и передумать.
            FirstRunStep.User => FirstRunStep.Key,

            _ => _step - 1,
        };
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
        _settings.Appearance.Theme = _theme;
        _settings.Appearance.Language = _language;

        if (_package is { } package)
        {
            CompleteByActivation(package);
            return;
        }

        _settings.Account.Username = _username.Trim();
        _settings.Account.DisplayName = _displayName.Trim();
        _settings.Account.Site = WorkplaceSite.Office;
        _settings.Pbx.OfficeAddress = _officeAddress.Trim();
        _settings.Account.Domain = _officeAddress.Trim();

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

    /// <summary>
    /// Заводит машину пакетом активации.
    /// </summary>
    ///
    /// <remarks>
    /// Порядок тот же, что у ручного пути, и по той же причине: сперва учётка,
    /// потом настройки конторы, пароли последними. Между ними одно добавление —
    /// память о панели: без неё машина позвонит, но следующей ревизии не
    /// получит, потому что искать себя в файле ей будет нечем.
    ///
    /// Адрес АТС берётся из управляемых полей — их накладывает та же дорога, что
    /// и файл предустановок, — а домен учётки подтягивается к офисному адресу:
    /// пара адресов это настройка машины, а регистрируется учётка по своему
    /// домену, и одно из другого само не следует. На свежей машине без этой
    /// строки номер есть, пароль есть, а регистрироваться некуда.
    ///
    /// Черновик стирается в самом конце — тогда, когда терять уже нечего.
    /// </remarks>
    private void CompleteByActivation(ActivationPackage package)
    {
        _settings.Account.Username = package.Number;
        _settings.Account.DisplayName = package.Employee;
        _settings.Account.Site = WorkplaceSite.Office;
        _settings.Credentials.SetPassword(package.SipPassword);

        _settings.Panel.InstallationID = package.InstallationID;
        _settings.Panel.SetChannelKey(package.ChannelKey);
        _settings.Panel.PresetID = package.Preset.ID;
        _settings.Panel.PresetName = package.Preset.Name;
        _settings.Panel.Mode = PanelMode.Managed;

        _settings.Apply(ManagedFields.Parse(package.Preset.Settings));

        // Ревизия ставится после наложения: до него она означала бы «применено»
        // там, где ещё ничего не применялось.
        _settings.Panel.AppliedRevision = package.Preset.Revision;
        _settings.Panel.AppliedAt = DateTimeOffset.UtcNow;

        if (_settings.Pbx.OfficeAddress.Length > 0)
        {
            _settings.Account.Domain = _settings.Pbx.OfficeAddress;
        }

        // Административный пароль — из помашинного объекта, а не из пакета: в
        // пакете его нет вовсе. Не приехал — машина останется с прежним, и
        // пароль догонит её первым же тактом линии.
        if (_machineAccess is { AdminPassword.Length: > 0 } access)
        {
            var credential = AdminCredential.Create(access.AdminPassword);
            _settings.Admin.From(credential);
            _access.Restore(credential);
        }

        _settings.Setup.IsCompleted = true;

        ActivationDraftStore.Clear();
    }

    private void NotifyUser()
    {
        NotifyChanged(nameof(IsUserComplete));
        NotifyChanged(nameof(CanGoForward));
        NotifyChanged(nameof(AdminPasswordsDiffer));
    }
}
