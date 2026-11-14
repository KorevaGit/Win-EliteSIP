using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using EliteSIP.App.Incoming;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.App.Shell;
using EliteSIP.CallHistory;
using EliteSIP.SipCore;

namespace EliteSIP.App.Tests;

public sealed class NetworkRecoveryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Закрытие_канала_переподключает_телефон_и_возвращает_исходящие(int queuedConnections)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = new AppSettings();
        settings.Account.Username = "707";
        settings.Account.Domain = "127.0.0.1";
        settings.Pbx.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        settings.Pbx.Transport = Settings.SipTransport.Tcp;
        settings.Credentials.SetPassword("test");
        var path = Path.Combine(Path.GetTempPath(), $"network-recovery-{Guid.NewGuid():N}.db");
        using var history = new CallHistoryStore(new(path));
        var panel = new PanelViewModel();
        PhoneService phone = null!;
        Task startup = Task.CompletedTask;

        WpfHost.Run(() =>
        {
            phone = new PhoneService(settings, panel, history,
                new IncomingCallPresenter(_ => { }, () => 0), Dispatcher.CurrentDispatcher, _ => { });
            phone.RecoverConnection = () => _ = phone.ConnectAsync();
            var gate = (SemaphoreSlim)typeof(PhoneService).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(phone)!;
            Assert.True(gate.Wait(0));
            try { startup = Task.WhenAll(Enumerable.Range(0, queuedConnections).Select(_ => phone.ConnectAsync())); }
            finally { gate.Release(); }
        });

        try
        {
            await startup.WaitAsync(token);
            using (var first = await listener.AcceptTcpClientAsync(token))
            {
                await RespondAsync(first.GetStream(), SipMethod.Register, 200, token);
                await WaitForDialAsync(panel, token);
            }

            using var second = await listener.AcceptTcpClientAsync(token);
            var stream = second.GetStream();
            await RespondAsync(stream, SipMethod.Register, 200, token);
            await WaitForDialAsync(panel, token);

            Task dialing = Task.CompletedTask;
            WpfHost.Run(() => dialing = phone.PlaceCallAsync("709"));
            await RespondAsync(stream, SipMethod.Invite, 486, token);
            await dialing.WaitAsync(token);
        }
        finally
        {
            WpfHost.Run(phone.Dispose);
            history.Dispose();
            File.Delete(path);
        }
    }

    private static async Task WaitForDialAsync(PanelViewModel panel, CancellationToken token)
    {
        while (true)
        {
            var ready = false;
            WpfHost.Run(() => ready = panel.CanPlaceCall);
            if (ready)
            {
                return;
            }
            await Task.Delay(20, token);
        }
    }

    private static async Task RespondAsync(NetworkStream stream, SipMethod method, int status, CancellationToken token)
    {
        var framer = new SipMessageFramer();
        var buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token);
            Assert.True(read > 0);
            framer.Append(buffer.AsSpan(0, read));
            while (framer.NextMessageData() is { } message)
            {
                var request = SipParser.Parse(message).AsRequest;
                if (request?.Method != method)
                {
                    continue;
                }

                var headers = new SipHeaders();
                foreach (var name in new[] { SipHeaderName.Via, SipHeaderName.From, SipHeaderName.To, SipHeaderName.CallId, SipHeaderName.CSeq })
                {
                    headers.Append(name, request.Headers.First(name)!);
                }
                var response = new SipResponse(status, status == 200 ? "OK" : "Busy Here", headers);
                await stream.WriteAsync(response.Encoded(), token);
                return;
            }
        }
    }
}
