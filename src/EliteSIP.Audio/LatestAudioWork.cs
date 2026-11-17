namespace EliteSIP.Audio;

/// <summary>One driver worker and one latest request; stale opens cannot publish playback.</summary>
internal sealed class LatestAudioWork(Action<Exception> failed)
{
    private readonly object _gate = new();
    private (long Version, Action<long> Run, Action? Abandon)? _pending;
    private long _version;
    private bool _running;

    internal bool IsCurrent(long version) => version == Interlocked.Read(ref _version);

    internal void Post(Action<long> run, Action? abandon = null)
    {
        Action? discarded;
        lock (_gate)
        {
            discarded = _pending?.Abandon;
            _pending = (Interlocked.Increment(ref _version), run, abandon);
            if (!_running)
            {
                _running = true;
                _ = Task.Run(Drain);
            }
        }
        if (discarded is not null) { _ = Task.Run(() => AudioWorkerBoundary.Run(discarded, failed)); }
    }

    private void Drain()
    {
        while (true)
        {
            (long Version, Action<long> Run, Action? Abandon) work;
            lock (_gate)
            {
                if (_pending is not { } next) { _running = false; return; }
                work = next;
                _pending = null;
            }
            AudioWorkerBoundary.Run(() => work.Run(work.Version), failed);
        }
    }
}
