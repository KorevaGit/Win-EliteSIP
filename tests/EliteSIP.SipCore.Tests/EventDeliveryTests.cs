using EliteSIP.SipCore;

namespace EliteSIP.SipCore.Tests;

public sealed class EventDeliveryTests
{
    [Fact]
    public async Task Concurrent_writers_keep_every_control_event_and_each_writers_order()
    {
        var queue = new SipAgentEventQueue(8);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(producer => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                queue.TryWrite(new SipUserAgentEvent.Log(SipLogLevel.Debug, "noise"));
                Assert.True(queue.TryWrite(new SipUserAgentEvent.ChannelClosed($"{producer}:{i}")));
            }
        })));
        queue.TryComplete();
        var received = new List<string>();
        var logs = 0;
        await foreach (var value in queue.ReadAllAsync())
        {
            if (value is SipUserAgentEvent.ChannelClosed closed) { received.Add(closed.Reason); }
            else { logs++; }
        }
        Assert.Equal(400, received.Count);
        Assert.Equal(8, logs);
        Assert.Equal(392, queue.DroppedDiagnostics);
        for (var producer = 0; producer < 4; producer++)
        {
            var prefix = producer.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
            Assert.Equal(Enumerable.Range(0, 100).Select(i => prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                received.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)));
        }
    }
    [Fact]
    public async Task Log_flood_cannot_evict_incoming_or_closed_and_control_has_priority()
    {
        var queue = new SipAgentEventQueue(4);
        for (var i = 0; i < 4; i++) { Assert.True(queue.TryWrite(new SipUserAgentEvent.Log(SipLogLevel.Debug, i.ToString(System.Globalization.CultureInfo.InvariantCulture)))); }
        var incoming = new SipUserAgentEvent.IncomingCall(new SipIncomingCall
        {
            CallId = "delivery-test", CallerNumber = "2929", CalledNumber = "724",
            Events = SipCallEventChannel.Create().Reader.ReadAllAsync(),
        });
        var closed = new SipUserAgentEvent.ChannelClosed("test closure");
        Assert.True(queue.TryWrite(incoming));
        for (var i = 0; i < 10000; i++) { queue.TryWrite(new SipUserAgentEvent.Log(SipLogLevel.Debug, "load")); }
        Assert.True(queue.TryWrite(closed));
        queue.TryComplete();
        var events = new List<SipUserAgentEvent>();
        await foreach (var value in queue.ReadAllAsync()) { events.Add(value); }
        Assert.Same(incoming, events[0]);
        Assert.Same(closed, events[1]);
        Assert.Equal(6, events.Count);
        Assert.Equal(10000, queue.DroppedDiagnostics);
        Assert.False(queue.TryWrite(closed));
        await foreach (var _ in queue.ReadAllAsync()) { Assert.Fail("Completed queue replayed an event"); }
    }

    [Fact]
    public async Task Completion_wakes_waiting_reader_and_cancellation_is_respected()
    {
        var queue = new SipAgentEventQueue();
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        var waiting = reader.MoveNextAsync().AsTask();
        queue.TryComplete();
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        using var cancellation = new CancellationTokenSource();
        var live = new SipAgentEventQueue();
        await using var cancelled = live.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = cancelled.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Call_lifecycle_is_not_truncated_at_old_32_event_limit()
    {
        var channel = SipCallEventChannel.Create();
        for (var i = 0; i < 100; i++) { Assert.True(channel.Writer.TryWrite(new SipCallEvent.State(new SipCallState.Incoming()))); }
        var ended = new SipCallEvent.Ended("test end");
        Assert.True(channel.Writer.TryWrite(ended));
        channel.Writer.TryComplete();
        var count = 0;
        SipCallEvent? last = null;
        await foreach (var value in channel.Reader.ReadAllAsync()) { count++; last = value; }
        Assert.Equal(101, count);
        Assert.Same(ended, last);
    }
}
