using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;

namespace EliteSIP.App.Settings;

/// <summary>Клавиша DTMF-макроса в настройках.</summary>
///
/// <remarks>
/// Подпись и запись набора задаёт администратор, и переводу они не подлежат:
/// «Бухгалтерия» и <c>*4</c> — это его слова и коды его АТС.
/// </remarks>
public sealed class MacroSetting : Observable
{
    private string _title = string.Empty;
    private string _sequence = string.Empty;
    private bool _transfersCall;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Подпись на кнопке. Коротко: панель узкая.</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>Запись набора: цифры и запятые.</summary>
    public string Sequence
    {
        get => _sequence;
        set => Set(ref _sequence, value);
    }

    /// <summary>Уводит ли этот макрос звонок к другому человеку.</summary>
    ///
    /// <remarks>
    /// <b>Признаком, а не догадкой по коду.</b> Соблазн опознавать перевод по
    /// началу последовательности («<c>*02</c> — значит перевод») ложный:
    /// <c>*02</c> — это перевод боевого сервера заказчика, а не общее правило
    /// Asterisk. У другой установки там <c>#</c>, <c>*2</c> или своя запись в
    /// <c>[applicationmap]</c>, и зашитый префикс начал бы помечать переводом
    /// что попало — а пометка идёт в историю, которую читают как свидетельство
    /// при разборе жалобы.
    ///
    /// Поэтому отвечает администратор: он и так вписывает сюда коды под свою
    /// АТС, и он один знает, что они делают. Умолчание — «нет»: молчание не
    /// должно превращаться в утверждение о звонке.
    /// </remarks>
    public bool TransfersCall
    {
        get => _transfersCall;
        set => Set(ref _transfersCall, value);
    }
}

/// <summary>Клавиши макросов и то, как они разложены.</summary>
public sealed class DtmfSettings : Observable
{
    /// <summary>Ниже этого клавиша перестаёт быть мишенью для мыши.</summary>
    public const int MinimumMacroHeight = 44;

    /// <summary>Выше этого панели некуда расти: она стоит поверх CRM.</summary>
    public const int MaximumMacroHeight = 96;

    public const int DefaultMacroHeight = 58;

    private int _macroColumns = 3;
    private int _macroHeight = DefaultMacroHeight;
    private bool _macroHeightIsManual;

    public ObservableCollection<MacroSetting> Macros { get; init; } = [];

    /// <summary>Сколько клавиш в ряду.</summary>
    ///
    /// <remarks>
    /// Было константой темы — три, — и константа верна для коротких подписей
    /// вроде «Юрист» и неверна для названий отдела: живой прогон показал, что
    /// три в ряд ужимают их до нечитаемого.
    /// </remarks>
    public int MacroColumns
    {
        get => _macroColumns;
        set => Set(ref _macroColumns, Math.Clamp(value, 1, 4));
    }

    /// <summary>Высота клавиши. Читается только при <see cref="MacroHeightIsManual"/>.</summary>
    public int MacroHeight
    {
        get => _macroHeight;
        set => Set(ref _macroHeight, Math.Clamp(value, MinimumMacroHeight, MaximumMacroHeight));
    }

    /// <summary>Высоту задал человек, а не подбор по самой длинной подписи.</summary>
    public bool MacroHeightIsManual
    {
        get => _macroHeightIsManual;
        set => Set(ref _macroHeightIsManual, value);
    }
}

/// <summary>История: вести ли и сколько хранить.</summary>
public sealed class HistorySettings : Observable
{
    private bool _isEnabled = true;
    private int _maximumAgeInDays = 30;

    /// <summary>Вести ли историю вообще.</summary>
    ///
    /// <remarks>
    /// Выключатель административный, а не менеджерский: история — свидетельство
    /// при разборе жалобы, и решать, вести ли её, тому, чьи звонки в ней
    /// записаны, нельзя.
    /// </remarks>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    /// <summary>Через сколько дней запись уходит.</summary>
    public int MaximumAgeInDays
    {
        get => _maximumAgeInDays;
        set => Set(ref _maximumAgeInDays, Math.Clamp(value, 1, 365));
    }
}

/// <summary>Административный доступ: пароль и то, чем он проверяется.</summary>
///
/// <remarks>
/// В настройках лежит не пароль, а соль и хэш — то же, что в оригинале.
/// Проверку делает <c>AdminAccess</c>, здесь только хранение.
///
/// Кода восстановления нет и не будет: он лежал в бандле открытым текстом, то
/// есть «восстановление» работало у всякого, кто вскрыл приложение. Забывший
/// пароль администратор смотрит его в панели, откуда пароль и приезжает.
/// </remarks>
public sealed class AdminSettings : Observable
{
    private string? _salt;
    private string? _hash;
    private int _iterations;

    /// <summary>Соль, base64. <c>null</c> — пароля нет.</summary>
    public string? Salt
    {
        get => _salt;
        set
        {
            Set(ref _salt, value);
            NotifyChanged(nameof(IsProtected));
        }
    }

    /// <summary>Хэш пароля, base64.</summary>
    public string? Hash
    {
        get => _hash;
        set => Set(ref _hash, value);
    }

    public int Iterations
    {
        get => _iterations;
        set => Set(ref _iterations, value);
    }

    [JsonIgnore]
    public bool IsProtected => _salt is not null && _hash is not null;

    /// <summary>Собирает проверяльщика из того, что лежит в файле.</summary>
    public AdminCredential? ToCredential()
        => IsProtected
            ? new AdminCredential(_iterations, Convert.FromBase64String(_salt!), Convert.FromBase64String(_hash!))
            : null;

    /// <summary>Кладёт проверяльщика в файл. <c>null</c> снимает пароль.</summary>
    public void From(AdminCredential? credential)
    {
        if (credential is null)
        {
            Hash = null;
            Iterations = 0;
            Salt = null;
            return;
        }

        Hash = Convert.ToBase64String(credential.LoginHash);
        Iterations = credential.Iterations;

        // Соль последней: она же признак «пароль есть», и до неё остальные поля
        // должны быть уже на месте — иначе окно, слушающее IsProtected, успеет
        // спросить хэш, которого ещё нет.
        Salt = Convert.ToBase64String(credential.LoginSalt);
    }
}
