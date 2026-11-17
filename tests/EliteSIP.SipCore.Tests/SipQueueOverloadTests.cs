namespace EliteSIP.SipCore.Tests;

public sealed class SipQueueOverloadTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Provisional_flood_cannot_evict_terminal_or_reopen_finished_stream(int terminalKind)
    {
        var queue = new SipInviteEventQueue();
        for (var i = 0; i < SipInviteEventQueue.ProvisionalCapacity; i++)
        {
            Assert.True(queue.TryWrite(new SipInviteEvent.Provisional(new(180))));
        }
        Assert.False(queue.TryWrite(new SipInviteEvent.Provisional(new(183))));
        SipInviteEvent terminal = terminalKind switch
        {
            0 => new SipInviteEvent.Success(new(200)),
            1 => new SipInviteEvent.Failure(new(486)),
            2 => new SipInviteEvent.Timeout(),
            _ => new SipInviteEvent.TransportFailed("closed"),
        };
        Assert.True(queue.TryWrite(terminal));
        Assert.False(queue.TryWrite(new SipInviteEvent.Provisional(new(180))));
        Assert.False(queue.TryWrite(new SipInviteEvent.Timeout()));
        var values = new List<SipInviteEvent>();
        await foreach (var value in queue.ReadAllAsync()) { values.Add(value); }
        Assert.Equal(SipInviteEventQueue.ProvisionalCapacity + 1, values.Count);
        Assert.Same(terminal, values[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_inbound_queue_keeps_admitted_requests_and_rejects_new_request_explicitly(bool sendFails)
    {
        var server = new ScriptedSipServer((_, _) => null) { FailResponseWrites = sendFails };
        using var layer = new SipTransactionLayer(server, TestSupport.FastTimers());
        var failedSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        layer.OnDiscarded = (_, message) =>
        {
            if (message.Contains("synthetic response send failure", StringComparison.Ordinal)) { failedSend.TrySetResult(); }
        };
        await layer.StartAsync();
        try
        {
            for (var i = 0; i <= SipTransactionLayer.InboundQueueCapacity; i++)
            {
                var request = new SipRequest(i == 0 ? SipMethod.Bye : SipMethod.Options, new SipUri("localhost", user: "100"));
                request.Headers.Set(SipHeaderName.Via, $"SIP/2.0/UDP localhost;branch=z9hG4bK-{i}");
                request.Headers.Set(SipHeaderName.From, "<sip:200@localhost>;tag=remote");
                request.Headers.Set(SipHeaderName.To, "<sip:100@localhost>;tag=local");
                request.Headers.Set(SipHeaderName.CallId, $"overload-{i}");
                request.Headers.Set(SipHeaderName.CSeq, $"1 {request.Method.Name()}");
                server.Inject(request);
            }
            if (sendFails) { await failedSend.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            else
            {
                Assert.True(await TestSupport.WaitUntilAsync(() => server.SentResponses.Any(r => r.StatusCode == 503)));
                var rejected = Assert.Single(server.SentResponses, r => r.StatusCode == 503);
                Assert.Equal($"overload-{SipTransactionLayer.InboundQueueCapacity}", rejected.CallId);
                Assert.Equal("1", rejected.Headers.First("Retry-After"));
            }
            await using var reader = layer.InboundRequests.GetAsyncEnumerator();
            for (var i = 0; i < SipTransactionLayer.InboundQueueCapacity; i++)
            {
                Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal($"overload-{i}", reader.Current.CallId);
            }
            var followup = new SipRequest(SipMethod.Options, new SipUri("localhost", user: "100"));
            followup.Headers.Set(SipHeaderName.CallId, "after-overload");
            server.Inject(followup);
            Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("after-overload", reader.Current.CallId);
        }
        finally { await layer.StopAsync(); }
    }
}
