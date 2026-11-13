using System.Net;
using System.Net.Sockets;
using EliteSIP.SipCore.Udp;

namespace EliteSIP.SipCore.Tests;

public sealed class RecoveryAuditTests
{
    private static SipRequest Options()
    {
        var request = new SipRequest(SipMethod.Options, new SipUri("127.0.0.1"));
        request.Headers.Append(SipHeaderName.Via, new SipVia(SipTransport.Udp, "127.0.0.1", 5060) { Branch = SipToken.Branch() }.ToString());
        request.Headers.Append(SipHeaderName.CSeq, "1 OPTIONS");
        return request;
    }

    [Fact]
    public async Task Cancelled_transaction_stops_retransmitting_and_allows_next_request()
    {
        var respond = false;
        var server = new ScriptedSipServer((request, _) => Volatile.Read(ref respond) ? ScriptedSipServer.RegistrationAccepted(request) : null);
        using var layer = new SipTransactionLayer(server, new SipTransactionTimers { T1 = TimeSpan.FromMilliseconds(20) });
        await layer.StartAsync();
        await layer.WaitUntilReadyAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = layer.SendAsync(Options(), cancellation.Token);
        Assert.True(await TestSupport.WaitUntilAsync(() => server.ReceivedRequests.Count >= 2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var count = server.ReceivedRequests.Count;
        await Task.Delay(150);
        Assert.Equal(count, server.ReceivedRequests.Count);
        Volatile.Write(ref respond, true);
        Assert.Equal(200, (await layer.SendAsync(Options())).StatusCode);
        await layer.StopAsync();
    }

    [Fact]
    public async Task Cancellation_removes_readiness_waiter_without_cancelling_other_waiters()
    {
        var server = new ScriptedSipServer((_, _) => null) { StaysSilentOnStart = true };
        using var layer = new SipTransactionLayer(server);
        await layer.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelled = layer.WaitUntilReadyAsync(cancellationToken: cancellation.Token);
        var other = layer.WaitUntilReadyAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        server.BecomeReady();
        Assert.Equal(server.Local, await other.WaitAsync(TimeSpan.FromSeconds(2)));
        await layer.StopAsync();
    }

    [Fact]
    public async Task Unregister_obeys_three_second_deadline_when_server_disappears()
    {
        var server = new ScriptedSipServer((request, _) => request.Headers.Number(SipHeaderName.Expires) == 0
            ? null : ScriptedSipServer.RegistrationAccepted(request));
        using var agent = new SipUserAgent(TestSupport.TestAccount(server.Transport), TestSupport.TestCredentials, server);
        await agent.StartAsync();
        Assert.True(await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered));
        await agent.StopAsync().WaitAsync(TimeSpan.FromSeconds(4.5));
        var count = server.ReceivedRequests.Count;
        await Task.Delay(150);
        Assert.Equal(count, server.ReceivedRequests.Count);
    }

    [Fact]
    public async Task Localhost_connects_to_ipv4_listener_when_ipv6_is_unavailable()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = (ushort)((IPEndPoint)listener.LocalEndPoint!).Port;
        using var transport = new SocketSipTransport(new SipEndpoint("localhost", port), SipTransport.Tcp);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var accepted = listener.AcceptAsync(deadline.Token).AsTask();
        await transport.StartAsync();
        await foreach (var item in transport.Events.WithCancellation(deadline.Token))
        {
            Assert.IsNotType<SipTransportEvent.Failed>(item);
            if (item is SipTransportEvent.Ready)
            {
                using var socket = await accepted;
                await transport.StopAsync();
                return;
            }
        }
        Assert.Fail("Transport never became ready");
    }
}
