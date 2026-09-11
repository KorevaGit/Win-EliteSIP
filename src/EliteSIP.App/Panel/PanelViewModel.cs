using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace EliteSIP.App.Panel;

/// <summary>Состояние регистрации — то, что показывает точка в капсуле профиля.</summary>
public enum RegistrationState
{
    Idle,
    Registering,
    Registered,
    Failed,
}

/// <summary>Беда, о которой панель говорит одной строкой.</summary>
///
/// <param name="Text">Короткая причина — и это всё, что говорит панель.</param>
/// <param name="Glyph">Значок слева или <c>null</c>.</param>
/// <param name="IsFailure">Красным или второстепенным цветом.</param>
/// <param name="OpensSettings">Чинит человек — значит, надпись ведёт туда, где чинят.</param>
///
/// <remarks>
/// Подробности — время повтора, код ответа — живут в журнале, где записаны
/// подробнее. Вторая правда в двух местах хуже одной.
/// </remarks>
public sealed record Trouble(string Text, string? Glyph, bool IsFailure, bool OpensSettings);

/// <summary>Клавиша DTMF-макроса.</summary>
///
/// <remarks>
/// Подпись задаёт администратор и не переводится — как и в оригинале: перевод
/// сделал бы название отдела двумя разными строками.
/// </remarks>
public sealed class MacroViewModel(string title, string tones) : Observable
{
    private bool _isBusy;

    public string Title { get; } = title;

    public string Tones { get; } = tones;

    /// <summary>Пока команда идёт в RTP, клавиша молчит.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }
}

/// <summary>Одна линия: собеседник, состояние, таймер.</summary>
public sealed class CallLineViewModel : Observable
{
    private string _title = string.Empty;
    private string _status = string.Empty;
    private bool _isActive;
    private bool _isOnHold;
    private DateTimeOffset? _connectedAt;

    /// <summary>Про что вызов, а не откуда он пришёл.</summary>
    ///
    /// <remarks>
    /// Разбор вызова решает, что здесь стоит: на раздаче из очереди мобильный
    /// клиента скрыт, и панель не имеет права показать его крупно после того,
    /// как окно входящего его спрятало.
    /// </remarks>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>Номер мелкой строкой — или пусто, если он повторил бы имя.</summary>
    public string? SecondaryNumber { get; set; }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public bool IsOnHold
    {
        get => _isOnHold;
        set => Set(ref _isOnHold, value);
    }

    /// <summary>С какого мгновения идёт разговор. <c>null</c> — ещё не отвечено.</summary>
    public DateTimeOffset? ConnectedAt
    {
        get => _connectedAt;
        set
        {
            Set(ref _connectedAt, value);
            NotifyChanged(nameof(Duration));
        }
    }

    /// <summary>Длительность разговора. Пересчитывается тактом панели.</summary>
    public string? Duration
    {
        get
        {
            if (_connectedAt is not { } start)
            {
                return null;
            }

            var seconds = Math.Max((int)(DateTimeOffset.Now - start).TotalSeconds, 0);
            return $"{seconds / 60:00}:{seconds % 60:00}";
        }
    }

    /// <summary>Толкает таймер. Зовётся раз в секунду, пока панель на экране.</summary>
    public void Tick() => NotifyChanged(nameof(Duration));
}

/// <summary>
/// Состояние панели софтфона: всё, что показывает её середина, и всё, что
/// оператор может из неё сделать.
/// </summary>
///
/// <remarks>
/// Отдельный слой, а не свойства окна, по той же причине, по которой в
/// оригинале был <c>AppModel</c>: панель — не единственное место, откуда
/// звонят. Те же действия вызывает значок в области уведомлений и окно
/// входящего, и повторять условия «можно ли сейчас перевести» в трёх местах
/// значит рано или поздно разойтись.
///
/// Здесь только состояние и разрешения. Сам звонок, регистрация и тоны придут
/// со слоем приложения — он подпишется на команды и будет заполнять это
/// состояние. Разрешения (<c>Can…</c>) объявлены тут, потому что это правила
/// панели, а не протокола: «переводить нечего, пока нет разговора» — вопрос
/// интерфейса.
/// </remarks>
public sealed class PanelViewModel : Observable
{
    private RegistrationState _registration = RegistrationState.Idle;
    private bool _isOfflineByChoice;
    private string _statusTitle = "—";
    private string? _statusLabel;
    private Trouble? _trouble;
    private string _dialedNumber = string.Empty;
    private CallLineViewModel? _activeLine;
    private string _callStatus = string.Empty;
    private bool _isOnHold;
    private bool _isMicrophoneMuted;
    private bool _isConferenceStarted;
    private bool _isTransferEntryVisible;
    private bool _isTransferring;
    private string _transferNumber = string.Empty;
    private bool _canPlaceCall;
    private bool _canSendDtmf;
    private int _macroColumns = Theme.Metrics.MacroColumns;
    private double _macroHeight = Theme.Metrics.MacroMinHeight;
    private RelayCommand? _goOnline;
    private RelayCommand? _goOffline;

