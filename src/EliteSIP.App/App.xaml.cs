using System.Globalization;
using System.IO;
using System.Windows;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.CallHistory;
using EliteSIP.App.History;

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

        _appearance = new AppearanceService(this) { Appearance = _settings.Appearance.Theme };
        _appearance.Apply();

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

        PanelDemo.Apply(model, _settings, e.Args);

        if (e.Args.Contains("--demo"))
        {
            HistoryDemo.Seed(_history, _settings.Account.ProfileId);
        }

        new PanelWindow(model).Show();

        // Тот же ключ, что в оригинале: снимок окна настроек нужен для сверки
        // раскладки, а дотянуться до него скриптом иначе нечем — окно
        // открывается из панели, а панель до фокуса не доходит.
        if (e.Args.Contains("--open-history"))
        {
            ShowHistory();
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
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
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
            _settings.Account.Username);

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
        GC.SuppressFinalize(this);
    }
}
