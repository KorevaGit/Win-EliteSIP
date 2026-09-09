using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EliteSIP.App.Panel;

/// <summary>Панель софтфона — единственное окно, которое оператор видит весь день.</summary>
///
/// <remarks>
/// Главное правило, из которого выведено всё остальное: **нижняя полоса
/// неподвижна**. Кнопка завершения обязана оказываться под курсором в одном и
/// том же месте независимо от того, появилась ли вторая линия, потеряна ли
/// регистрация, открыто ли поле перевода и сколько у сотрудника макросов.
/// </remarks>
public partial class PanelWindow : Window
{
    private readonly DispatcherTimer _clock;
    private double _bottomEdge;

    public PanelWindow(PanelViewModel model)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;

        // Такт разговора: ровно раз в секунду и только пока панель на экране.
        // `DispatcherTimer`, а не таймер из пула, потому что он толкает
        // привязки — а те живут в потоке интерфейса.
        //
        // Точность здесь не нужна и вредна: таймер на 100 мс ради ровной смены
        // цифры будил бы процесс десять раз в секунду весь рабочий день.
        _clock = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _clock.Tick += (_, _) => Model.Tick();
    }

    public PanelViewModel Model { get; }

    /// <summary>
    /// Где панель стояла в прошлый раз и куда её вернуть.
    /// </summary>
    ///
    /// <remarks>
    /// Ставится приложением, потому что настройки — его дело, а не окна. Пустое
    /// значение означает первый запуск: тогда окно встаёт посреди экрана, как и
    /// было.
    /// </remarks>
    public Func<(double Left, double Top)?>? RestorePlacement { get; init; }

    /// <summary>Куда панель переехала. Зовётся при закрытии.</summary>
    public Action<double, double>? SavePlacement { get; init; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        Restore();

        // Место запоминается по концу перетаскивания, а не по каждому шагу
        // мыши: настройки пишутся на каждую правку, и запись на каждый пиксель
        // означала бы сотню записей на один перенос окна.
        //
        // Закрытие тоже сохраняет — но одного его мало: машину выключают, не
        // закрывая софтфон, и тогда OnClosed не случается вовсе.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(OnMessage);
        }

        _bottomEdge = Top + ActualHeight;
        _clock.Start();
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_EXITSIZEMOVE = 0x0232;

        if (message == WM_EXITSIZEMOVE && WindowState is WindowState.Normal)
        {
            SavePlacement?.Invoke(Left, Top);
        }

        return 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();

        if (WindowState is WindowState.Normal)
        {
            SavePlacement?.Invoke(Left, Top);
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Возвращает панель туда, где её оставили.
    /// </summary>
    ///
    /// <remarks>
    /// <b>С проверкой, что это место ещё существует.</b> Панель, оставленная на
    /// втором мониторе, после его отключения оказалась бы за краем рабочего
    /// стола — то есть невидимой, притом что приложение считает её показанной.
    /// Не поместилась — встаёт посреди основного экрана, как при первом запуске.
    /// </remarks>
    private void Restore()
    {
        if (RestorePlacement?.Invoke() is not { } placement)
        {
            return;
        }

        var visible = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        // Требуется, чтобы на экране оказался весь заголовок с кнопками, а не
        // уголок окна: панель, у которой видны две точки, не перетащить мышью.
        var wanted = new Rect(placement.Left, placement.Top, ActualWidth, ActualHeight);

        if (!visible.Contains(wanted))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
    }

    /// <summary>Enter в поле номера звонит.</summary>
    private void OnDialedNumberKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not System.Windows.Input.Key.Enter)
        {
            return;
        }

        // Через ту же команду, что и кнопка: у неё уже есть все проверки —
        // пустой номер, отсутствие регистрации, идущий разговор.
        if (Model.CallOrHangUp?.CanExecute(null) is true)
        {
            Model.CallOrHangUp.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Держит нижний край окна на месте, когда середина меняет высоту.</summary>
    ///
    /// <remarks>
    /// Это то место, где Windows расходится с macOS, и разойтись ей есть на чём.
    /// Начало координат окна на macOS — левый нижний угол, поэтому выросшее
    /// окно там раздаётся вверх само, и правило «низ не двигается» соблюдается
    /// без единой строки кода. На Windows начало — левый верхний, и окно растёт
    /// вниз: открытое поле перевода уводило бы кнопку «Завершить» на 70 точек
    /// вниз ровно в тот момент, когда до неё тянутся не глядя.
    ///
    /// Поэтому верхний край сдвигается на разницу высот. Считается это от
    /// запомненного низа, а не «текущий Top минус дельта»: округления
    /// накапливались бы, и за день окно уползало бы вверх по точке за
    /// перевод.
    /// </remarks>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (!sizeInfo.HeightChanged || _bottomEdge <= 0)
        {
            return;
        }

        // Свёрнутое и развёрнутое окно двигать нельзя: у первого координаты
        // ничего не значат, у второго их задаёт система.
        if (WindowState is not WindowState.Normal)
        {
            return;
        }

        Top = _bottomEdge - sizeInfo.NewSize.Height;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);

        // Окно передвинули мышью — значит, новый низ и есть тот, который надо
        // держать. Без этого панель, оттащенная в угол экрана, прыгала бы
        // обратно к прежнему низу при первом же изменении высоты.
        if (WindowState is WindowState.Normal && IsLoaded)
        {
            _bottomEdge = Top + ActualHeight;
        }
    }

    /// <summary>Закрытие панели прячет её в область уведомлений, а не гасит.</summary>
    ///
    /// <remarks>
    /// Софтфон, закрытый крестиком, обязан продолжать принимать звонки:
    /// оператор закрывает панель, чтобы она не мешала, а не чтобы перестать
    /// быть на линии. Выход живёт в меню значка — единственном месте, откуда
    /// его видно при спрятанной панели.
    ///
    /// Своих кнопок окна у панели больше нет — полоса заголовка системная, — и
    /// это место стало единственным, где закрытие перехватывается. Раньше их
    /// было два: свой крестик и вот эта проверка, и совпадали они только
    /// потому, что делали одно и то же руками.
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // Крестик в полосе заголовка, Alt+F4 и «Закрыть» из панели задач —
        // теперь один и тот же путь.
        if (!AllowsClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>Разрешено ли окну закрыться по-настоящему. Ставит выход.</summary>
    public bool AllowsClosing { get; set; }

    private void OnClearNumberClick(object sender, RoutedEventArgs e)
        => Model.DialedNumber = string.Empty;

    /// <summary>Раскрывает меню профиля под капсулой.</summary>
    ///
    /// <remarks>
    /// Руками, а не само: меню, привязанное к кнопке через
    /// <c>ContextMenu</c>, WPF открывает по правому щелчку — так устроено
    /// контекстное меню. Здесь же оно не контекстное, а выпадающее: открывать
    /// его обязан обычный щелчок, тот же, каким открываются все прочие списки
    /// в приложении.
    ///
    /// Место назначается тут же: без <c>PlacementTarget</c> меню встаёт под
    /// курсором, то есть каждый раз в новом месте, а список под кнопкой обязан
    /// открываться под кнопкой.
    /// </remarks>
    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button pill || pill.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = pill;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = -8;
        menu.VerticalOffset = -4;

        // Данные меню берёт у кнопки: всплывающее живёт вне дерева окна, и
        // `DataContext` туда сам не доходит.
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    private void OnTransferClick(object sender, RoutedEventArgs e)
    {
        Model.ShowTransferEntry();

        // Поле открывается ради ввода, значит и курсор в нём. Без этого
        // оператор жмёт «Перевести», начинает набирать и обнаруживает, что
        // цифры уходят в поле набора над ним.
        TransferField.Focus();
    }

    private void OnCancelTransferClick(object sender, RoutedEventArgs e)
        => Model.CancelTransferEntry();
}
