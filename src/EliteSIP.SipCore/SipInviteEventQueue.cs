using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace EliteSIP.SipCore;

/// <summary>A bounded provisional backlog and one reserved terminal event.</summary>
internal sealed class SipInviteEventQueue
{
    internal const int ProvisionalCapacity = 32;
    private readonly object _gate = new();
    private readonly Channel<SipInviteEvent> _events = Channel.CreateUnbounded<SipInviteEvent>();
    private int _provisional;
    private bool _finished;

    internal bool TryWrite(SipInviteEvent value)
    {
        lock (_gate)
        {
            if (_finished) { return false; }
            if (value is SipInviteEvent.Provisional)
            {
                if (_provisional >= ProvisionalCapacity) { return false; }
                _provisional++;
            }
            else { _finished = true; }
            _events.Writer.TryWrite(value);
            if (_finished) { _events.Writer.TryComplete(); }
            return true;
        }
    }

    internal void TryComplete()
    {
        lock (_gate) { _finished = true; _events.Writer.TryComplete(); }
    }

    internal async IAsyncEnumerable<SipInviteEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var value in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (value is SipInviteEvent.Provisional)
            {
                lock (_gate) { _provisional--; }
            }
            yield return value;
        }
    }
}
