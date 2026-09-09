using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteSIP.App.Panel;

// Псевдоним: у раздела оформления есть свойство `Theme`, и оно закрывает собой
// одноимённое пространство имён — тип пришлось бы писать полным путём в каждой
// строке.
using AppearanceMode = EliteSIP.App.Theme.Appearance;

namespace EliteSIP.App.Settings;

/// <summary>Откуда человек работает сегодня.</summary>
///
/// <remarks>
/// Не пометка, а переезд: вместе с местом меняется адрес АТС по паре из
/// настроек, регистрация снимается и поднимается заново.
///
/// Третьего значения «определять самому» здесь нет и не будет: оно означало бы
/// «не выбирать», а выбирает как раз человек — тот же менеджер с тем же номером
/// сидит то в офисе, то дома, и знает об этом только он.
/// </remarks>
public enum WorkplaceSite
{
    Office,
    Remote,
}

/// <summary>Язык интерфейса: как в системе, русский, английский.</summary>
public enum LanguageSetting
{
    System,
    Russian,
    English,
}

/// <summary>Куда играть рингтон.</summary>
public enum RingtoneOutput
{
    /// <summary>Системное устройство: гарнитуру на столе не слышно.</summary>
    SystemDefault,

    /// <summary>То же устройство, что и разговор.</summary>
    CallDevice,
}

/// <summary>Звук: устройства, усиление, громкость.</summary>
public sealed class AudioSettings : Observable
{
    private string? _inputDeviceId;
    private string? _inputDeviceName;
    private string? _outputDeviceId;
    private string? _outputDeviceName;
    private double _microphoneGain = 1.0;
    private double _playbackVolume = 1.0;
    private bool _automaticGainControl = true;
    private bool _releasesDeviceWhenIdle = true;

    /// <summary>Постоянный идентификатор конечной точки. <c>null</c> — системное.</summary>
    ///
    /// <remarks>
    /// Идентификатор один, а не два, как было на macOS: строка конечной точки
    /// Windows переживает и перезагрузку, и переподключение устройства.
    /// Имя рядом хранится только для человека — опознавать по нему нельзя.
    /// </remarks>
    public string? InputDeviceId
    {
        get => _inputDeviceId;
        set => Set(ref _inputDeviceId, value);
    }

    public string? InputDeviceName
    {
        get => _inputDeviceName;
        set => Set(ref _inputDeviceName, value);
    }

    public string? OutputDeviceId
    {
        get => _outputDeviceId;
        set => Set(ref _outputDeviceId, value);
    }

    public string? OutputDeviceName
    {
        get => _outputDeviceName;
        set => Set(ref _outputDeviceName, value);
    }

    /// <summary>Своя ручка усиления: у половины гарнитур своей нет вовсе.</summary>
    public double MicrophoneGain
    {
        get => _microphoneGain;
        set => Set(ref _microphoneGain, value);
    }

    /// <summary>Выше единицы не идёт: микшер громче не умеет.</summary>
    ///
    /// <remarks>
    /// Ручка, которая двигается и ничего не меняет, хуже её отсутствия.
    /// Системная громкость сюда не годится — она меняет звук всей машины, а
    /// тише надо сделать только собеседника.
    /// </remarks>
    public double PlaybackVolume
    {
        get => _playbackVolume;
        set => Set(ref _playbackVolume, value);
    }

    /// <summary>Единственное, что в обработке голоса действительно спорно.</summary>
    public bool AutomaticGainControl
    {
        get => _automaticGainControl;
        set
        {
            Set(ref _automaticGainControl, value);
            NotifyChanged(nameof(GainIsAdjustable));
        }
    }

    /// <summary>Можно ли тянуть ручку усиления руками.</summary>
    ///
    /// <remarks>
    /// При включённой автоматике ручка гаснет, и это не придирка к виду: она
    /// отдаёт уровень входа обработчику, тот его тут же переставляет по своему
    /// счёту — и ползунок оказывается регулятором, который двигается и ничего
    /// не меняет. Оператор при этом тянет его вправо, слышит «меня всё так же
    /// плохо слышно» и делает единственный доступный вывод: сломано приложение.
    /// </remarks>
    [JsonIgnore]
    public bool GainIsAdjustable => !_automaticGainControl;

