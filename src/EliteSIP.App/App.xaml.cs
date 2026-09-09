using System.Globalization;
using System.IO;
using System.Windows;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.CallHistory;
using EliteSIP.App.History;
using EliteSIP.App.Admin;
using EliteSIP.AdminAccess;
using EliteSIP.App.Shell;
using EliteSIP.App.FirstRun;

namespace EliteSIP.App;

// IDisposable у приложения — не церемония: `AppearanceService` подписан на
// статическое событие Windows о смене темы, и неотписанная подписка живёт
// дольше процесса, стреляя по закрытым окнам.
public partial class App : Application, IDisposable
{
    private AppearanceService? _appearance;
    private AppSettings? _settings;
    private SettingsWindow? _settingsWindow;
    private CallHistoryWindow? _historyWindow;
    private CallHistoryStore? _history;
    private PanelViewModel? _panel;
    private AdministrationWindow? _administrationWindow;
    private AdminAccessState? _access;
    private PanelWindow? _panelWindow;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Необработанное исключение в потоке интерфейса кладёт софтфон молча:
        // окно исчезает, в системном журнале остаётся только код 0xE0434352.
        // Оператор в этот момент видит, что телефон пропал, и звонить в
        // поддержку ему не с чего — поэтому падение обязано оставлять след.
        DispatcherUnhandledException += (_, failure) => Record(failure.Exception);

        // Порядок важен: настройки читаются до языка, язык — до палитры, палитра
        // — до первого окна. Иначе панель успевает нарисоваться английской и
        // светлой и перекрашивается на глазах.
        _settings = AppSettings.Load();
        _settings.AutoSave();

        Strings.Apply(Chosen(_settings.Appearance.Language));

        // Замок на «Управление» один на приложение и восстанавливается из
        // настроек: пароль там лежит солью с хэшем, а не текстом.
        _access = new AdminAccessState(_settings.Admin.ToCredential());

        _appearance = new AppearanceService(this) { Appearance = _settings.Appearance.Theme };
        _appearance.Apply();

        // Ненастроенная машина встречает мастер, а не панель: панель без
        // добавочного и адреса не зазвонит, а «Управление» на такой машине
        // обязано быть заперто раньше пароля.
        if (!_settings.Setup.IsCompleted || e.Args.Contains("--first-run"))
        {
            if (!RunFirstRun())
            {
                Shutdown();
                return;
            }
        }

        // Слоя приложения ещё нет: панель поднимается со своим состоянием, а
        // звонить ей пока нечем. Настоящая модель подпишется на те же команды.
        // История открывается сразу с настоящим хранилищем: оно готово с W7 и
        // ничего от слоя приложения не ждёт. Показательные записи, если их
        // просили, ложатся в отдельный файл — чужую историю ими не портим.
        _history = new CallHistoryStore(new CallHistoryStore.Settings(
            Path.Combine(
                Path.GetDirectoryName(AppSettings.DefaultPath)!,
                e.Args.Contains("--demo") ? "history-demo.db" : "history.db")));

        _panel = new PanelViewModel
        {
            ShowSettings = new RelayCommand(_ => ShowSettings()),
            ShowHistory = new RelayCommand(_ => ShowHistory()),
        };

        var model = _panel;

        SyncMacros();
        PanelDemo.Apply(model, _settings, e.Args);

        if (e.Args.Contains("--demo"))
        {
            HistoryDemo.Seed(_history, _settings.Account.ProfileId);
        }

        _panelWindow = new PanelWindow(model);
        _panelWindow.Show();

        // Значок в области уведомлений — второй вход к панели и единственный
        // выход из приложения при спрятанной панели.
        _tray = new TrayIcon(model, new TrayIcon.TrayActions(
            IsPanelVisible: () => _panelWindow?.IsVisible is true,
            TogglePanel: TogglePanel,
            ShowHistory: ShowHistory,
            ShowSettings: ShowSettings,
            ToggleOffline: () => model.IsOfflineByChoice = !model.IsOfflineByChoice,
            Quit: Quit));

