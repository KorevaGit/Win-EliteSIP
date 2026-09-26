using System.Text.Json.Serialization;
using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;

namespace EliteSIP.App.Settings;

/// <summary>Режим машины: слушать панель или жить своим умом.</summary>
///
/// <remarks>
/// Действует на машину целиком, а не на отдельные настройки: половинчатый режим
/// означал бы, что администратор не может ответить на вопрос «управляется ли
/// это рабочее место», не перебрав два десятка полей.
/// </remarks>
public enum PanelMode
{
    /// <summary>
    /// Файл предустановок не применяется ни в чём. Это же — состояние машины,
    /// которую поднимали не ключом.
    /// </summary>
    Manual,

    /// <summary>
    /// Файл предустановок применяется. Управляемые поля показываются, но не
    /// редактируются локально.
    /// </summary>
    Managed,
}

/// <summary>Что машина знает о панели: чем она для неё опознаётся и чем ходит.</summary>
public sealed class PanelSettings : Observable
{
    private string _installationID = string.Empty;
    private string? _protectedChannelKey;
    private string _presetID = string.Empty;
    private string _presetName = string.Empty;
    private int _appliedRevision;
    private DateTimeOffset? _appliedAt;
    private DateTimeOffset? _lastContactAt;
    private PanelMode _mode = PanelMode.Manual;
    private bool _wantsResync;
    private string? _protectedMachineKey;
    private int _appliedConfigRevision;
    private bool _machineKeyRegistered;
    private string _lastNumberNotice = string.Empty;

    /// <summary>
    /// Единственное, по чему панель вообще узнаёт эту машину.
    /// </summary>
    ///
    /// <remarks>
    /// Приезжает в пакете активации и сообщается при каждом запросе за файлом
    /// предустановок. <b>Не теряется при сбросе учётки</b>: учётка — это
    /// телефон, а идентификатор — это машина.
    /// </remarks>
    public string InstallationID
    {
        get => _installationID;
        set
        {
            Set(ref _installationID, value);
            NotifyChanged(nameof(IsActivated));
            NotifyChanged(nameof(HasChannelKey));
            NotifyChanged(nameof(IsManaged));
        }
    }

    /// <summary>
    /// Помашинный ключ доступа к каналу раздачи, зашифрованный DPAPI.
    /// </summary>
    ///
    /// <remarks>
    /// Приезжает в пакете активации. Им машина берёт файл предустановок, свой
    /// административный пароль и проверяет отзыв; общая пара из установщика
    /// открывает теперь только выпуски.
    ///
    /// <b>Панель убирает его на своей стороне — и машина перестаёт получать что
    /// бы то ни было.</b> Это и есть отзыв доступа.
    ///
    /// Под DPAPI по тому же сквозному правилу, что и пароль SIP: на macOS он
    /// лежал в файле с правами <c>0600</c>, а на Windows права такой защиты не
    /// дают. Унесённый файл настроек ключа канала не отдаёт.
    /// </remarks>
    public string? ProtectedChannelKey
    {
        get => _protectedChannelKey;
        set
        {
            Set(ref _protectedChannelKey, value);
            NotifyChanged(nameof(HasChannelKey));
        }
    }

    /// <summary>
    /// Предустановка, под которой машина живёт.
    /// </summary>
    ///
    /// <remarks>
    /// Ищет она себя в файле по идентификатору, а <b>не по имени</b>: имя
    /// переименовывают, и поиск по нему разорвал бы связь у всех машин разом.
    /// Имя хранится рядом только затем, чтобы было что показать человеку.
    /// </remarks>
    public string PresetID
    {
        get => _presetID;
        set
        {
            Set(ref _presetID, value);
            NotifyChanged(nameof(IsManaged));
        }
    }

    public string PresetName
    {
        get => _presetName;
        set => Set(ref _presetName, value);
    }

    /// <summary>Применённая ревизия. Ноль означает «ни одной ещё не применяли».</summary>
    public int AppliedRevision
    {
        get => _appliedRevision;
        set => Set(ref _appliedRevision, value);
    }

    public DateTimeOffset? AppliedAt
    {
        get => _appliedAt;
        set => Set(ref _appliedAt, value);
    }

    /// <summary>
    /// Когда канал последний раз ответил.
    /// </summary>
    ///
    /// <remarks>
    /// Не то же, что <see cref="AppliedAt"/>: файл предустановок меняется раз в
    /// месяцы, а спрашивают его раз в два часа. Без этой отметки администратор
    /// узнавал бы о лежащем канале только тогда, когда правка не доехала.
    /// </remarks>
    public DateTimeOffset? LastContactAt
    {
        get => _lastContactAt;
        set => Set(ref _lastContactAt, value);
    }

    public PanelMode Mode
    {
        get => _mode;
        set
        {
            Set(ref _mode, value);
            NotifyChanged(nameof(IsManaged));
        }
    }

    /// <summary>
    /// Машина просит применить предустановку заново, даже если ревизия та же
    /// самая.
    /// </summary>
    ///
    /// <remarks>
    /// Нужен потому, что обычное правило линии — «применяем только то, что новее
    /// применённого», — и оно верно ровно до того момента, когда машину
    /// возвращают под предустановку после жизни своим умом. Ревизия за это время
    /// не менялась, локальные правки накопились, и без этого признака возврат не
    /// возвращал <b>ничего</b>: кнопка обещала заменить правки серверными, а
    /// проверка отвечала «настройки уже свежие».
    ///
    /// Живёт в настройках, а не в памяти: возврат нажимают в «Управлении», а
    /// применяется он следующим заходом на канал — между ними умещается и
    /// «Сохранить», и перезапуск, и разговор.
    ///
    /// Снимается тем, кто применил, — и только им.
    /// </remarks>
    public bool WantsResync
    {
        get => _wantsResync;
        set => Set(ref _wantsResync, value);
    }

