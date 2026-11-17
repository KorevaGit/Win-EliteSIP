using System.Diagnostics;
using System.Windows.Threading;

namespace EliteSIP.App.Shell;

/// <summary>One outstanding UI probe; no queue growth while the dispatcher is blocked.</summary>
internal sealed class ResponsivenessWatch : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly Timer _timer;
    private long _sent;
    private int _pending;
    private int _reported;
    private int _disposed;

    internal ResponsivenessWatch(Dispatcher dispatcher, Action<string> log)
    {
        _dispatcher = dispatcher;
        _log = log;
        _timer = new Timer(TickSafely, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void TickSafely(object? state)
    {
        try { Tick(state); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A closing dispatcher/process must not turn diagnostics into a crash.
        }
    }

    private void Tick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) { return; }
        if (Interlocked.CompareExchange(ref _pending, 1, 0) == 0)
        {
            Volatile.Write(ref _sent, Stopwatch.GetTimestamp());
            if (_dispatcher.HasShutdownStarted) { return; }
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _sent));
                if (Interlocked.Exchange(ref _reported, 0) != 0 && Volatile.Read(ref _disposed) == 0)
                {
                    _log($"интерфейс: очередь возобновилась после {elapsed.TotalSeconds:F1} с; PID={Environment.ProcessId}");
                }
                Volatile.Write(ref _pending, 0);
            }));
        }
        else if (Stopwatch.GetElapsedTime(Volatile.Read(ref _sent)) >= TimeSpan.FromSeconds(10)
                 && Interlocked.Exchange(ref _reported, 1) == 0)
        {
            using var process = Process.GetCurrentProcess();
            _log($"интерфейс: очередь не отвечает 10 с; PID={process.Id}; "
                + $"RAM={process.WorkingSet64 / 1048576} МБ; private={process.PrivateMemorySize64 / 1048576} МБ; "
                + $"CPU total={process.TotalProcessorTime.TotalSeconds:F1} с; threads={process.Threads.Count}; "
                + $"GC={GC.GetTotalMemory(false) / 1048576} МБ; pool pending={ThreadPool.PendingWorkItemCount}");
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, 1);
        _timer.Dispose();
    }
}
