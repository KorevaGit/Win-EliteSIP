using System.Collections.ObjectModel;
using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Admin;

/// <summary>Разделы «Управления».</summary>
///
/// <remarks>
/// Девять. Девятый — «Входящие», политика защиты от автокликеров, — приехал с
/// W9 вместе с окном входящего вызова: заводить раздел настроек раньше того,
/// чем он управляет, значило бы показать выключатели, за которыми ничего нет.
/// </remarks>
public enum AdminSectionKind
{
    Account,
    Pbx,
    Macros,
    Queues,
    History,
    Access,
    Diagnostics,
    Maintenance,

    /// <summary>Защита приёма вызова: девятый раздел, приехал с W9.</summary>
    Incoming,

    /// <summary>
    /// Связь с панелью: десятый раздел, приехал с W10.
    ///
    /// Единственный, где кнопки действуют сразу, а не по «Сохранить». Так и
    /// должно быть: «Проверить настройки сейчас» и перепрошивка — это не правки
    /// машины, а обращения к каналу, и придерживать их черновиком не за чем.
    /// </summary>
    Support,
}

/// <summary>Пункт бокового списка. <paramref name="Group"/> — заголовок над ним.</summary>
///
/// <remarks>
/// Группы отвечают на «про что этот раздел»: без них пункты читаются одним
/// списком, в котором «Очереди» стоят рядом с «Историей» без всякой причины.
/// У первой группы заголовка нет намеренно — в системных списках первая пачка
/// тоже идёт без подписи.
/// </remarks>
public sealed record AdminSectionItem(AdminSectionKind Kind, string Title, string Glyph, string? Group);

/// <summary>Состояние окна «Управление».</summary>
///
/// <remarks>
/// <b>Правки придержаны черновиком.</b> Это отличие от менеджерских настроек, и
/// оно по существу: менеджер меняет громкость себе, а администратор — рабочее
/// место целиком, и половина применённой правки означает машину, которая уже
/// не та и ещё не эта. Поэтому здесь есть «Сохранить» и «Отменить», а точка у
/// пункта списка показывает, где лежит несохранённое.
/// </remarks>
public sealed class AdministrationViewModel : Observable
{
    private readonly AppSettings _settings;
    private readonly AdminAccessState _access;

    private AdminSectionKind _section = AdminSectionKind.Macros;
    private int _macroColumns;
    private int _macroHeight;
    private bool _macroHeightIsManual;
    private bool _historyIsEnabled;
    private int _historyAgeInDays;
    private string _username = string.Empty;
    private string _displayName = string.Empty;
    private string _sipPassword = string.Empty;
    private string _officeAddress = string.Empty;
    private string _remoteAddress = string.Empty;
    private int _port;
    private SipTransport _transport;
    private int _registrationExpiry;
    private string _transferCode = string.Empty;
    private string _conferenceCode = string.Empty;
    private string _pinnedFingerprint = string.Empty;
    private double _knockSpacing = 1;
    private double _knockRepeat = 600;
    private bool _acceptsAnyCertificate;
    private bool _logToFile;
    private bool _logsSipTrace;
    private string _newPassword = string.Empty;
    private string _repeatedPassword = string.Empty;
    private bool _isDirty;
    private string? _lastGuardReport;