    public PanelViewModel()
    {
        Lines.CollectionChanged += (_, _) =>
        {
            NotifyChanged(nameof(HasSecondLine));
            NotifyCallControls();
        };

        Macros.CollectionChanged += (_, _) => NotifyChanged(nameof(HasMacros));
    }

    // --- Строка состояния -------------------------------------------------

    public RegistrationState Registration
    {
        get => _registration;
        set
        {
            Set(ref _registration, value);
            NotifyChanged(nameof(CanOpenProfileMenu));
        }
    }

    /// <summary>Отключён решением оператора — до конца сеанса и не в настройки.</summary>
    ///
    /// <remarks>
    /// «Выключил в пятницу, в понедельник не понял, почему тихо» — не то
    /// состояние, в котором софтфон должен встречать рабочий день.
    /// </remarks>
    public bool IsOfflineByChoice
    {
        get => _isOfflineByChoice;
        set
        {
            Set(ref _isOfflineByChoice, value);
            NotifyChanged(nameof(IsOnline));
        }
    }

    /// <summary>Номер в капсуле профиля.</summary>
    public string StatusTitle
    {
        get => _statusTitle;
        set
        {
            Set(ref _statusTitle, value);
            NotifyChanged(nameof(ProfileMenuTitle));
        }
    }

    /// <summary>Пометка профиля. Ужимается первой: её оператор и так знает.</summary>
    public string? StatusLabel
    {
        get => _statusLabel;
        set
        {
            Set(ref _statusLabel, value);
            NotifyChanged(nameof(ProfileMenuTitle));
        }
    }

    public Trouble? Trouble
    {
        get => _trouble;
        set => Set(ref _trouble, value);
    }

    /// <summary>В разговоре капсула не нажимается: смена профиля снимает регистрацию.</summary>
    public bool CanOpenProfileMenu => !IsInCall;

    /// <summary>Строка профиля в меню: номер и пометка вместе.</summary>
    ///
    /// <remarks>
    /// Одной строкой, а не двумя: пометка — единственное, чем два профиля
    /// одного добавочного различаются, и в списке она обязана стоять рядом с
    /// номером, а не под ним.
    /// </remarks>
    public string ProfileMenuTitle => string.IsNullOrEmpty(StatusLabel)
        ? StatusTitle
        : StatusTitle + " · " + StatusLabel;

    /// <summary>На линии ли профиль. Обратное <see cref="IsOfflineByChoice"/>.</summary>
    ///
    /// <remarks>
    /// Заведено ради галочки в меню профиля: там два пункта, «на линии» и
    /// «отключён», и второй уже есть свойством. Отрицание в разметке WPF не
    /// пишется, а заводить ради него преобразователь — больше кода, чем эта
    /// строка.
    /// </remarks>
    public bool IsOnline => !IsOfflineByChoice;

    /// <summary>Вернуться на линию. Пункт меню профиля.</summary>
    ///
    /// <remarks>
    /// Команды здесь, а не в слое приложения, потому что делать им нечего
    /// сверх того, что уже написано: снятие и подъём регистрации подвешены к
    /// <see cref="IsOfflineByChoice"/>, и слову «отключись» достаточно этого
    /// свойства.
    /// </remarks>
    public ICommand GoOnline => _goOnline ??= new RelayCommand(_ => IsOfflineByChoice = false);

    /// <summary>Уйти с линии до конца сеанса.</summary>
    public ICommand GoOffline => _goOffline ??= new RelayCommand(_ => IsOfflineByChoice = true);

    // --- Шапка ------------------------------------------------------------

    public string DialedNumber
    {
        get => _dialedNumber;
        set
        {
            Set(ref _dialedNumber, value);
            NotifyChanged(nameof(HasDialedNumber));
            NotifyChanged(nameof(IsCallButtonEnabled));
        }
    }

