using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.Audio;

namespace EliteSIP.App.Settings;

/// <summary>Разделы менеджерских настроек.</summary>
///
/// <remarks>
/// Порядок — по тому, как часто сюда заходят. «Работа» первой: её открывают в
/// начале смены и по самому важному поводу — телефон не работает вовсе, пока
/// рабочее место выбрано неверно. «Звук» следом, потому что его правят при
/// каждой смене наушников; «Техподдержка» последней, потому что туда идут,
/// когда уже сломалось.
/// </remarks>
public enum SettingsSectionKind
{
    Work,
    Audio,
    Ringtone,
    Appearance,
    Support,
}

/// <summary>Пункт бокового списка.</summary>
public sealed record SettingsSectionItem(SettingsSectionKind Kind, string Title, string Glyph);

/// <summary>Устройство в выпадающем списке. <c>null</c> в поле — «системное».</summary>
///
/// <remarks>
/// Пункт «системное по умолчанию» — не отсутствие выбора, а выбор: он значит
/// «ходи за устройством к системе каждый раз», и от него зависит, переедет ли
/// звук вслед за воткнутой гарнитурой.
/// </remarks>
public sealed record AudioDeviceOption(string? Id, string Name);

/// <summary>Состояние окна настроек менеджера.</summary>
public sealed class SettingsViewModel : Observable
{
    private SettingsSectionKind _section = SettingsSectionKind.Work;

    public SettingsViewModel(AppSettings settings)
    {
        Settings = settings;

        Sections =
        [
            new(SettingsSectionKind.Work, Strings.Get("SectionWork"), "person.crop.circle"),
            new(SettingsSectionKind.Audio, Strings.Get("SectionAudio"), "mic.fill"),
            new(SettingsSectionKind.Ringtone, Strings.Get("SectionRingtone"), "bell"),
            // Своего значка у оформления в комплекте нет. `gearshape` — не
            // «тема», а «настройка вообще», и это признанная неточность: раздел
            // опознаётся подписью, а значок держит строку в колонке с прочими.
            new(SettingsSectionKind.Appearance, Strings.Get("SectionAppearance"), "gearshape"),
            new(SettingsSectionKind.Support, Strings.Get("SectionSupport"), "stethoscope"),
        ];

        ReloadDevices();

        // Без условия «замыкание подставлено».
        //
        // Команды заводятся в конструкторе, а замыкания приходят инициализатором
        // объекта — то есть позже. Условие успевало посчитаться на пустом
        // значении, и «Исправить сеть» оставалась серой навсегда: пересчитать
        // его было некому. Проверка вернулась внутрь самого действия.
        RepairNetwork = new RelayCommand(_ => OnRepairNetwork?.Invoke());
        CheckPresets = new RelayCommand(_ => OnCheckPresets?.Invoke());
        CollectLogs = new RelayCommand(_ => OnCollectLogs?.Invoke());
        RunSelfTest = new RelayCommand(_ => OnRunSelfTest?.Invoke());

        ApplyKey = new RelayCommand(
            async _ => await ApplyKeyAsync(),
            _ => !_isApplyingKey && _newKey.Trim().Length > 0);
    }

    public AppSettings Settings { get; }

    // --- Кнопки, которые прежде были нарисованы и никуда не вели --------------
    //
    // Раздел настроек собирали макетом: у «Исправить сеть», «Собрать логи»,
    // «Применить ключ» и самопроверки звука стояло `IsEnabled="False"`, и ни
    // одна не была подключена. Заводим их через замыкания от приложения — оно
    // владеет и телефоном, и линией панели, а окно настроек о них не знает.

    /// <summary>Стук по портам прямо сейчас.</summary>
    public RelayCommand RepairNetwork { get; }

    /// <summary>Спросить панель о предустановках, не дожидаясь такта.</summary>
    public RelayCommand CheckPresets { get; }

    /// <summary>Собрать архив с журналом для поддержки.</summary>
    public RelayCommand CollectLogs { get; }

    internal Action? OnRepairNetwork { get; init; }

    internal Action? OnCheckPresets { get; init; }

    internal Action? OnCollectLogs { get; init; }

    /// <summary>Запустить самопроверку звука. Ставит приложение — тракт его.</summary>
    internal Action? OnRunSelfTest { get; init; }

    private string? _supportResult;
    private string _newKey = string.Empty;
    private string? _newKeyResult;
    private bool _newKeyFailed;
    private bool _isApplyingKey;

    /// <summary>Чем кончилось последнее нажатие в «Техподдержке».</summary>
    public string? SupportResult => _supportResult;

    public bool HasSupportResult => _supportResult is not null;

    /// <summary>Показать человеку, чем кончилось нажатие.</summary>
    ///
    /// <remarks>
    /// Строка результата — своя у каждой кнопки и стоит под ней.
    ///
    /// Прежде строка была одна на три кнопки из двух разных страниц, и стояла
    /// на «Техподдержке». Итог «Исправить сеть», которая живёт на «Работе»,
    /// оператор не видел вовсе: нажатие выглядело ничем.
    /// </remarks>
    internal void ReportSupport(string message, SupportArea area)
    {
        _supportResult = message;
        _supportArea = area;
        NotifyChanged(nameof(SupportResult));
        NotifyChanged(nameof(HasSupportResult));
        NotifyChanged(nameof(HasLogsResult));
        NotifyChanged(nameof(HasPresetsResult));
        NotifyChanged(nameof(HasNetworkResult));
    }