    /// <summary>Отпускать ли устройство между звонками.</summary>
    public bool ReleasesDeviceWhenIdle
    {
        get => _releasesDeviceWhenIdle;
        set => Set(ref _releasesDeviceWhenIdle, value);
    }
}

/// <summary>Рингтон: играть ли, как громко, куда и чем.</summary>
///
/// <remarks>
/// Звук на своём рабочем месте человек выбирает сам — потому раздел и
/// менеджерский, а не административный.
/// </remarks>
public sealed class RingtoneSettings : Observable
{
    private bool _isEnabled = true;
    private double _volume = 0.7;
    private RingtoneOutput _output = RingtoneOutput.SystemDefault;
    private string? _customSoundPath;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public double Volume
    {
        get => _volume;
        set => Set(ref _volume, value);
    }

    public RingtoneOutput Output
    {
        get => _output;
        set => Set(ref _output, value);
    }

    /// <summary>Свой файл или <c>null</c> — стандартный.</summary>
    public string? CustomSoundPath
    {
        get => _customSoundPath;
        set
        {
            Set(ref _customSoundPath, value);
            NotifyChanged(nameof(HasCustomSound));
            NotifyChanged(nameof(SoundIsMissing));
        }
    }

    [JsonIgnore]
    public bool HasCustomSound => !string.IsNullOrEmpty(_customSoundPath);

    /// <summary>Файл выбран, но его нет на диске.</summary>
    ///
    /// <remarks>
    /// Пропавший файл называется прямо: рингтон в этом случае молча вернётся к
    /// стандартному, и человек должен понимать почему, а не слышать не то.
    /// </remarks>
    [JsonIgnore]
    public bool SoundIsMissing => HasCustomSound && !File.Exists(_customSoundPath);
}

/// <summary>Оформление и язык.</summary>
public sealed class AppearanceSettings : Observable
{
    private AppearanceMode _theme = AppearanceMode.System;
    private LanguageSetting _language = LanguageSetting.System;

    public AppearanceMode Theme
    {
        get => _theme;
        set => Set(ref _theme, value);
    }

    /// <summary>Язык интерфейса. Меняется только вместе с перезапуском.</summary>
    public LanguageSetting Language
    {
        get => _language;
        set => Set(ref _language, value);
    }

    /// <summary>
    /// Сообщить о языке, ничего не меняя, — чтобы сегменты вернулись к
    /// записанному после отказа от перезапуска.
    /// </summary>
    public void NotifyLanguageChanged() => NotifyChanged(nameof(Language));
}

/// <summary>Учётка и рабочее место — то немногое из них, что видит менеджер.</summary>
public sealed class AccountSettings : Observable
{
    private Guid _profileId = Guid.NewGuid();
    private string _username = string.Empty;
    private string _displayName = string.Empty;
    private string _domain = string.Empty;
    private WorkplaceSite _site = WorkplaceSite.Office;

    /// <summary>Кому принадлежат звонки в истории.</summary>
    ///
    /// <remarks>
    /// Заводится сам при первом запуске и не меняется: история отобрана по нему
    /// жёстко, и смена значения означала бы, что все прежние звонки исчезли.
    /// Профилей в настройках пока один — их список приедет с «Управлением», и
    /// тогда это поле станет ссылкой на выбранный.
    /// </remarks>
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

    /// <summary>Отображаемое имя: то, что видит собеседник.</summary>
    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    /// <summary>Адрес АТС — на чтение: его подставляет переключатель места.</summary>
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
}