    /// <summary>Слушает ли машина панель.</summary>
    ///
    /// <remarks>
    /// Отсюда выводится «управляется сервером» у защиты приёма вызова, и
    /// <b>только отсюда</b>: полем из файла он не приезжает, потому что это был
    /// бы второй источник одного факта.
    /// </remarks>
    [JsonIgnore]
    public bool IsManaged => Mode == PanelMode.Managed && PresetID.Length > 0;

    /// <summary>Знает ли панель об этой машине вообще.</summary>
    [JsonIgnore]
    public bool IsActivated => InstallationID.Length > 0;

    /// <summary>
    /// Есть ли чем ходить на канал за своим.
    /// </summary>
    ///
    /// <remarks>
    /// Отдельно от <see cref="IsActivated"/>: машина, поднятая ключом старого
    /// образца, панель знает, а ключа канала у неё нет — и ходить ей нечем, пока
    /// не перепрошьют.
    /// </remarks>
    [JsonIgnore]
    public bool HasChannelKey => IsActivated && !string.IsNullOrEmpty(_protectedChannelKey);

    /// <summary>Кладёт ключ канала под DPAPI. Пустой — стирает.</summary>
    public void SetChannelKey(string key)
        => ProtectedChannelKey = string.IsNullOrEmpty(key) ? null : ProtectedSecret.Protect(key);

    /// <summary>Достаёт ключ канала. <c>null</c> — расшифровать не удалось.</summary>
    ///
    /// <remarks>
    /// Не удалось — это законный исход: файл настроек, принесённый с чужой
    /// машины, расшифровке не поддаётся по построению. Линия панели тогда просто
    /// молчит, и машина живёт тем, что применила раньше, — ровно как при
    /// лежащем канале.
    /// </remarks>
    public string? ChannelKey()
        => _protectedChannelKey is null ? null : ProtectedSecret.Unprotect(_protectedChannelKey);

    /// <summary>
    /// Закрытый ключ машины X25519 под DPAPI (base64 внутри).
    /// </summary>
    ///
    /// <remarks>
    /// Им машина открывает свою конфигурацию из Spark. Создаётся в мастере до
    /// привязки (или при регистрации машины, поднятой ключом активации), живёт
    /// до сброса. <c>null</c> у машины, поднятой ключом и ещё не
    /// зарегистрировавшей свой ключ: ей Spark отдаёт только <c>access/</c>
    /// старого образца.
    /// </remarks>
    public string? ProtectedMachineKey
    {
        get => _protectedMachineKey;
        set
        {
            Set(ref _protectedMachineKey, value);
            NotifyChanged(nameof(HasMachineKey));
        }
    }

    /// <summary>Ревизия применённой конфигурации — заголовок <c>X-EliteSIP-Config</c>.</summary>
    ///
    /// <remarks>
    /// По нему Spark показывает в карточке сотрудника «Работает»: правка дошла.
    /// Ноль — ещё не применялась; так же обнуляется возвратом в онлайн, чтобы та
    /// же ревизия легла заново поверх местных правок.
    /// </remarks>
    public int AppliedConfigRevision
    {
        get => _appliedConfigRevision;
        set => Set(ref _appliedConfigRevision, value);
    }

    /// <summary>
    /// Номер, о смене которого панель уже сказала: «Администратор сменил номер:
    /// 205». Пусто — сказать нечего.
    /// </summary>
    public string LastNumberNotice
    {
        get => _lastNumberNotice;
        set => Set(ref _lastNumberNotice, value);
    }

    /// <summary>
    /// Spark знает открытый ключ этой машины: привязана по коду или машина,
    /// поднятая ключом активации, уже зарегистрировала свой.
    /// </summary>
    public bool MachineKeyRegistered
    {
        get => _machineKeyRegistered;
        set => Set(ref _machineKeyRegistered, value);
    }

    [JsonIgnore]
    public bool HasMachineKey => !string.IsNullOrEmpty(_protectedMachineKey);

    /// <summary>
    /// Машина в оффлайне: связь со Spark есть чем вернуть, но она выключена.
    /// </summary>
    ///
    /// <remarks>
    /// Уходит в оффлайн машина, на которой в «Управлении» сохранили правки: с
    /// этой минуты Spark её настроек не трогает — ни конфигурацией, ни
    /// предустановкой. Ключ канала и привязка при этом остаются, поэтому вернуть
    /// её можно кнопкой «Вернуться в онлайн», без новой привязки. Отзыв
    /// работает и в оффлайне: отвязанная в Spark машина сбрасывается всё равно.
    /// </remarks>
    [JsonIgnore]
    public bool IsOffline => Mode == PanelMode.Manual && HasChannelKey;

    /// <summary>Сохраняет ключ машины под DPAPI.</summary>
    public void SetMachineKey(string privateKeyBase64)
        => ProtectedMachineKey = string.IsNullOrEmpty(privateKeyBase64)
            ? null
            : ProtectedSecret.Protect(privateKeyBase64);

    /// <summary>Ключ машины. <c>null</c> — нет или не расшифровался.</summary>
    public string? MachineKey()
        => _protectedMachineKey is null ? null : ProtectedSecret.Unprotect(_protectedMachineKey);
}
