using System.Windows;
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _bottomEdge = Top + ActualHeight;
        _clock.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        base.OnClosed(e);
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

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    /// <summary>Закрытие панели прячет её в область уведомлений, а не гасит.</summary>
    ///
    /// <remarks>
    /// Софтфон, закрытый крестиком, обязан продолжать принимать звонки:
    /// оператор закрывает панель, чтобы она не мешала, а не чтобы перестать
    /// быть на линии. Выход живёт в меню значка — единственном месте, откуда
    /// его видно при спрятанной панели.
    /// </remarks>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // Системное закрытие (Alt+F4, «Закрыть» из панели задач) — то же самое
        // и по той же причине.
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