    public bool HasDialedNumber => !string.IsNullOrWhiteSpace(_dialedNumber);

    public ObservableCollection<CallLineViewModel> Lines { get; } = [];

    /// <summary>Линия, которая звучит. Остальные ждут на удержании.</summary>
    public CallLineViewModel? ActiveLine
    {
        get => _activeLine;
        set
        {
            Set(ref _activeLine, value);
            NotifyCallControls();
            NotifyChanged(nameof(CallTitle));
        }
    }

    /// <summary>Линий больше одной — шапка делится на два поля в том же слоте.</summary>
    public bool HasSecondLine => Lines.Count > 1;

    /// <summary>
    /// Исходящий вызов, на который ещё не ответили. <c>null</c> — такого нет.
    /// </summary>
    ///
    /// <remarks>
    /// До 10 сентября 2026 панель не знала о звонке до самого «ответили»:
    /// линия заводится по <c>200 OK</c>, а <see cref="IsInCall"/> считался по
    /// линиям. Пока шли гудки, панель выглядела свободной — без набранного
    /// номера, без «Завершить», и «Позвонить» нажималась ещё раз, заводя второй
    /// вызов поверх первого.
    ///
    /// Слой SIP при этом всё сообщал: у исходящего вызова есть событие
    /// состояния с гудками, и оно попадало в ветку <c>default</c>.
    /// </remarks>
    public string? PendingNumber
    {
        get => _pendingNumber;
        set
        {
            Set(ref _pendingNumber, value);
            NotifyCallControls();
            NotifyChanged(nameof(CallTitle));
        }
    }

    private string? _pendingNumber;

    /// <summary>Что показывать в шапке: собеседник или набираемый номер.</summary>
    public string CallTitle => ActiveLine?.Title ?? _pendingNumber ?? string.Empty;

    /// <summary>
    /// Занята ли панель звонком — установленным или ещё только идущим.
    /// </summary>
    public bool IsInCall => Lines.Count > 0 || _pendingNumber is not null;

    /// <summary>Всё, что считается от <see cref="IsInCall"/>, — одним списком.</summary>
    ///
    /// <remarks>
    /// Списком в одном месте, а не строками в каждом сеттере: до 11 сентября
    /// 2026 <see cref="CanMuteMicrophone"/> не попал ни в один из трёх наборов
    /// уведомлений, и кнопка «Микрофон» оставалась серой весь разговор —
    /// окно спросило её один раз, до звонка, и больше не переспрашивало.
    /// </remarks>
    private void NotifyCallControls()
    {
        NotifyChanged(nameof(IsInCall));
        NotifyChanged(nameof(IsCallButtonEnabled));
        NotifyChanged(nameof(CanOpenProfileMenu));
        NotifyChanged(nameof(CanHold));
        NotifyChanged(nameof(CanMuteMicrophone));
        NotifyChanged(nameof(CanTransfer));
        NotifyChanged(nameof(CanStartConference));
        ResyncToggles();
    }

    /// <summary>Заново сообщить окну состояние кнопок-переключателей ряда управления.</summary>
    ///
    /// <remarks>
    /// Кнопки ряда — `ToggleButton` с привязкой в одну сторону, и щелчок
    /// переключает их сам, не дожидаясь модели. Если модель после этого не
    /// сменила значение — удержание не встало, собеседник положил трубку, пока
    /// шёл повторный INVITE, — уведомления нет, и кнопка оставалась «нажатой»
    /// и после конца разговора. Повторное уведомление дешевле, чем выяснять,
    /// какой из путей его пропустил.
    /// </remarks>
    public void ResyncToggles()
    {
        NotifyChanged(nameof(IsOnHold));
        NotifyChanged(nameof(IsMicrophoneMuted));
        NotifyChanged(nameof(IsConferenceStarted));
        NotifyChanged(nameof(IsTransferEntryVisible));
    }

    /// <summary>Строка состояния разговора: «разговор», «удержание», «перевод…».</summary>
    public string CallStatus
    {
        get => _callStatus;
        set => Set(ref _callStatus, value);
    }

    // --- Ряд управления ---------------------------------------------------

    public bool IsOnHold
    {
        get => _isOnHold;
        set => Set(ref _isOnHold, value);
    }

    public bool IsMicrophoneMuted
    {
        get => _isMicrophoneMuted;
        set => Set(ref _isMicrophoneMuted, value);
    }