        // Прятать панель за значок можно только если значок встал: иначе
        // приложение окажется запущенным, невидимым и без выхода.
        _panelWindow.AllowsClosing = !_tray.IsAdded;

        // Тот же ключ, что в оригинале: снимок окна настроек нужен для сверки
        // раскладки, а дотянуться до него скриптом иначе нечем — окно
        // открывается из панели, а панель до фокуса не доходит.
        if (e.Args.Contains("--open-history"))
        {
            ShowHistory();
        }

        if (e.Args.Contains("--open-admin"))
        {
            ShowAdministration();
        }

        var opened = Array.IndexOf(e.Args, "--open-settings");
        if (opened >= 0)
        {
            ShowSettings();

            // Разделом можно назвать нужный: снимок сверяют по разделам, а
            // щёлкать по списку скрипту нечем.
            if (opened + 1 < e.Args.Length
                && Enum.TryParse<SettingsSectionKind>(e.Args[opened + 1], ignoreCase: true, out var section))
            {
                _settingsWindow!.Model.Section = section;
            }
        }
    }

    /// <summary>Во что обращается выбор языка. <c>null</c> — «как в системе».</summary>
    private static CultureInfo? Chosen(LanguageSetting language) => language switch
    {
        LanguageSetting.Russian => CultureInfo.GetCultureInfo("ru"),

        // Английский задаётся инвариантной культурой, а не «en»: нейтральный
        // каталог ресурсов и есть английский, и просить «en» значило бы искать
        // каталог, которого нет, чтобы после отката прийти к тому же самому.
        LanguageSetting.English => CultureInfo.InvariantCulture,
        _ => null,
    };

    /// <summary>
    /// Открывает настройки — или поднимает уже открытые.
    /// </summary>
    ///
    /// <remarks>
    /// Второе окно настроек поверх первого — это две правды об одной настройке:
    /// в одном окне тумблер включён, в другом выключен, и запишется тот, по
    /// которому щёлкнули последним.
    /// </remarks>
    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(new SettingsViewModel(_settings!), _appearance!);
        _settingsWindow.AdministrationRequested += ShowAdministration;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>Спрашивает пароль и открывает «Управление».</summary>
    ///
    /// <remarks>
    /// Замок один на приложение (<c>AdminAccessState</c>), и открытым он
    /// остаётся до конца сеанса: администратор, закрывший окно, чтобы посмотреть
    /// панель, не должен вводить пароль второй раз. Запирает его выход.
    /// </remarks>
    private void ShowAdministration()
    {
        if (_administrationWindow is { IsLoaded: true })
        {
            _administrationWindow.Activate();
            return;
        }

        // Спрашивается один раз за сеанс — но спрашивается всегда, даже когда
        // пароля нет: на незащищённой машине окно не просит ничего, а
        // предупреждает. Открытые настройки не делают правку менее
        // последствийной, и знать об этом надо до, а не после.
        if (!_access!.IsUnlocked)
        {
            var unlock = new AdminUnlockWindow(_settings!, _access, _appearance!)
            {
                Owner = _settingsWindow,
            };

            if (unlock.ShowDialog() is not true)
            {
                return;
            }
        }

        _administrationWindow = new AdministrationWindow(
            new AdministrationViewModel(_settings!, _access),
            _appearance!);

        _administrationWindow.Closed += (_, _) =>
        {
            _administrationWindow = null;

            // Клавиши могли перемениться — панель обязана показать те, что
            // сохранили, а не те, с которыми её открыли.
            SyncMacros();
        };

        _administrationWindow.Show();

        // «Управление» заменяет собой настройки, а не встаёт рядом: два окна об
        // одной машине означали бы две правды о ней.
        _settingsWindow?.Close();
    }

    /// <summary>Проводит мастер. <c>false</c> — человек отказался.</summary>
    ///
    /// <remarks>
    /// Смена языка в мастере требует перезапуска по той же причине, что и в
    /// настройках: язык берётся при старте процесса, и поменять его у уже
    /// собранных окон нельзя. Перезапуск здесь не спрашивается — человек только
    /// что сам выбрал язык на первом же экране и увидит выбранное сразу.
    /// </remarks>
    private bool RunFirstRun()
    {
        var model = new FirstRunViewModel(_settings!, _access!);
        var window = new FirstRunWindow(model, _appearance!);

        if (window.ShowDialog() is not true)
        {
            return false;
        }

        if (model.LanguageChanges)
        {
            Restart();
            return false;
        }

        _appearance!.Appearance = _settings!.Appearance.Theme;
        return true;
    }

    /// <summary>Перезапуск: новый процесс поднимается, этот закрывается.</summary>
    private void Restart()
    {
        if (Environment.ProcessPath is { } executable)
        {
            System.Diagnostics.Process.Start(executable);
        }

        Shutdown();
    }

    /// <summary>Показывает панель или прячет её.</summary>
    private void TogglePanel()
    {
        if (_panelWindow is null)
        {
            return;
        }

        if (_panelWindow.IsVisible)
        {
            _panelWindow.Hide();
            return;
        }

        _panelWindow.Show();

        // Показанная из области уведомлений панель обязана оказаться сверху:
        // без этого она поднимается за тем окном, из которого её позвали, и
        // выглядит не открывшейся.
        _panelWindow.Activate();
    }

    /// <summary>Выход. Единственное место, откуда приложение завершают.</summary>
    private void Quit()
    {
        if (_panelWindow is not null)
        {
            _panelWindow.AllowsClosing = true;
        }

        Shutdown();
    }

    /// <summary>Переносит клавиши из настроек в панель.</summary>
    private void SyncMacros()
    {
        if (_panel is null || _settings is null)
        {
            return;
        }

        _panel.Macros.Clear();
        foreach (var macro in _settings.Dtmf.Macros)
        {
            _panel.Macros.Add(new MacroViewModel(macro.Title, macro.Sequence));
        }

        _panel.MacroColumns = _settings.Dtmf.MacroColumns;
        _panel.MacroHeight = _settings.Dtmf.MacroHeightIsManual
            ? _settings.Dtmf.MacroHeight
            : DtmfSettings.DefaultMacroHeight;
    }

    /// <summary>Открывает историю — или поднимает уже открытую.</summary>
    private void ShowHistory()
    {
        if (_historyWindow is { IsLoaded: true })
        {
            _historyWindow.Activate();
            return;
        }

        // Срез читается при создании окна, а не при каждом его показе: список,
        // перечитанный поверх уже разложенных строк, уводил бы прокрутку.
        var model = new CallHistoryViewModel(
            _history!,
            _settings!.Account.ProfileId,
            _settings.Account.Username,

            // Срок хранения спрашивается у настроек каждый раз, а не берётся
            // копией: администратор мог сменить его, пока окно было закрыто.
            () => _settings.History.MaximumAgeInDays);

        _historyWindow = new CallHistoryWindow(model, _appearance!);

        // Перезвонить — дело панели: там поле набора, там же и разрешение
        // звонить. История только говорит, какой номер выбрали.
        _historyWindow.RedialRequested += number => _panel!.DialedNumber = number;
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <summary>Кладёт падение рядом с настройками — там же, где журнал.</summary>
    private static void Record(Exception failure)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(AppSettings.DefaultPath)!;
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(directory, "crash.log"),
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
                    + Environment.NewLine + failure + Environment.NewLine + Environment.NewLine);
        }
        catch (System.IO.IOException)
        {
            // Записать не удалось — падать второй раз из-за этого незачем.
        }
    }

    public void Dispose()
    {
        _appearance?.Dispose();
        _appearance = null;

        // База закрывается явно: у хранилища свой поток, и незакрытое оно
        // держит файл после выхода — следующий запуск встречает занятый.
        _history?.Dispose();
        _history = null;

        _tray?.Dispose();
        _tray = null;
        GC.SuppressFinalize(this);
    }
}
