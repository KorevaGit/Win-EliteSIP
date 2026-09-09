using System.Collections.ObjectModel;
using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Admin;

/// <summary>Разделы «Управления».</summary>
///
/// <remarks>
/// Здесь пока четыре из девяти: те, у которых уже есть что править. Аккаунт,
/// АТС, входящие, очереди и обслуживание приедут вместе со своими настройками —
/// пустой раздел в списке хуже отсутствующего: он обещает то, чего за ним нет.
/// </remarks>
public enum AdminSectionKind
{
    Macros,
    History,
    Access,
    Diagnostics,
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
    private string _newPassword = string.Empty;
    private string _repeatedPassword = string.Empty;
    private bool _isDirty;

    public AdministrationViewModel(AppSettings settings, AdminAccessState access)
    {
        _settings = settings;
        _access = access;

        Sections =
        [
            new(AdminSectionKind.Macros, Strings.Get("AdminSectionMacros"), "square.grid.3x3", null),
            new(AdminSectionKind.History, Strings.Get("AdminSectionHistory"), "clock", Strings.Get("AdminGroupMachine")),
            new(AdminSectionKind.Access, Strings.Get("AdminSectionAccess"), "lock.shield.fill", null),
            new(AdminSectionKind.Diagnostics, Strings.Get("AdminSectionDiagnostics"), "stethoscope", null),
        ];

        Revert();
    }

    public IReadOnlyList<AdminSectionItem> Sections { get; }

    public AdminSectionKind Section
    {
        get => _section;
        set
        {
            Set(ref _section, value);
            foreach (var name in new[]
            {
                nameof(ShowsMacros), nameof(ShowsHistory), nameof(ShowsAccess), nameof(ShowsDiagnostics),
            })
            {
                NotifyChanged(name);
            }
        }
    }

    public bool ShowsMacros => _section is AdminSectionKind.Macros;

    public bool ShowsHistory => _section is AdminSectionKind.History;

    public bool ShowsAccess => _section is AdminSectionKind.Access;

    public bool ShowsDiagnostics => _section is AdminSectionKind.Diagnostics;

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

        _macroColumns = _settings.Dtmf.MacroColumns;
        _macroHeight = _settings.Dtmf.MacroHeight;
        _macroHeightIsManual = _settings.Dtmf.MacroHeightIsManual;
        _historyIsEnabled = _settings.History.IsEnabled;
        _historyAgeInDays = _settings.History.MaximumAgeInDays;

        foreach (var name in new[]
        {
            nameof(MacroColumns), nameof(MacroHeight), nameof(MacroHeightIsManual),
            nameof(HistoryIsEnabled), nameof(HistoryAgeInDays),
        })
        {
            NotifyChanged(name);
        }

        IsDirty = false;
    }

    private void MarkDirty() => IsDirty = true;
}
