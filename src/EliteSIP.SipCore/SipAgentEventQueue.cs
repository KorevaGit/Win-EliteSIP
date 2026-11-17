using System.Runtime.CompilerServices;

namespace EliteSIP.SipCore;

/// <summary>
/// Call/control events have their own FIFO. Diagnostics have a fixed budget and
/// can never evict a call. Writers do not wait for a blocked UI or audio driver.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "SemaphoreSlim is used only asynchronously; no native WaitHandle is created. Disposing it would invalidate an outstanding reader during queue completion.")]
internal sealed class SipAgentEventQueue(int diagnosticCapacity = 256)
{
    private readonly object _gate = new();
    private readonly Queue<SipUserAgentEvent> _control = new();
    private readonly Queue<SipUserAgentEvent> _diagnostics = new();
    private readonly SemaphoreSlim _ready = new(0);
    private bool _completed;
    private long _dropped;

    internal long DroppedDiagnostics => Interlocked.Read(ref _dropped);

    internal bool TryWrite(SipUserAgentEvent value)
    {
        lock (_gate)
        {
            if (_completed) { return false; }
            if (value is SipUserAgentEvent.Log or SipUserAgentEvent.UnsupportedRequest)
            {
                if (_diagnostics.Count >= diagnosticCapacity)
                {
                    Interlocked.Increment(ref _dropped);
                    return false;
                }
                _diagnostics.Enqueue(value);
            }
            else
            {
                // This stream contains lifecycle transitions, not packet traffic.
                // Active calls are admission-limited, unlike diagnostic traffic.
                // Retain their transitions during consumer stalls rather than
                // silently evicting them. There is deliberately no lossy hard cap.
                _control.Enqueue(value);
            }
            _ready.Release();
            return true;
        }
    }

    internal void TryComplete()
    {
        lock (_gate)
        {
            if (_completed) { return; }
            _completed = true;
            _ready.Release();
        }
    }

    internal async IAsyncEnumerable<SipUserAgentEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_completed && _control.Count == 0 && _diagnostics.Count == 0) { yield break; }
            }
            await _ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            SipUserAgentEvent? next;
            lock (_gate)
            {
                if (_control.TryDequeue(out next) || _diagnostics.TryDequeue(out next)) { }
                else if (_completed) { yield break; }
                else { continue; }
            }
            yield return next;
        }
    }
}
