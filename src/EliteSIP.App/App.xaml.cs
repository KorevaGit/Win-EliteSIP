using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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
using EliteSIP.App.Incoming;
using EliteSIP.App.PanelLine;

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
    private PhoneService? _phone;
    private IncomingCallPresenter? _incoming;
    private PanelLineHost? _panelLine;
    private UpdateService? _updates;
    private SingleInstance? _instance;
    private NetworkWatch? _networkWatch;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Необработанное исключение в потоке интерфейса кладёт софтфон молча:
        // окно исчезает, в системном журнале остаётся только код 0xE0434352.
        // Оператор в этот момент видит, что телефон пропал, и звонить в
        // поддержку ему не с чего — поэтому падение обязано оставлять след.
        DispatcherUnhandledException += (_, failure) => Record(failure.Exception);

        // Падение фоновой работы — тоже падение, и до сих пор оно не оставляло
        // ни строки. Работ вида `_ = ЧтоТоAsync()` в приложении с десяток —
        // линия панели, отзыв, обновления, — и молча умершая линия выглядит не
        // ошибкой, а тем, что предустановки «почему-то перестали приезжать».
        AppDomain.CurrentDomain.UnhandledException += (_, failure) =>
        {
            if (failure.ExceptionObject is Exception error)
            {
                Record(error);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, failure) =>
        {
            Record(failure.Exception);

            // Иначе исключение доедет до финализатора и уронит процесс целиком:
            // софтфон не должен закрываться из-за того, что не ответил канал.
            failure.SetObserved();
        };

        // Вторая копия только будит первую и выходит: две копии пишут один файл
        // настроек и теряют правки администратора молча.
        _instance = SingleInstance.Claim();
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        _instance.Watch(() => Dispatcher.Invoke(ShowPanel));

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

        // Подписка до первого `Apply`: первое же окно должно открыться с
        // полосой заголовка нужного тона, а не перекраситься на глазах.
        SystemCaption.Watch(() => _appearance!.IsDark);
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

        // История: настоящее хранилище с W7. Показательные записи, если их
        // просили ключом, ложатся в отдельный файл — чужую историю ими не
        // портим.
        _history = new CallHistoryStore(new CallHistoryStore.Settings(
            Path.Combine(
                Path.GetDirectoryName(AppSettings.DefaultPath)!,
                e.Args.Contains("--demo") ? "history-demo.db" : "history.db")));

        _panel = new PanelViewModel
        {
            ShowSettings = new RelayCommand(_ => ShowSettings()),
            ShowHistory = new RelayCommand(_ => ShowHistory()),
        };

        // Окно входящего вызова вместе с защитой от автокликеров (W9). Мигает
        // при этом кнопка панели в панели задач — своей у карточки вызова нет.
        _incoming = new IncomingCallPresenter(
            Log,
            () => _panelWindow is null ? 0 : new WindowInteropHelper(_panelWindow).Handle);

        // Телефон: то, что связывает панель с сигнализацией, звуком и историей.
        _phone = new PhoneService(_settings, _panel, _history, _incoming, Dispatcher, Log);

        _panel.CallOrHangUp = new RelayCommand(async _ =>
        {
            if (_panel.IsInCall)
            {
                await _phone.HangUpAsync();
            }
            else
            {
                await _phone.PlaceCallAsync(_panel.DialedNumber);
            }
        });

        _panel.ToggleHold = new RelayCommand(async _ => await _phone.ToggleHoldAsync());
        _panel.ToggleMicrophone = new RelayCommand(_ => _phone.ToggleMicrophone());
        _panel.StartConference = new RelayCommand(async _ => await _phone.StartConferenceAsync());
        _panel.Transfer = new RelayCommand(async _ => await _phone.TransferAsync(_panel.TransferNumber));

        _panel.SendMacro = new RelayCommand(async parameter =>
        {
            if (parameter is MacroViewModel macro)
            {
                await _phone.SendMacroAsync(macro);
            }
        });

        // «Не беспокоить» снимает регистрацию до конца сеанса и в настройки не
        // пишется: «выключил в пятницу, в понедельник не понял, почему тихо» —
        // не то состояние, в котором софтфон должен встречать рабочий день.
        _panel.PropertyChanged += async (_, change) =>
        {
            if (change.PropertyName is nameof(PanelViewModel.IsOfflineByChoice))
            {
                if (_panel.IsOfflineByChoice)
                {
                    await _phone.DisconnectAsync();
                }
                else
                {
                    await _phone.ConnectAsync();
                }
            }
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

        // Линия панели (W10): предустановки раз в два часа, отзыв раз в
        // пятнадцать минут. Заводится после панели и телефона, потому что
        // применение приехавшего трогает и то, и другое.
        _panelLine = new PanelLineHost(
            _settings,
            _access,
            // Помеха ровно двух видов: разговор и открытое «Управление». Второе
            // потому, что правки там копятся в памяти и пишутся разом по
            // «Сохранить»: применить предустановку в этот момент значит либо
            // потерять её по «Отменить», либо затереть ею несохранённое.
            isBlocked: () => model.IsInCall || _administrationWindow is { IsLoaded: true },
            reset: ResetMachine,
            log: Log);

        _panelLine.Start();

        // Линия обновлений (W12). Будильник у неё общий с предустановками: канал
        // один, и два независимых срока на нём разошлись бы через полгода.
        _updates = new UpdateService(
            isBusy: () => model.IsInCall,
            announce: version => Log(version is null
                ? "обновление больше не ждёт"
                : $"обновление {version} ждёт решения оператора"),
            alsoCheckPresets: () => _ = _panelLine?.CheckAsync(),
            log: Log)
        {
            AskToInstall = AskToInstallUpdate,
            PrepareForRestart = PrepareForUpdateRestart,
        };

        _updates.Start();

        // Помеха ушла — доложить отложенное. Разговор кончился виден по той же
        // отметке, по которой линия его и ждала.
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(PanelViewModel.IsInCall) && !model.IsInCall)
            {
                _panelLine?.HostBecameIdle();
                _updates?.Offer();
            }
        };

        // Выключение Windows и выход из системы: снять регистрацию, пока нас
        // ещё не убили. Без этого сервер две минуты раздаёт вызовы на
        // выключенную машину.
        SessionEnding += (_, _) => LeaveTheAir(TimeSpan.FromSeconds(2));

        // Пробуждение и смена сети — то, чего у настольного телефона не бывает,
        // а у ноутбука бывает дважды в день.
        //
        // После сна сокет мёртв, а в `Contact` стоит вчерашний адрес; после
        // переезда с одной сети в другую открытый на шлюзе порт выдан адресу,
        // которого у нас больше нет. И то и другое лечится одним и тем же —
        // поднять связь заново, а стук переиграть.
        _networkWatch = new NetworkWatch(
            () => Dispatcher.BeginInvoke(() => Reconnect("сеть сменилась")),
            () => Dispatcher.BeginInvoke(() => Reconnect("машина проснулась")),
            Log);

        // Регистрация поднимается сама при запуске — как в оригинале
        // (автоподключение). Ждать нажатия оператора нельзя: софтфон, который
        // после включения машины молчит, пропускает первые звонки смены.
        _ = _phone.ConnectAsync();

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
    /// Замок один на приложение (<c>AdminAccessState</c>) и запирается обратно
    /// вместе с закрытием окна — смотри обработчик <c>Closed</c> ниже.
    ///
    /// Порт сперва держал его открытым до конца сеанса, рассудив, что
    /// администратор, закрывший окно ради взгляда на панель, не должен вводить
    /// пароль дважды. Это оказалось той же ошибкой, которую оригинал уже
    /// пережил и починил 17 августа 2026: закрывший окно администратор чаще
    /// всего не «сейчас вернётся», а ушёл — и оставил рабочее место открытым
    /// до конца смены. Пароль, который спрашивают один раз за смену, не
    /// защищает ничего.
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

        // Раздел «Поддержка» заводится только вместе с линией: без неё показывать
        // в нём нечего, а кнопки обещали бы связь, которой нет.
        var support = _panelLine is null
            ? null
            : new SupportViewModel(
                _settings!,
                _access,
                checkNow: () => _panelLine.CheckAsync(),
                isInCall: () => _panel?.IsInCall is true);

        if (support is not null)
        {
            // Ответ линии кнопке «Проверить настройки сейчас» ходит через окно, а
            // не через настройки: «канал ответил 404» — это не состояние машины,
            // а ответ на нажатие, и жить он должен ровно столько, сколько открыто
            // окно.
            _panelLine!.Report = support.ReportFromLine;
        }

        var administration = new AdministrationViewModel(_settings!, _access, PreviewIncomingCall)
        {
            Support = support,
            Updates = _updates is null ? null : new UpdatesViewModel(_updates),
            // Отчёт последнего вызова — то немногое в «Управлении», что не
            // настройка, а факт: по нему видно, сработала ли защита на живом
            // звонке, не открывая журнал.
            LastGuardReport = _incoming?.LastReport?.Summary(),
        };

        _administrationWindow = new AdministrationWindow(administration, _appearance!);

        _administrationWindow.Closed += (_, _) =>
        {
            _administrationWindow = null;

            // Ответ линии больше некому показывать; заодно снимается ссылка на
            // закрытое окно.
            if (_panelLine is not null)
            {
                _panelLine.Report = null;
            }

            // «Управление» закрылось — помеха ушла, и отложенная предустановка
            // может лечь. Ждала она именно этого.
            _panelLine?.HostBecameIdle();

            // Замок защёлкивается здесь, а не при выходе из приложения.
            //
            // Здесь — единственное место, куда приходят все способы закрыть
            // окно: крестик, «Сохранить», «Отменить», Alt+F4 и закрытие вместе
            // с приложением. Именно на этом оригинал однажды и споткнулся:
            // гасил режим обработчик крестика, а кнопки шли мимо него, и
            // «Управление», закрытое «Сохранить», второй раз открывалось без
            // пароля.
            _access!.Lock();

            // Клавиши могли перемениться — панель обязана показать те, что
            // сохранили, а не те, с которыми её открыли.
            SyncMacros();
        };

        _administrationWindow.Show();

        // «Управление» заменяет собой настройки, а не встаёт рядом: два окна об
        // одной машине означали бы две правды о ней.
        _settingsWindow?.Close();
    }

    /// <summary>Показывает окно входящего для проверки из «Управления».</summary>
    ///
    /// <param name="asDealCall">
    /// <c>true</c> — вызов по сделке, <c>false</c> — раздача.
    /// </param>
    ///
    /// <remarks>
    /// Тем же путём, что и боевой вызов: разбор случая, политика из настроек,
    /// то же окно. Иначе проверка показывала бы не то, что увидит оператор на
    /// живом вызове, — а ради этого её и открывают.
    ///
    /// Номер раздачи — боевой формы, а не выдуманный: добавочный колл-центра и
    /// просьба автоответа. Кнопки при этом не делают ничего: проверка кончается
    /// закрытием окна, а не звонком.
    /// </remarks>
    private void PreviewIncomingCall(bool asDealCall)
    {
        if (_incoming is null || _settings is null)
        {
            return;
        }

        var own = _settings.Account.Username;
        var subject = asDealCall
            ? IncomingCallSubject.Classify(own, callerName: null, requestsAutoAnswer: false, ownNumber: own)
            : IncomingCallSubject.Classify("712", "Call_Center", requestsAutoAnswer: true, ownNumber: own);

        _incoming.Show(
            subject,
            _settings.IncomingCall.ToPolicy(),
            onAnswer: () => { },
            onDecline: () => { });
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
    /// <summary>
    /// Поднять связь заново после сна или переезда в другую сеть.
    /// </summary>
    ///
    /// <remarks>
    /// Молчащий софтфон и софтфон, который «сейчас переподключится», выглядят
    /// одинаково, поэтому повод пишется в журнал: разбирающему потом важно
    /// отличить переезд от отказа сервера.
    ///
    /// Сознательно ничего не делаем в разговоре: пересборка транспорта кладёт
    /// трубку, а разговор по живому сокету переживает и смену адреса — RTP уже
    /// идёт, и рвать его из-за события системы нельзя. Регистрация подтянется
    /// после того, как трубку положат.
    /// </remarks>
    private void Reconnect(string reason)
    {
        if (_phone is null || _panel is null)
        {
            return;
        }

        if (_panel.IsInCall)
        {
            Log($"{reason}: связь поднимется после разговора");
            return;
        }

        if (_panel.IsOfflineByChoice)
        {
            // «Не беспокоить» — это выбор человека, и событие системы его не
            // отменяет.
            return;
        }

        Log($"{reason}: поднимаем регистрацию заново");
        _ = _phone.ConnectAsync();
    }

    /// <summary>Показать панель и поднять её наверх.</summary>
    ///
    /// <remarks>
    /// Зовётся, когда по ярлыку щёлкнули второй раз: человек хотел открыть
    /// софтфон, и открыть ему надо именно панель, а не ещё одну копию
    /// приложения. Свёрнутое окно при этом разворачивается — иначе «открылось»
    /// означало бы мигание кнопки в панели задач.
    /// </remarks>
    private void ShowPanel()
    {
        if (_panelWindow is null)
        {
            return;
        }

        if (!_panelWindow.IsVisible)
        {
            _panelWindow.Show();
        }

        if (_panelWindow.WindowState is WindowState.Minimized)
        {
            _panelWindow.WindowState = WindowState.Normal;
        }

        _panelWindow.Activate();
    }

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

    /// <summary>
    /// Предложение обновиться: две кнопки и ничего больше.
    /// </summary>
    ///
    /// <remarks>
    /// Кнопки «Пропустить эту версию» здесь нет и не будет — это решение
    /// оригинала, перенесённое как есть. Отложить можно сколько угодно раз, а
    /// пропустить версию нельзя вовсе: рабочее место, оставшееся на старой
    /// сборке навсегда, — это то, ради чего линия обновлений и заведена.
    ///
    /// Файл к этому моменту уже скачан и проверен, поэтому «Обновить» выглядит
    /// мгновенным: оно ничего не начинает, а завершает.
    /// </remarks>
    private bool AskToInstallUpdate(Version version)
    {
        var answer = Theme.Dialog.Ask(
            _panelWindow,
            Strings.Format("UpdateOfferTitle", version.ToString(3)),
            Strings.Get("UpdateOfferBody"),
            confirmTitle: Strings.Get("UpdateOfferInstall"));

        return answer is DialogAnswer.Confirm;
    }

    /// <summary>
    /// Уйти с дороги установщика.
    /// </summary>
    ///
    /// <remarks>
    /// Тем же выходом, что и по кнопке: снятие регистрации, предел ожидания и
    /// закрытие — всё это одинаково нужно и уходящему на обед, и уходящему под
    /// установщик. Своя копия этих трёх шагов разошлась бы с общей на первой же
    /// правке — а разница здесь означает регистрацию, оставленную на сервере.
    /// </remarks>
    private void PrepareForUpdateRestart() => Quit();

    /// <summary>
    /// Полная чистка машины по подписанному отзыву.
    /// </summary>
    ///
    /// <remarks>
    /// Порядок здесь значим целиком, и каждый шаг стоит там, где стоит:
    ///
    /// <list type="number">
    ///   <item><description>снимаем регистрацию и закрываем базу истории —
    ///   иначе стирать придётся занятые файлы;</description></item>
    ///   <item><description>стираем файлы;</description></item>
    ///   <item><description>запускаем себя заново и выходим.</description></item>
    /// </list>
    ///
    /// Перезапуск, а не просто выход: машина обязана вернуться в состояние сразу
    /// после установки, а это состояние встречает человека мастером. Оставшееся
    /// закрытым приложение выглядело бы как падение, и первое, что сделал бы
    /// сотрудник, — запустил бы его снова, ничего не поняв.
    ///
    /// Чистка идёт <b>после</b> того, как всё закрылось, ещё и потому, что
    /// настройки пишутся на каждую правку: стёртый до отключения телефона файл
    /// успел бы возродиться от первого же изменения состояния линии.
    /// </remarks>
    private void ResetMachine()
    {
        _updates?.Dispose();
        _updates = null;

        _panelLine?.Dispose();
        _panelLine = null;

        _phone?.Dispose();
        _phone = null;

        _history?.Dispose();
        _history = null;

        var failures = MachineReset.Wipe();
        if (failures.Count > 0)
        {
            // Записать это в журнал нельзя — его только что стёрли, и он же в
            // списке несдавшихся. Остаётся системный журнал: он переживает
            // чистку по построению.
            Record(new IOException(
                "чистка по отзыву не убрала: " + string.Join(", ", failures)));
        }

        if (Environment.ProcessPath is { } executable)
        {
            using var restarted = System.Diagnostics.Process.Start(executable);
        }

        Quit();
    }

    /// <summary>Выход. Единственное место, откуда приложение завершают.</summary>
    private void Quit()
    {
        LeaveTheAir(TimeSpan.FromSeconds(2));

        if (_panelWindow is not null)
        {
            _panelWindow.AllowsClosing = true;
        }

        Shutdown();
    }

    /// <summary>
    /// Снять регистрацию перед уходом — и не ждать этого вечно.
    /// </summary>
    ///
    /// <remarks>
    /// <b>Зачем вообще ждать.</b> Брошенная регистрация живёт на сервере до
    /// конца своего срока — по умолчанию две минуты. Всё это время Asterisk
    /// считает рабочее место на связи и раздаёт ему вызовы, которые никто не
    /// снимет: для конторы, где вызовы — это лиды, конец смены превращается в
    /// две минуты потерянных звонков.
    ///
    /// <b>Почему нельзя просто <c>Wait()</c>.</b> Снятие регистрации
    /// возвращается в поток интерфейса (<c>ConfigureAwait(true)</c>), а
    /// <c>Wait()</c> этот самый поток и блокирует — получилось бы взаимное
    /// ожидание, которое разрешается только по сроку. Поэтому очередь
    /// диспетчера продолжает работать: <see cref="DispatcherFrame"/> крутится,
    /// пока задача не кончится или не выйдет время.
    ///
    /// <b>Срок обязателен.</b> Сюда приходят с выключением Windows, где система
    /// ждёт нас считанные секунды и убивает не дождавшись. Лучше уйти без
    /// снятия, чем быть убитым посреди него.
    /// </remarks>
    private void LeaveTheAir(TimeSpan limit)
    {
        if (_phone is null)
        {
            return;
        }

        var leaving = _phone.DisconnectAsync();
        if (leaving.IsCompleted)
        {
            return;
        }

        DispatcherFrame frame = new();

        _ = leaving.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());

        DispatcherTimer deadline = new(limit, DispatcherPriority.Send, (_, _) => frame.Continue = false, Dispatcher);
        deadline.Start();

        Dispatcher.PushFrame(frame);
        deadline.Stop();
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

    /// <summary>Пишет строку в журнал рядом с настройками.</summary>
    ///
    /// <remarks>
    /// Журнал технический и не переводится: его сравнивают между машинами и
    /// прикладывают к обращению в поддержку. Полноценная ротация с маскированием
    /// секретов лежит в `EliteSIP.Diagnostics` с W1 и подключается вместе с
    /// разделом «Обслуживание»; пока сюда пишется то, без чего разбирать звонок
    /// нечем.
    /// </remarks>
    private void Log(string message)
    {
        if (_settings?.Maintenance.LogToFile is not true)
        {
            return;
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(AppSettings.DefaultPath)!;
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(directory, "elitesip.log"),
                DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " " + message + Environment.NewLine);
        }
        catch (System.IO.IOException)
        {
            // Журнал не пишется — звонить это не мешает.
        }
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

        _networkWatch?.Dispose();
        _networkWatch = null;

        _instance?.Dispose();
        _instance = null;

        // База закрывается явно: у хранилища свой поток, и незакрытое оно
        // держит файл после выхода — следующий запуск встречает занятый.
        _history?.Dispose();
        _history = null;

        _tray?.Dispose();
        _tray = null;

        _panelLine?.Dispose();
        _panelLine = null;

        _phone?.Dispose();
        _incoming?.Dispose();
        _phone = null;
        GC.SuppressFinalize(this);
    }
}