    /// <param name="preview">
    /// Показ окна входящего для проверки: <c>true</c> — вызов по сделке,
    /// <c>false</c> — раздача. Приходит снаружи, потому что окном владеет
    /// приложение, а не «Управление»: проверка обязана показывать ровно то, что
    /// оператор увидит на живом вызове, — ради этого её и открывают.
    /// </param>
    public AdministrationViewModel(
        AppSettings settings,
        AdminAccessState access,
        Action<bool>? preview = null)
    {
        _settings = settings;
        _access = access;

        Guard.PropertyChanged += (_, _) => MarkDirty();
        PreviewDistribution = new RelayCommand(_ => preview?.Invoke(false));
        PreviewDeal = new RelayCommand(_ => preview?.Invoke(true), _ => Username.Length > 0);

        // Порядок и группы — из оригинала. Заголовок группы несёт та строка,
        // которая её открывает; у первой группы заголовка нет намеренно.
        Sections =
        [
            new(AdminSectionKind.Account, Strings.Get("AdminSectionAccount"), "person.crop.circle", null),
            new(AdminSectionKind.Pbx, Strings.Get("AdminSectionPbx"), "phone.arrow.right", null),
            new(AdminSectionKind.Macros, Strings.Get("AdminSectionMacros"), "square.grid.3x3", Strings.Get("AdminGroupCall")),
            new(AdminSectionKind.Queues, Strings.Get("AdminSectionQueues"), "person.3.fill", null),
            new(AdminSectionKind.Incoming, Strings.Get("AdminSectionIncoming"), "bell.badge", null),
            new(AdminSectionKind.History, Strings.Get("AdminSectionHistory"), "clock", Strings.Get("AdminGroupMachine")),
            new(AdminSectionKind.Access, Strings.Get("AdminSectionAccess"), "lock.shield.fill", null),
            new(AdminSectionKind.Diagnostics, Strings.Get("AdminSectionDiagnostics"), "stethoscope", null),
            new(AdminSectionKind.Maintenance, Strings.Get("AdminSectionMaintenance"), "hammer.fill", null),
            new(AdminSectionKind.Support, Strings.Get("AdminSectionSupport"), "crown.fill", null),
        ];

        Revert();
    }

    public IReadOnlyList<AdminSectionItem> Sections { get; }

    /// <summary>Черновик защиты приёма вызова.</summary>
    ///
    /// <remarks>
    /// Отдельный набор, а не сами настройки: правки администратора придержаны
    /// «Сохранить», а половина применённой политики защиты — это защита, о
    /// которой никто не знает, какая она.
    /// </remarks>
    public IncomingCallSettings Guard { get; } = new();

    /// <summary>Раздел «Поддержка». <c>null</c> — приложение его не завело.</summary>
    ///
    /// <remarks>
    /// Приходит снаружи, потому что линией панели владеет приложение, а не
    /// «Управление»: здесь только показ того, что она знает, и три кнопки к ней.
    /// </remarks>
    public SupportViewModel? Support { get; init; }

    /// <summary>Обновления в «Диагностике». <c>null</c> — линия не заведена.</summary>
    ///
    /// <remarks>
    /// Внутренний тип у открытого класса: линия обновлений — дело приложения, а
    /// не чужого кода, и открывать её наружу незачем. Разметка это не смущает —
    /// привязки идут по имени.
    /// </remarks>
    internal UpdatesViewModel? Updates { get; init; }

    /// <summary>Показать окно входящего для проверки: раздача.</summary>
    public RelayCommand PreviewDistribution { get; }

    /// <summary>То же для вызова по сделке. Отдельной кнопкой, а не переключателем.</summary>
    ///
    /// <remarks>
    /// У звонка по сделке другой заголовок и своя подсказка, и увидеть их иначе
    /// нельзя — случай приходит из CRM, а не из настроек. Номер подставляется
    /// свой: так проверка заодно показывает, что добавочный вообще опознаётся.
    /// </remarks>
    public RelayCommand PreviewDeal { get; }

    /// <summary>Отчёт защиты по последнему вызову. <c>null</c> — вызовов не было.</summary>
    public string? LastGuardReport
    {
        get => _lastGuardReport;
        set => Set(ref _lastGuardReport, value);
    }

    public AdminSectionKind Section
    {
        get => _section;
        set
        {
            Set(ref _section, value);
            foreach (var name in new[]
            {
                nameof(ShowsAccount), nameof(ShowsPbx), nameof(ShowsMacros), nameof(ShowsQueues),
                nameof(ShowsHistory), nameof(ShowsAccess), nameof(ShowsDiagnostics), nameof(ShowsMaintenance),
                nameof(ShowsIncoming), nameof(ShowsSupport),
            })
            {
                NotifyChanged(name);
            }
        }
    }

    public bool ShowsAccount => _section is AdminSectionKind.Account;

    public bool ShowsPbx => _section is AdminSectionKind.Pbx;

    public bool ShowsMacros => _section is AdminSectionKind.Macros;

    public bool ShowsQueues => _section is AdminSectionKind.Queues;

    public bool ShowsHistory => _section is AdminSectionKind.History;

    public bool ShowsAccess => _section is AdminSectionKind.Access;

    public bool ShowsDiagnostics => _section is AdminSectionKind.Diagnostics;

    public bool ShowsMaintenance => _section is AdminSectionKind.Maintenance;

    public bool ShowsIncoming => _section is AdminSectionKind.Incoming;