    private SupportArea _supportArea;

    public bool HasLogsResult => _supportResult is not null && _supportArea is SupportArea.Logs;

    public bool HasPresetsResult => _supportResult is not null && _supportArea is SupportArea.Presets;

    public bool HasNetworkResult => _supportResult is not null && _supportArea is SupportArea.Network;

    // --- Самопроверка звука --------------------------------------------------

    private string? _selfTestResult;

    /// <summary>Запустить запись и воспроизведение.</summary>
    public RelayCommand RunSelfTest { get; private set; } = null!;

    /// <summary>Что вышло: и «говорите» по ходу, и итог.</summary>
    public string? SelfTestResult => _selfTestResult;

    public bool HasSelfTestResult => _selfTestResult is not null;

    /// <summary>Строка о ходе или итоге проверки. Зовётся из потока интерфейса.</summary>
    internal void ReportSelfTest(string message)
    {
        _selfTestResult = message;
        NotifyChanged(nameof(SelfTestResult));
        NotifyChanged(nameof(HasSelfTestResult));
    }

    // --- Новый ключ ----------------------------------------------------------

    /// <summary>Ключ, который вводит человек. Нигде не сохраняется.</summary>
    public string NewKey
    {
        get => _newKey;
        set
        {
            Set(ref _newKey, value);
            ApplyKey.RaiseCanExecuteChanged();
        }
    }

    public bool CanTypeNewKey => !_isApplyingKey;

    public string? NewKeyResult => _newKeyResult;

    public bool HasNewKeyResult => _newKeyResult is not null;

    /// <summary>Тревожная ли приписка. Отказ красный, успех обычный.</summary>
    public bool NewKeyFailed => _newKeyFailed;

    /// <summary>Применить ключ смены рабочего места.</summary>
    public RelayCommand ApplyKey { get; private set; } = null!;

    /// <summary>
    /// Что делает приложение с введённым ключом.
    /// </summary>
    ///
    /// <remarks>
    /// Замыканием: заход на канал, распечатывание пакета и наложение на
    /// настройки — дело приложения, а окно настроек про панель не знает.
    /// Возвращает строку для человека; исключений наружу не выпускает.
    /// </remarks>
    internal Func<string, Task<(bool Ok, string Message)>>? OnApplyKey { get; init; }

    private async Task ApplyKeyAsync()
    {
        if (OnApplyKey is null || _isApplyingKey)
        {
            return;
        }

        _isApplyingKey = true;
        _newKeyResult = Strings.Get("SupportKeyChecking");
        _newKeyFailed = false;
        NotifyKeyState();

        var (ok, message) = await OnApplyKey(_newKey).ConfigureAwait(true);

        _isApplyingKey = false;
        _newKeyResult = message;
        _newKeyFailed = !ok;

        // Удачный ключ сгорел — поле чистится, чтобы его не нажали второй раз.
        if (ok)
        {
            _newKey = string.Empty;
            NotifyChanged(nameof(NewKey));
        }

        NotifyKeyState();
    }

    private void NotifyKeyState()
    {
        foreach (var name in new[]
        {
            nameof(NewKeyResult), nameof(HasNewKeyResult), nameof(NewKeyFailed), nameof(CanTypeNewKey),
        })
        {
            NotifyChanged(name);
        }

        ApplyKey.RaiseCanExecuteChanged();
    }

    public IReadOnlyList<SettingsSectionItem> Sections { get; }

    public SettingsSectionKind Section
    {
        get => _section;
        set
        {
            Set(ref _section, value);
            foreach (var name in new[]
            {
                nameof(ShowsWork), nameof(ShowsAudio), nameof(ShowsRingtone),
                nameof(ShowsAppearance), nameof(ShowsSupport),
            })
            {
                NotifyChanged(name);
            }
        }
    }

    public bool ShowsWork => _section is SettingsSectionKind.Work;

    /// <summary>
    /// Запускать софтфон вместе с входом в систему.
    /// </summary>
    ///
    /// <remarks>
    /// Здесь, в «Техподдержке», а не в «Управлении»: автозапуск пишется в ветку
    /// пользователя, прав администратора не требует, и решать его должен тот,
    /// кто за машиной работает. Действует сразу — правда о нём живёт в реестре,
    /// его же показывает диспетчер задач, и читается тоже у системы: снятый там
    /// автозапуск обязан показаться снятым и здесь.
    /// </remarks>
    public bool StartsWithWindows
    {
        get => Shell.AutoStart.IsEnabled;
        set
        {
            _ = Shell.AutoStart.Set(value);
            NotifyChanged();
        }
    }

    public bool ShowsAudio => _section is SettingsSectionKind.Audio;

