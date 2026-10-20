namespace EliteSIP.App.Platform;

/// <summary>
/// Поток окна: всё, что трогает модели видов, делается на нём.
/// </summary>
///
/// <remarks>
/// <para>
/// До 20 октября 2026 телефон, линия Spark и обновления держали
/// <c>System.Windows.Threading.Dispatcher</c> напрямую — и этим держали себя на
/// WPF. Здесь ровно то, что им от него нужно, и ничего сверх: узнать, на нём
/// ли мы; поставить работу в очередь; дождаться её; завести таймер, который
/// тикает на нём же.
/// </para>
/// <para>
/// Реализации — над <c>Dispatcher</c> в WPF-приложении и над
/// <c>Dispatcher.UIThread</c> в Linux-версии на Avalonia.
/// </para>
/// </remarks>
public interface IUiThread
{
    /// <summary>Мы сейчас на потоке окна.</summary>
    public bool CheckAccess();

    /// <summary>Поставить работу в очередь потока окна и не ждать её.</summary>
    public void Post(Action action);

    /// <summary>Выполнить работу на потоке окна; задача кончается вместе с ней.</summary>
    public Task InvokeAsync(Action action);

    /// <summary>
    /// Таймер, который тикает на потоке окна. Создаётся остановленным.
    /// </summary>
    ///
    /// <remarks>
    /// Тикает с низким приоритетом — после ввода и отрисовки. Так стояли все
    /// таймеры приложения до 20 октября 2026: <c>DispatcherTimer</c> WPF без
    /// явного приоритета берёт <c>Background</c>, а такт статусов телефона
    /// просил его явно. Работа на тактах неспешная, а окно отвечать обязано.
    /// </remarks>
    public IUiTimer CreateTimer(TimeSpan interval);
}

/// <summary>Таймер потока окна — то, чем у WPF был <c>DispatcherTimer</c>.</summary>
// CA1716 про Stop — то же решение, что у IVoiceAudioEngine: продукт на C#, а
// «остановить таймер» на другом слове читаться перестанет.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "продукт на C#; Start/Stop — имена DispatcherTimer и таймеров Avalonia")]
public interface IUiTimer
{
    /// <summary>Период. Менять можно на ходу: следующий такт пойдёт по новому.</summary>
    public TimeSpan Interval { get; set; }

    /// <summary>Таймер запущен.</summary>
    public bool IsEnabled { get; }

    /// <summary>Такт. Вызывается на потоке окна.</summary>
    public event EventHandler? Tick;

    public void Start();

    public void Stop();
}