    public bool ShowsSupport => _section is AdminSectionKind.Support;

    // --- Аккаунт ---------------------------------------------------------

    public string Username
    {
        get => _username;
        set
        {
            Set(ref _username, value);
            MarkDirty();
            PreviewDeal.RaiseCanExecuteChanged();
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            Set(ref _displayName, value);
            MarkDirty();
        }
    }

    /// <summary>Новый пароль учётки. Пустой — оставить прежний.</summary>
    ///
    /// <remarks>
    /// Прежний сюда не подставляется, и это решение: показать пароль в поле —
    /// значит показать его всякому, кто заглянул через плечо администратора, а
    /// прочитать его отсюда всё равно нельзя (он уходит под DPAPI). Поэтому
    /// поле пустое, а рядом стоит строка «пароль задан».
    /// </remarks>
    public string SipPassword
    {
        get => _sipPassword;
        set
        {
            Set(ref _sipPassword, value);
            MarkDirty();
        }
    }

    public bool HasSipPassword => _settings.Credentials.HasPassword;

    // --- АТС -------------------------------------------------------------

    public string OfficeAddress
    {
        get => _officeAddress;
        set
        {
            Set(ref _officeAddress, value);
            MarkDirty();
        }
    }

    public string RemoteAddress
    {
        get => _remoteAddress;
        set
        {
            Set(ref _remoteAddress, value);
            MarkDirty();
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            Set(ref _port, value);
            MarkDirty();
        }
    }

    public SipTransport Transport
    {
        get => _transport;
        set
        {
            Set(ref _transport, value);
            NotifyChanged(nameof(IsTls));
            MarkDirty();
        }
    }

    // --- Стук по портам --------------------------------------------------

    /// <summary>Черновик последовательности стука. Настоящая правится по «Сохранить».</summary>
    public ObservableCollection<PortKnockStepSetting> KnockSteps { get; } = [];

    public double KnockSpacingSeconds
    {
        get => _knockSpacing;
        set
        {
            Set(ref _knockSpacing, value);
            MarkDirty();
            NotifyChanged(nameof(KnockSummary));
        }
    }

    public double KnockRepeatIntervalSeconds
    {
        get => _knockRepeat;
        set
        {
            Set(ref _knockRepeat, value);
            MarkDirty();
        }
    }

    /// <summary>
    /// Чем стук обойдётся при подключении.
    /// </summary>
    ///
    /// <remarks>
    /// Задержка перед первым REGISTER — то единственное, что оператор от стука
    /// замечает, и знать её администратор должен до того, как её заметят: восемь
    /// пакетов с паузой в секунду это семь секунд тишины на каждом запуске
    /// удалённой машины.
    /// </remarks>
    public string KnockSummary
    {
        get
        {
            var packets = KnockSteps.Sum(step => Math.Max(0, step.Count));
            var seconds = Math.Max(0, packets - 1) * _knockSpacing;

            return packets == 0
                ? Strings.Get("AdminKnockDisabled")
                : Strings.Format("AdminKnockSummary", packets, seconds);
        }
    }

    public void AddKnockStep()
    {
        PortKnockStepSetting step = new();
        step.PropertyChanged += (_, _) =>
        {
            MarkDirty();
            NotifyChanged(nameof(KnockSummary));
        };

        KnockSteps.Add(step);
        MarkDirty();
        NotifyChanged(nameof(KnockSummary));
    }

    public void RemoveKnockStep(PortKnockStepSetting step)
    {
        KnockSteps.Remove(step);
        MarkDirty();
        NotifyChanged(nameof(KnockSummary));
    }

    /// <summary>Показывать ли настройки проверки сертификата.</summary>
    ///
    /// <remarks>
    /// На UDP и TCP они не значат ничего, а показанные всегда — приглашают
    /// включить «принимать любой» там, где шифрования нет вовсе.
    /// </remarks>
    public bool IsTls => _transport is SipTransport.Tls;

    /// <summary>Отпечаток сертификата, которому доверяем вместо системной проверки.</summary>
    public string PinnedCertificateFingerprint
    {
        get => _pinnedFingerprint;
        set
        {
            Set(ref _pinnedFingerprint, value);
            MarkDirty();
        }
    }

