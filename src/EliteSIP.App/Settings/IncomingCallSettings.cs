using System.Text.Json.Serialization;
using EliteSIP.App.Panel;
using EliteSIP.CallGuard;

namespace EliteSIP.App.Settings;

/// <summary>Защита приёма вызова в том виде, в каком её правят и хранят.</summary>
///
/// <remarks>
/// Отдельный от <see cref="CallGuardPolicy"/> тип, а не сама политика в поле
/// настроек, и на то две причины. Первая: настройки правятся привязками, то
/// есть требуют уведомлений о каждом свойстве, а политика — значение и обязана
/// им остаться, иначе её нельзя сравнить и нельзя передать в пакет без страха,
/// что она поменяется под ним. Вторая: пакет не должен знать ни про WPF, ни про
/// то, где лежит файл настроек.
///
/// <b>Выключатель не заперт, даже когда значение приезжает из панели.</b> Это
/// решение оригинала, и оно объясняется там же: замок защищал ничего — правка
/// любого управляемого поля живёт ровно до следующей выкладки предустановки, а
/// окно «Управление» затем и существует, чтобы перенастроить место руками.
/// Сказать правду про судьбу правки при этом надо, и говорит её подпись под
/// переключателем.
/// </remarks>
public sealed class IncomingCallSettings : Observable
{
    private bool _isEnabled = true;
    private bool _isServerManaged;
    private bool _isRandomPositionEnabled = true;
    private bool _tunesRandomnessByHand;
    private double _minimumTravel = 150;
    private double _screenMargin = 24;
    private int _targetCount = 1;
    private bool _requiresCursorMovement = true;
    private bool _tunesLivenessByHand;
    private double _requiredCursorTravel = 40;
    private int _requiredCursorSamples = 3;
    private bool _rejectsSyntheticEvents;
    private string _autoAnswer = AutoAnswerModes.Off;
    private IReadOnlyList<string> _autoAnswerNumbers = [];
    private bool _masksMobileNumbers = true;

    // MARK: - Номера (с 0.1.73)

    /// <summary>
    /// Прятать ли мобильные номера звонящих: <c>+7</c> и звёздочки.
    /// </summary>
    ///
    /// <remarks>
    /// До 0.1.73 маска стояла у всех без исключения. Отделам, которые
    /// перезванивают клиенту сами, номер нужен открытым, — поэтому выключатель,
    /// и он же управляется предустановкой Spark (поле <c>masksMobileNumbers</c>).
    /// Умолчание — прятать: забытый выключатель не должен открыть лидов всем.
    /// Действует на окно входящего, шапку панели и историю разом — правило одно,
    /// см. <see cref="Incoming.IncomingCallSubject"/>.
    /// </remarks>
    public bool MasksMobileNumbers
    {
        get => _masksMobileNumbers;
        set => Set(ref _masksMobileNumbers, value);
    }

    // MARK: - Автоподъём (с 0.1.61, как на macOS 0.1.53)

    /// <summary>
    /// Режим автоподъёма строкой, как в предустановке: <c>off</c>,
    /// <c>always</c>, <c>header</c>, <c>list</c>.
    /// </summary>
    ///
    /// <remarks>
    /// Строкой, а не перечислением, ради одного правила: незнакомое значение
    /// читается как «выключено». Перечисление с конвертером JSON на незнакомом
    /// значении роняло бы чтение всего файла настроек.
    /// </remarks>
    public string AutoAnswer
    {
        get => _autoAnswer;
        set
        {
            Set(ref _autoAnswer, AutoAnswerModes.Normalize(value));
            foreach (var name in new[] { nameof(AutoAnswerOff), nameof(AutoAnswerAlways), nameof(AutoAnswerHeader), nameof(AutoAnswerList) })
            {
                NotifyChanged(name);
            }
        }
    }

    /// <summary>Номера для режима <c>list</c>.</summary>
    public IReadOnlyList<string> AutoAnswerNumbers
    {
        get => _autoAnswerNumbers;
        set
        {
            Set(ref _autoAnswerNumbers, value ?? []);
            NotifyChanged(nameof(AutoAnswerNumbersText));
        }
    }

    [JsonIgnore]
    public bool AutoAnswerOff
    {
        get => _autoAnswer == AutoAnswerModes.Off;
        set { if (value) { AutoAnswer = AutoAnswerModes.Off; } }
    }