/// <summary>
/// Настройки приложения. Здесь пока только то, что правит менеджер; закрытая
/// часть приедет с окном «Управление».
/// </summary>
///
/// <remarks>
/// <b>Менеджерские настройки применяются сразу</b> — кнопок «Сохранить» и
/// «Отменить» в том окне нет и быть не может. Отсюда и запись: правка любого
/// поля кладётся на диск тем же движением. В «Управлении» будет иначе — там
/// запись придержана черновиком, потому что администратор правит рабочее место
/// целиком, а не громкость.
///
/// Формат — JSON, как в оригинале (<c>settings.json</c>), и по той же причине:
/// файл читают в поддержке глазами, приложив его к обращению.
/// </remarks>
public sealed class AppSettings : Observable
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Куда лечь файлу: <c>LocalApplicationData</c>, а не роуминговый.
    /// </summary>
    ///
    /// <remarks>
    /// Та же развилка, что у журнала: в домене роуминговый профиль ездит по
    /// сети, и настройки рабочего места уехали бы вместе с ним на чужую машину
    /// — вместе с выбранной там гарнитурой.
    /// </remarks>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EliteSIP",
        "settings.json");

    public AccountSettings Account { get; init; } = new();

    public AudioSettings Audio { get; init; } = new();

    public RingtoneSettings Ringtone { get; init; } = new();

    public AppearanceSettings Appearance { get; init; } = new();

    public DtmfSettings Dtmf { get; init; } = new();

    public HistorySettings History { get; init; } = new();

    /// <summary>Административный доступ. Правится только из «Управления».</summary>
    public AdminSettings Admin { get; init; } = new();

    public PbxSettings Pbx { get; init; } = new();

    public QueueSettings Queues { get; init; } = new();

    /// <summary>Защита приёма вызова: этап W9. Правится только из «Управления».</summary>
    public IncomingCallSettings IncomingCall { get; init; } = new();

    public MaintenanceSettings Maintenance { get; init; } = new();

    public SetupSettings Setup { get; init; } = new();

    /// <summary>Пароль учётки. Шифруется DPAPI — см. `SipCredentials`.</summary>
    public SipCredentials Credentials { get; init; } = new();

    /// <summary>Читает настройки или отдаёт умолчания.</summary>
    ///
    /// <remarks>
    /// Испорченный файл — не повод не запуститься: софтфон без настроек
    /// работает, софтфон, который не открылся, не работает вовсе. Разобранный
    /// разбор уходит в журнал слоем выше, а здесь остаются умолчания.
    /// </remarks>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Format)
                ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Кладёт настройки на диск. Зовётся на каждую правку.</summary>
    ///
    /// <remarks>
    /// Через временный файл с переименованием: запись поверх падает посреди
    /// файла ровно тогда, когда машину выключили кнопкой, и настройки рабочего
    /// места пропадают целиком. Переименование в пределах тома неделимо.
    /// </remarks>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Format));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Подписывает все разделы на запись при первой же правке.</summary>
    ///
    /// <remarks>
    /// Подписка, а не вызов <c>Save</c> из каждого сеттера: сеттеров три
    /// десятка, и забытый в одном из них означал бы настройку, которая
    /// применилась и не пережила перезапуск. Ищется такое неделями.
    /// </remarks>
    public void AutoSave(Action<Exception>? onFailure = null)
    {
        // Список клавиш — коллекция, и её правки уведомлением о свойстве не
        // приходят: подписываться надо и на сам список, и на каждую клавишу в
        // нём. Без этого переименованная клавиша применяется и не переживает
        // перезапуск.
        void WatchMacros()
        {
            foreach (var macro in Dtmf.Macros)
            {
                macro.PropertyChanged -= OnMacroChanged;
                macro.PropertyChanged += OnMacroChanged;
            }
        }

        void OnMacroChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change) => TrySave(onFailure);

        Dtmf.Macros.CollectionChanged += (_, _) =>
        {
            WatchMacros();
            TrySave(onFailure);
        };

        WatchMacros();

        foreach (var section in new Observable[] { Account, Audio, Ringtone, Appearance, Dtmf, History, Admin, Pbx, Queues, IncomingCall, Maintenance, Credentials, Setup })
        {
            section.PropertyChanged += (_, _) => TrySave(onFailure);
        }
    }
    private void TrySave(Action<Exception>? onFailure)
    {
        try
        {
            Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Не упасть: диск может быть полон или занят, а настройка уже
            // применена. Сообщить об этом — дело слоя выше.
            onFailure?.Invoke(exception);
        }
    }
}
