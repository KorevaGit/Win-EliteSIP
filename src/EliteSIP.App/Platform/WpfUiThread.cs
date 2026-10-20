using System.Windows.Threading;

namespace EliteSIP.App.Platform;

/// <summary>Поток окна WPF — над <see cref="Dispatcher"/>, как было до 20 октября 2026.</summary>
internal sealed class WpfUiThread(Dispatcher dispatcher) : IUiThread
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;

    // Background — приоритет DispatcherTimer по умолчанию; так тикали все
    // таймеры приложения, пока создавали их сами.
    public IUiTimer CreateTimer(TimeSpan interval) =>
        new Timer(new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = interval,
        });

    private sealed class Timer(DispatcherTimer timer) : IUiTimer
    {
        public TimeSpan Interval
        {
            get => timer.Interval;
            set => timer.Interval = value;
        }

        public bool IsEnabled => timer.IsEnabled;

        public event EventHandler? Tick
        {
            add => timer.Tick += value;
            remove => timer.Tick -= value;
        }

        public void Start() => timer.Start();

        public void Stop() => timer.Stop();
    }
}