    [JsonIgnore]
    public bool AutoAnswerAlways
    {
        get => _autoAnswer == AutoAnswerModes.Always;
        set { if (value) { AutoAnswer = AutoAnswerModes.Always; } }
    }

    [JsonIgnore]
    public bool AutoAnswerHeader
    {
        get => _autoAnswer == AutoAnswerModes.Header;
        set { if (value) { AutoAnswer = AutoAnswerModes.Header; } }
    }

    [JsonIgnore]
    public bool AutoAnswerList
    {
        get => _autoAnswer == AutoAnswerModes.List;
        set { if (value) { AutoAnswer = AutoAnswerModes.List; } }
    }

    /// <summary>Список номеров для поля ввода: по номеру в строке, запятые тоже годятся.</summary>
    [JsonIgnore]
    public string AutoAnswerNumbersText
    {
        get => string.Join(Environment.NewLine, _autoAnswerNumbers);
        set => AutoAnswerNumbers = (value ?? string.Empty)
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// Снимать ли трубку самому: режим, просьба в заголовках и номер звонящего.
    /// </summary>
    public bool ShouldAutoAnswer(bool asksForAutoAnswer, string callerNumber) => _autoAnswer switch
    {
        AutoAnswerModes.Always => true,
        AutoAnswerModes.Header => asksForAutoAnswer,
        AutoAnswerModes.List => _autoAnswerNumbers.Any(number => AutoAnswerModes.SameNumber(number, callerNumber)),
        _ => false,
    };

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            Set(ref _isEnabled, value);
            NotifyChanged(nameof(IsGuardOff));
        }
    }

    /// <summary>Приезжает ли значение из панели. Смысл получит на W10.</summary>
    public bool IsServerManaged
    {
        get => _isServerManaged;
        set => Set(ref _isServerManaged, value);
    }

    public bool IsRandomPositionEnabled
    {
        get => _isRandomPositionEnabled;
        set => Set(ref _isRandomPositionEnabled, value);
    }

    public bool TunesRandomnessByHand
    {
        get => _tunesRandomnessByHand;
        set
        {
            Set(ref _tunesRandomnessByHand, value);
            NotifyChanged(nameof(TunesRandomnessAutomatically));
            NotifyChanged(nameof(RandomnessSlidersAreEditable));
        }
    }

    public double MinimumTravel
    {
        get => _minimumTravel;
        set => Set(ref _minimumTravel, value);
    }

    public double ScreenMargin
    {
        get => _screenMargin;
        set => Set(ref _screenMargin, value);
    }

    public int TargetCount
    {
        get => _targetCount;
        set
        {
            Set(ref _targetCount, value);
            NotifyChanged(nameof(AsksForDigit));
        }
    }

    public bool RequiresCursorMovement
    {
        get => _requiresCursorMovement;
        set
        {
            Set(ref _requiresCursorMovement, value);
            NotifyChanged(nameof(LivenessSlidersAreEditable));
        }
    }

    public bool TunesLivenessByHand
    {
        get => _tunesLivenessByHand;
        set
        {
            Set(ref _tunesLivenessByHand, value);
            NotifyChanged(nameof(TunesLivenessAutomatically));
            NotifyChanged(nameof(LivenessSlidersAreEditable));
        }
    }

    public double RequiredCursorTravel
    {
        get => _requiredCursorTravel;
        set => Set(ref _requiredCursorTravel, value);
    }

    public int RequiredCursorSamples
    {
        get => _requiredCursorSamples;
        set => Set(ref _requiredCursorSamples, value);
    }

    public bool RejectsSyntheticEvents
    {
        get => _rejectsSyntheticEvents;
        set => Set(ref _rejectsSyntheticEvents, value);
    }

    /// <summary>Обратная сторона выключателя — для подписи про журнал.</summary>
    [JsonIgnore]
    public bool IsGuardOff => !_isEnabled;

    /// <summary>Пара «Авто ↔ Вручную» одним переключателем на каждый слой.</summary>
    ///
    /// <remarks>
    /// Отдельные свойства, а не отрицание в разметке: на «Авто» ползунки
    /// заперты, и запирает их именно это значение. Разметка, считающая
    /// отрицание сама, разошлась бы с ним при первой же правке.
    /// </remarks>
    [JsonIgnore]
    public bool TunesRandomnessAutomatically
    {
        get => !_tunesRandomnessByHand;
        set => TunesRandomnessByHand = !value;
    }

    [JsonIgnore]
    public bool TunesLivenessAutomatically
    {
        get => !_tunesLivenessByHand;
        set => TunesLivenessByHand = !value;
    }

    [JsonIgnore]
    public bool RandomnessSlidersAreEditable => _tunesRandomnessByHand;

    [JsonIgnore]
    public bool LivenessSlidersAreEditable => _tunesLivenessByHand && _requiresCursorMovement;

    /// <summary>Включено ли подтверждение цифрой.</summary>
    ///
    /// <remarks>
    /// Переключателем, а не полем «сколько целей»: у настройки два состояния —
    /// «одна кнопка» и «ряд цифр», — а число целей уточняется отдельно и только
    /// во втором. Тройка при включении — то же значение, что в оригинале.
    /// </remarks>
    [JsonIgnore]
    public bool AsksForDigit
    {
        get => _targetCount > 1;
        set => TargetCount = value ? 3 : 1;
    }

    /// <summary>Забирает значения у другого набора: черновик «Управления».</summary>
    ///
    /// <remarks>
    /// Правки администратора придержаны черновиком, а черновик — это отдельный
    /// такой же набор. Копирование по полям, а не подмена ссылки: настройки уже
    /// подписаны на запись, и подменённая ссылка увела бы запись мимо файла.
    /// </remarks>
    public void CopyFrom(IncomingCallSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        IsEnabled = other.IsEnabled;
        IsServerManaged = other.IsServerManaged;
        IsRandomPositionEnabled = other.IsRandomPositionEnabled;
        TunesRandomnessByHand = other.TunesRandomnessByHand;
        MinimumTravel = other.MinimumTravel;
        ScreenMargin = other.ScreenMargin;
        TargetCount = other.TargetCount;
        RequiresCursorMovement = other.RequiresCursorMovement;
        TunesLivenessByHand = other.TunesLivenessByHand;
        RequiredCursorTravel = other.RequiredCursorTravel;
        RequiredCursorSamples = other.RequiredCursorSamples;
        RejectsSyntheticEvents = other.RejectsSyntheticEvents;
        AutoAnswer = other.AutoAnswer;
        AutoAnswerNumbers = other.AutoAnswerNumbers;
        MasksMobileNumbers = other.MasksMobileNumbers;
    }

    /// <summary>То, что уходит в пакет.</summary>
    public CallGuardPolicy ToPolicy() => new()
    {
        IsEnabled = _isEnabled,
        IsServerManaged = _isServerManaged,
        IsRandomPositionEnabled = _isRandomPositionEnabled,
        TunesRandomnessByHand = _tunesRandomnessByHand,
        MinimumTravel = _minimumTravel,
        ScreenMargin = _screenMargin,
        TargetCount = _targetCount,
        RequiresCursorMovement = _requiresCursorMovement,
        TunesLivenessByHand = _tunesLivenessByHand,
        RequiredCursorTravel = _requiredCursorTravel,
        RequiredCursorSamples = _requiredCursorSamples,
        RejectsSyntheticEvents = _rejectsSyntheticEvents,
    };
}

