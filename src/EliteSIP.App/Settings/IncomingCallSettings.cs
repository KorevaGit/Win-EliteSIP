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