    /// <summary>
    /// Принимать любой сертификат: отключение защиты от перехвата целиком.
    /// </summary>
    ///
    /// <remarks>
    /// Отпечаток при включённом признаке не читается и в окне гаснет: два
    /// способа доверия разом означали бы, что человек считает себя защищённым
    /// прописанным отпечатком, а на деле принимает что угодно.
    /// </remarks>
    public bool AcceptsAnyTlsCertificate
    {
        get => _acceptsAnyCertificate;
        set
        {
            Set(ref _acceptsAnyCertificate, value);
            MarkDirty();
        }
    }

    public int RegistrationExpirySeconds
    {
        get => _registrationExpiry;
        set
        {
            Set(ref _registrationExpiry, value);
            MarkDirty();
        }
    }

    public string TransferFeatureCode
    {
        get => _transferCode;
        set
        {
            Set(ref _transferCode, value);
            MarkDirty();
        }
    }

    public string ConferenceFeatureCode
    {
        get => _conferenceCode;
        set
        {
            Set(ref _conferenceCode, value);
            MarkDirty();
        }
    }

    // --- Очереди ---------------------------------------------------------

    public ObservableCollection<QueueSetting> Queues { get; } = [];

    public void AddQueue()
    {
        var queue = new QueueSetting();
        queue.PropertyChanged += (_, _) => MarkDirty();
        Queues.Add(queue);
        MarkDirty();
    }

    public void RemoveQueue(QueueSetting queue)
    {
        Queues.Remove(queue);
        MarkDirty();
    }

    // --- Обслуживание ----------------------------------------------------

    public bool LogToFile
    {
        get => _logToFile;
        set
        {
            Set(ref _logToFile, value);
            MarkDirty();
        }
    }

    public bool LogsSipTrace
    {
        get => _logsSipTrace;
        set
        {
            Set(ref _logsSipTrace, value);
            MarkDirty();
        }
    }

    /// <summary>Черновик списка клавиш. Настоящий список правится по «Сохранить».</summary>
    public ObservableCollection<MacroSetting> Macros { get; } = [];

    public int MacroColumns
    {
        get => _macroColumns;
        set
        {
            Set(ref _macroColumns, value);
            MarkDirty();
        }
    }

    public int MacroHeight
    {
        get => _macroHeight;
        set
        {
            Set(ref _macroHeight, value);
            MarkDirty();
        }
    }

    public bool MacroHeightIsManual
    {
        get => _macroHeightIsManual;
        set
        {
            Set(ref _macroHeightIsManual, value);
            MarkDirty();
        }
    }

    public bool HistoryIsEnabled
    {
        get => _historyIsEnabled;
        set
        {
            Set(ref _historyIsEnabled, value);
            MarkDirty();
        }
    }

    public int HistoryAgeInDays
    {
        get => _historyAgeInDays;
        set
        {
            Set(ref _historyAgeInDays, value);
            MarkDirty();
        }
    }

    /// <summary>Новый пароль администратора. В черновике живёт открытым.</summary>
    ///
    /// <remarks>
    /// Открытым — но только в памяти окна и только до сохранения: на диск
    /// уходит соль с хэшем, а не он сам.
    /// </remarks>
    public string NewPassword
    {
        get => _newPassword;
        set
        {
            Set(ref _newPassword, value);
            NotifyChanged(nameof(CanSetPassword));
            NotifyChanged(nameof(PasswordsDiffer));
        }
    }

    public string RepeatedPassword
    {
        get => _repeatedPassword;
        set
        {
            Set(ref _repeatedPassword, value);
            NotifyChanged(nameof(CanSetPassword));
            NotifyChanged(nameof(PasswordsDiffer));
        }
    }

    /// <summary>Повтор не совпал — и это говорится до нажатия, а не после.</summary>
    public bool PasswordsDiffer
        => _repeatedPassword.Length > 0 && _newPassword != _repeatedPassword;

    public bool CanSetPassword
        => _newPassword.Length > 0 && _newPassword == _repeatedPassword;

    public bool IsProtected => _settings.Admin.IsProtected;

    /// <summary>Есть ли несохранённое.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    /// <summary>Куда лежит файл настроек — для «Диагностики».</summary>
    public static string SettingsPath => AppSettings.DefaultPath;

    public static string Version => SettingsViewModel.Version;