/// <summary>Режимы автоподъёма и сравнение номеров для режима «по списку».</summary>
public static class AutoAnswerModes
{
    public const string Off = "off";
    public const string Always = "always";
    public const string Header = "header";
    public const string List = "list";

    /// <summary>Знакомый ли режим. Незнакомое из предустановки не применяется.</summary>
    public static bool IsKnown(string? mode) => mode?.Trim().ToLowerInvariant() is Off or Always or Header or List;

    /// <summary>Незнакомое — «выключено».</summary>
    public static string Normalize(string? mode) => IsKnown(mode) ? mode!.Trim().ToLowerInvariant() : Off;

    /// <summary>
    /// Один ли это номер: по цифрам, у длинных — по последним десяти.
    /// </summary>
    ///
    /// <remarks>
    /// «+7 (999) 123-45-67», «89991234567» и «9991234567» — один номер: у
    /// длинных сравниваются последние десять цифр, и код страны с восьмёркой
    /// не мешает. Короткие внутренние сравниваются целиком: «176» не совпадёт
    /// с «1176».
    /// </remarks>
    public static bool SameNumber(string? listed, string? caller)
    {
        var a = Digits(listed);
        var b = Digits(caller);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        if (a.Length >= 10 && b.Length >= 10)
        {
            return a[^10..] == b[^10..];
        }

        return a == b;
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
}