    /// <summary>Команда конференции ушла — приложение знает, что комната собрана.</summary>
    public bool IsConferenceStarted
    {
        get => _isConferenceStarted;
        set
        {
            Set(ref _isConferenceStarted, value);
            NotifyChanged(nameof(CanStartConference));
        }
    }

    public bool CanHold => IsInCall;

    public bool CanMuteMicrophone => IsInCall;

    public bool CanTransfer => IsInCall && !_isTransferEntryVisible;

    public bool CanStartConference => IsInCall && !_isConferenceStarted;

    // --- Поле перевода ----------------------------------------------------

    /// <summary>
    /// Поле перевода занимает место сетки макросов, а не встаёт под ней: пока
    /// оператор набирает номер, макросы всё равно не нужны.
    /// </summary>
    public bool IsTransferEntryVisible
    {
        get => _isTransferEntryVisible;
        set
        {
            Set(ref _isTransferEntryVisible, value);
            NotifyChanged(nameof(CanTransfer));
            NotifyChanged(nameof(ShowsMacros));
        }
    }

    public bool IsTransferring
    {
        get => _isTransferring;
        set => Set(ref _isTransferring, value);
    }

    public string TransferNumber
    {
        get => _transferNumber;
        set
        {
            Set(ref _transferNumber, value);
            NotifyChanged(nameof(HasTransferNumber));
        }
    }

    public bool HasTransferNumber => !string.IsNullOrWhiteSpace(_transferNumber);

    // --- Сетка макросов ---------------------------------------------------

    public ObservableCollection<MacroViewModel> Macros { get; } = [];

    public bool HasMacros => Macros.Count > 0;

    /// <summary>Видна ли сетка: поле перевода занимает то же место.</summary>
    public bool ShowsMacros => !_isTransferEntryVisible;

    /// <summary>Сколько клавиш в ряду — задаёт администратор.</summary>
    ///
    /// <remarks>
    /// Прежде было константой темы. Константа верна для коротких подписей вроде
    /// «Юрист» и неверна для названий отдела: три в ряд ужимают их до
    /// нечитаемого.
    /// </remarks>
    public int MacroColumns
    {
        get => _macroColumns;
        set => Set(ref _macroColumns, value);
    }

    public double MacroHeight
    {
        get => _macroHeight;
        set => Set(ref _macroHeight, value);
    }

    // --- Неподвижный низ --------------------------------------------------

    /// <summary>Есть ли регистрация: без неё набирать некуда.</summary>
    public bool CanPlaceCall
    {
        get => _canPlaceCall;
        set
        {
            Set(ref _canPlaceCall, value);
            NotifyChanged(nameof(IsCallButtonEnabled));
        }
    }

    public bool CanSendDtmf
    {
        get => _canSendDtmf;
        set => Set(ref _canSendDtmf, value);
    }

    /// <summary>
    /// В разговоре кнопка всегда живая — это «Завершить», и до неё тянутся не
    /// глядя. Вне разговора она оживает только с набранным номером.
    /// </summary>
    public bool IsCallButtonEnabled => IsInCall || (CanPlaceCall && HasDialedNumber);

    // --- Действия ---------------------------------------------------------
    //
    // Команды объявлены здесь, а исполняются слоем приложения: панель знает,
    // когда действие возможно, но не знает, как оно делается.

    public ICommand? CallOrHangUp { get; set; }

    public ICommand? ToggleHold { get; set; }

    public ICommand? ToggleMicrophone { get; set; }

    /// <summary>В поле номера набран символ — для звука нажатия.</summary>
    public Action<char>? KeyPressed { get; set; }

    public ICommand? StartConference { get; set; }

    public ICommand? SendMacro { get; set; }

    public ICommand? ShowHistory { get; set; }

    public ICommand? ShowSettings { get; set; }

    /// <summary>Открывает поле перевода. Само по себе — дело панели, а не протокола.</summary>
    public void ShowTransferEntry()
    {
        TransferNumber = string.Empty;
        IsTransferEntryVisible = true;
    }

    public void CancelTransferEntry()
    {
        IsTransferEntryVisible = false;
        TransferNumber = string.Empty;
    }

    public ICommand? Transfer { get; set; }

    /// <summary>Толкает таймеры всех линий. Зовётся раз в секунду.</summary>
    public void Tick()
    {
        foreach (var line in Lines)
        {
            line.Tick();
        }
    }
}

/// <summary>Основание для всего, что показывается: уведомление об изменении.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        NotifyChanged(name);
    }

    protected void NotifyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