    /// <summary>Заводит пароль или меняет прежний.</summary>
    ///
    /// <remarks>
    /// Применяется сразу, а не черновиком, и это не оплошность: пароль — не
    /// настройка рабочего места, а ключ от него, и «сменил, но не сохранил»
    /// означало бы замок в двух состояниях одновременно. Замок при этом
    /// остаётся открытым: администратор уже вошёл, и выгонять его за смену
    /// пароля незачем.
    /// </remarks>
    public void SetPassword()
    {
        if (!CanSetPassword)
        {
            return;
        }

        _access.SetPassword(_newPassword);
        _settings.Admin.From(_access.Credential);

        NewPassword = string.Empty;
        RepeatedPassword = string.Empty;
        NotifyChanged(nameof(IsProtected));
    }

    /// <summary>Снимает пароль. Машина остаётся настроенной, но открытой.</summary>
    public void RemovePassword()
    {
        _access.RemovePassword();
        _settings.Admin.From(credential: null);
        NotifyChanged(nameof(IsProtected));
    }

    /// <summary>Добавляет пустую клавишу — её тут же и правят.</summary>
    public void AddMacro()
    {
        Macros.Add(new MacroSetting());
        MarkDirty();
    }

    public void RemoveMacro(MacroSetting macro)
    {
        Macros.Remove(macro);
        MarkDirty();
    }

    /// <summary>Двигает клавишу в списке: порядок в сетке — это порядок здесь.</summary>
    ///
    /// <remarks>
    /// Порядок значим: оператор целится в место, а не читает каждый раз, и
    /// переставленная клавиша стоит ему промаха на неделю вперёд.
    /// </remarks>
    public void MoveMacro(MacroSetting macro, int offset)
    {
        var from = Macros.IndexOf(macro);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Macros.Count)
        {
            return;
        }