    public bool ShowsRingtone => _section is SettingsSectionKind.Ringtone;

    public bool ShowsAppearance => _section is SettingsSectionKind.Appearance;

    public bool ShowsSupport => _section is SettingsSectionKind.Support;

    public ObservableCollection<AudioDeviceOption> Inputs { get; } = [];

    public ObservableCollection<AudioDeviceOption> Outputs { get; } = [];

    /// <summary>
    /// Оба устройства заданы явно и разными — значит системного эхоподавления
    /// не будет.
    /// </summary>
    ///
    /// <remarks>
    /// Текст под этим признаком не обещает эха, и это правка после разбора
    /// макета: эха не будет ни в наушниках, ни в гарнитуре — там микрофон
    /// акустически развязан с динамиком. Эхо случается на колонках. Прежняя
    /// формулировка пугала им всегда, то есть чаще всего впустую.
    /// </remarks>
    public bool NeedsAggregate
        => Settings.Audio.InputDeviceId is not null && Settings.Audio.OutputDeviceId is not null;

    /// <summary>Версия и сборка одной строкой.</summary>
    ///
    /// <remarks>
    /// Первое, что просит поддержка по телефону, — «какая у вас версия». В
    /// оригинале эта строка сперва стояла только за административным паролем, и
    /// менеджеру было нечего ответить.
    /// </remarks>
    /// <remarks>
    /// Хвост после «+» отрезается. Сборка кладёт в осведомительную версию
    /// отпечаток последнего коммита, и человек по телефону читал бы поддержке
    /// сорок знаков шестнадцатеричного мусора вместо «ноль один один».
    /// </remarks>
    public static string Version
    {
        get
        {
            var full = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrEmpty(full))
            {
                return "—";
            }

            var plus = full.IndexOf('+', StringComparison.Ordinal);

            return plus < 0 ? full : full[..plus];
        }
    }

    /// <summary>
    /// Линия обновлений. <c>null</c> — не заведена.
    /// </summary>
    ///
    /// <remarks>
    /// Открытое свойство, а не внутреннее, и это не формальность: привязка WPF
    /// ищет свойство отражением и до внутреннего не дотягивается — молча, без
    /// ошибки. Из-за этого в 0.1.2 и 0.1.4 «Проверить обновления» не нажималась,
    /// а «Установить обновление» висела всегда: обе привязки просто не
    /// разрешались.
    /// </remarks>
    public Admin.UpdatesViewModel? Updates { get; init; }

    /// <summary>Имя выбранного рингтона — или «Стандартный».</summary>
    public string RingtoneName
    {
        get
        {
            if (!Settings.Ringtone.HasCustomSound)
            {
                return Strings.Get("RingtoneDefault");
            }

            var name = Path.GetFileName(Settings.Ringtone.CustomSoundPath)!;

            // Пропавший файл называется прямо: рингтон в этом случае молча
            // вернётся к стандартному, и человек должен понимать почему, а не
            // слышать не то.
            return Settings.Ringtone.SoundIsMissing
                ? name + Strings.Get("RingtoneMissingSuffix")
                : name;
        }
    }

    /// <summary>Толкает подпись рингтона: она считается, а не хранится.</summary>
    public void NotifyRingtoneName() => NotifyChanged(nameof(RingtoneName));

    /// <summary>Возвращает сегменты языка к записанному в настройках.</summary>
    public void NotifyLanguage() => Settings.Appearance.NotifyLanguageChanged();

    /// <summary>Перечитывает устройства у системы.</summary>
    ///
    /// <remarks>
    /// Списки берутся готовыми при создании окна, а не спрашиваются у Core Audio
    /// при показе раздела: в оригинале именно такой опрос давал видимую
    /// задержку — он успевал не к первой отрисовке, а к следующей, и строки под
    /// выпадающими списками появлялись на глазах, сдвигая выключатели вниз.
    /// </remarks>
    public void ReloadDevices()
    {
        Fill(Inputs, AudioDeviceDirection.Capture, Strings.Get("AudioSystemDefaultInput"));
        Fill(Outputs, AudioDeviceDirection.Render, Strings.Get("AudioSystemDefaultOutput"));
        NotifyChanged(nameof(NeedsAggregate));
    }

    private static void Fill(
        ObservableCollection<AudioDeviceOption> target,
        AudioDeviceDirection direction,
        string systemTitle)
    {
        target.Clear();

        // «Системное» первым пунктом, а не отдельным выключателем рядом: это
        // одно и то же поле — какое устройство брать.
        var system = AudioDeviceCatalog.Default(direction);
        target.Add(new AudioDeviceOption(
            Id: null,
            Name: system is null ? systemTitle : $"{systemTitle} — {system.Name}"));

        foreach (var device in AudioDeviceCatalog.Devices(direction))
        {
            target.Add(new AudioDeviceOption(device.Id, device.Name));
        }
    }
}

/// <summary>Под какой кнопкой показать итог нажатия.</summary>
public enum SupportArea
{
    Logs,
    Presets,
    Network,
}