        Macros.Move(from, to);
        MarkDirty();
    }

    /// <summary>Пишет черновик в настройки.</summary>
    public void Save()
    {
        _settings.Dtmf.Macros.Clear();
        foreach (var macro in Macros)
        {
            _settings.Dtmf.Macros.Add(new MacroSetting
            {
                Id = macro.Id,
                Title = macro.Title,
                Sequence = macro.Sequence,
                TransfersCall = macro.TransfersCall,
            });
        }

        _settings.Dtmf.MacroColumns = MacroColumns;
        _settings.Dtmf.MacroHeight = MacroHeight;
        _settings.Dtmf.MacroHeightIsManual = MacroHeightIsManual;

        _settings.History.IsEnabled = HistoryIsEnabled;
        _settings.History.MaximumAgeInDays = HistoryAgeInDays;

        _settings.Account.Username = Username;
        _settings.Account.DisplayName = DisplayName;

        // Пустое поле означает «оставить прежний», а не «стереть»: стирают
        // паролем в одно нажатие только по ошибке.
        if (SipPassword.Length > 0)
        {
            _settings.Credentials.SetPassword(SipPassword);
            SipPassword = string.Empty;
            NotifyChanged(nameof(HasSipPassword));
        }

        _settings.Pbx.OfficeAddress = OfficeAddress;
        _settings.Pbx.RemoteAddress = RemoteAddress;
        _settings.Pbx.Port = Port;
        _settings.Pbx.Transport = Transport;
        _settings.Pbx.PinnedCertificateFingerprint = PinnedCertificateFingerprint.Trim();

        _settings.PortKnock.SpacingSeconds = KnockSpacingSeconds;
        _settings.PortKnock.RepeatIntervalSeconds = KnockRepeatIntervalSeconds;
        _settings.PortKnock.Steps.Clear();
        foreach (var step in KnockSteps)
        {
            _settings.PortKnock.Steps.Add(new PortKnockStepSetting
            {
                Id = step.Id,
                Host = step.Host.Trim(),
                PayloadBytes = step.PayloadBytes,
                Count = step.Count,
            });
        }
        _settings.Pbx.AcceptsAnyTlsCertificate = AcceptsAnyTlsCertificate;
        _settings.Pbx.RegistrationExpirySeconds = RegistrationExpirySeconds;
        _settings.Pbx.TransferFeatureCode = TransferFeatureCode;
        _settings.Pbx.ConferenceFeatureCode = ConferenceFeatureCode;

        // Адрес, которым пользуется панель, идёт от выбранной площадки: её
        // выбирает человек в менеджерских настройках, а пару адресов заводит
        // администратор здесь.
        _settings.Account.Domain = _settings.Account.Site is WorkplaceSite.Office
            ? OfficeAddress
            : RemoteAddress;

        _settings.Queues.Queues.Clear();
        foreach (var queue in Queues)
        {
            _settings.Queues.Queues.Add(new QueueSetting
            {
                Id = queue.Id,
                Number = queue.Number,
                Title = queue.Title,
            });
        }

        _settings.IncomingCall.CopyFrom(Guard);

        _settings.Maintenance.LogToFile = LogToFile;
        _settings.Maintenance.LogsSipTrace = LogsSipTrace;

        // Запись на диск делает сама настройка (AutoSave); здесь остаётся
        // только снять пометку несохранённого.
        IsDirty = false;
    }

    /// <summary>Возвращает черновик к тому, что записано.</summary>
    public void Revert()
    {
        Macros.Clear();
        foreach (var macro in _settings.Dtmf.Macros)
        {
            var copy = new MacroSetting
            {
                Id = macro.Id,
                Title = macro.Title,
                Sequence = macro.Sequence,
                TransfersCall = macro.TransfersCall,
            };

            // Правка внутри клавиши — тоже несохранённое: без подписки точка у
            // пункта списка не загоралась бы, пока клавиши не добавляли и не
            // убирали.
            copy.PropertyChanged += (_, _) => MarkDirty();
            Macros.Add(copy);
        }

        Queues.Clear();
        foreach (var queue in _settings.Queues.Queues)
        {
            var copy = new QueueSetting { Id = queue.Id, Number = queue.Number, Title = queue.Title };
            copy.PropertyChanged += (_, _) => MarkDirty();
            Queues.Add(copy);
        }

        _username = _settings.Account.Username;
        _displayName = _settings.Account.DisplayName;
        _sipPassword = string.Empty;
        _officeAddress = _settings.Pbx.OfficeAddress;
        _remoteAddress = _settings.Pbx.RemoteAddress;
        _port = _settings.Pbx.Port;
        _transport = _settings.Pbx.Transport;
        _pinnedFingerprint = _settings.Pbx.PinnedCertificateFingerprint;
        _knockSpacing = _settings.PortKnock.SpacingSeconds;
        _knockRepeat = _settings.PortKnock.RepeatIntervalSeconds;

        KnockSteps.Clear();
        foreach (var step in _settings.PortKnock.Steps)
        {
            PortKnockStepSetting copy = new()
            {
                Id = step.Id,
                Host = step.Host,
                PayloadBytes = step.PayloadBytes,
                Count = step.Count,
            };

            copy.PropertyChanged += (_, _) =>
            {
                MarkDirty();
                NotifyChanged(nameof(KnockSummary));
            };

            KnockSteps.Add(copy);
        }
        _acceptsAnyCertificate = _settings.Pbx.AcceptsAnyTlsCertificate;
        _registrationExpiry = _settings.Pbx.RegistrationExpirySeconds;
        _transferCode = _settings.Pbx.TransferFeatureCode;
        _conferenceCode = _settings.Pbx.ConferenceFeatureCode;
        _logToFile = _settings.Maintenance.LogToFile;
        _logsSipTrace = _settings.Maintenance.LogsSipTrace;
        _macroColumns = _settings.Dtmf.MacroColumns;
        _macroHeight = _settings.Dtmf.MacroHeight;
        _macroHeightIsManual = _settings.Dtmf.MacroHeightIsManual;
        Guard.CopyFrom(_settings.IncomingCall);

        _historyIsEnabled = _settings.History.IsEnabled;
        _historyAgeInDays = _settings.History.MaximumAgeInDays;

        foreach (var name in new[]
        {
            nameof(MacroColumns), nameof(MacroHeight), nameof(MacroHeightIsManual),
            nameof(HistoryIsEnabled), nameof(HistoryAgeInDays),
            nameof(Username), nameof(DisplayName), nameof(SipPassword), nameof(HasSipPassword),
            nameof(OfficeAddress), nameof(RemoteAddress), nameof(Port), nameof(Transport),
            nameof(RegistrationExpirySeconds), nameof(TransferFeatureCode), nameof(ConferenceFeatureCode),
            nameof(LogToFile), nameof(LogsSipTrace),
        })
        {
            NotifyChanged(name);
        }

        IsDirty = false;
    }

    private void MarkDirty() => IsDirty = true;
}
