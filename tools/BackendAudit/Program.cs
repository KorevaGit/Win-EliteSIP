using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using EliteSIP.SipCore.Udp;
using EliteSIP.Audio;
using EliteSIP.Audio.Tests;
using EliteSIP.Diagnostics;
using EliteSIP.SipCore;
using EliteSIP.SipCore.Tests;

Console.WriteLine("Backend audit probes; reference findings: e66331e. Simulated SIP peers, TCP/ICMP loopback only.");

var timers = new SipTransactionTimers { T1 = TimeSpan.FromMilliseconds(80), T2 = TimeSpan.FromMilliseconds(160) };
var server = new ScriptedSipServer((request, _) => request.Headers.First(SipHeaderName.Expires) == "0"
    ? null : ScriptedSipServer.RegistrationAccepted(request));
using (var agent = new SipUserAgent(TestSupport.TestAccount(), TestSupport.TestCredentials, server, timers))
{
    await agent.StartAsync();
    if (!await TestSupport.WaitUntilAsync(() => agent.RegistrationState.IsRegistered)) { throw new InvalidOperationException("Cannot set up registration"); }
    var clock = Stopwatch.StartNew();
    await agent.StopAsync().WaitAsync(TimeSpan.FromSeconds(9));
    Console.WriteLine($"REGISTER stop: {clock.Elapsed.TotalMilliseconds:0} ms; configured stop deadline=3000 ms; transaction={timers.TransactionTimeout.TotalMilliseconds:0} ms");
}

var addresses = await Dns.GetHostAddressesAsync("localhost");
Console.WriteLine($"localhost DNS order: {string.Join(", ", addresses.Select(address => address.ToString()))}");
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
    using var transport = new SocketSipTransport(new SipEndpoint("localhost", port), SipTransport.Tcp);
    await transport.StartAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await foreach (var value in transport.Events.WithCancellation(deadline.Token))
    {
        if (value is SipTransportEvent.Failed or SipTransportEvent.Ready)
        {
            Console.WriteLine($"IPv4 listener healthy; localhost transport result: {value}");
            break;
        }
    }
    await transport.StopAsync();
}

var knockLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
using (var knocker = PortKnocker.ForServer("127.0.0.1", WorkplaceSite.Remote,
    new PortKnockSequence { Steps = [new PortKnockStep(20, count: 2), new PortKnockStep(30, count: 2)], SpacingSeconds = 0.1 }, knockLines.Enqueue)!)
{
    var periodic = knocker.OpenPathAsync(SipPathOpenReason.Periodic);
    await Task.Delay(50);
    var retry = knocker.OpenPathAsync(SipPathOpenReason.Retry);
    await Task.WhenAll(periodic, retry);
    Console.WriteLine("Concurrent knock packets: " + string.Join(" | ", knockLines.Where(line => line.Contains(": пакет ", StringComparison.Ordinal))));
}

var scheduler = new ManualScheduler();
var rebuilds = 0;
RestartSupervisor? supervisor = null;
supervisor = new RestartSupervisor(_ =>
{
    rebuilds++;
    if (rebuilds == 1)
    {
        supervisor!.Notify("real default-device change during rebuild");
        scheduler.Fire();
    }
}, scheduler.Schedule);
using (supervisor)
{
    supervisor.Notify("initial unplug");
    scheduler.Fire();
    scheduler.Fire();
    Console.WriteLine($"Audio restart: rebuilds={rebuilds}; pending={scheduler.PendingCount}; second independent event lost={rebuilds < 2 && scheduler.PendingCount == 0}");
}

var binding = new AudioRouteBinding("missing-headset-in", "missing-headset-out", "laptop-in", "laptop-out");
var change = new AudioDeviceChange(AudioDeviceChangeKind.DefaultChanged, "new-usb-out", AudioDeviceDirection.Render, AudioDeviceAvailability.Active);
Console.WriteLine($"Fallback audio: on fallback={binding.UsesFallbackOutput}; new Windows default triggers rebuild={DeviceChangeRelevance.AffectsRoute(change, binding)}");

using (var engine = new BlockingEngine())
using (var bus = new VoiceAudioBus(engine, _ => new FakeVoiceAudioEngine()))
{
    var claim = Task.Run(() => bus.Claim(AudioOwnerToken.New(), new VoiceAudioConfiguration(), VoiceAudioHandlers.None));
    if (!engine.Entered.Wait(TimeSpan.FromSeconds(3))) { throw new InvalidOperationException("No audio start"); }
    var query = Task.Run(() => bus.TakeLevels());
    await Task.Delay(300);
    Console.WriteLine($"Audio bus: UI meter blocked by device Start for 300 ms={ !query.IsCompleted }");
    engine.Release.Set();
    await claim;
    await query;
}

var folder = Path.GetFullPath("artifacts/backend-audit/log-data");
Directory.CreateDirectory(folder);
using (var log = new LogFile(new LogFile.Settings(folder)))
using (var entered = new ManualResetEventSlim())
using (var release = new ManualResetEventSlim())
using (var drained = new ManualResetEventSlim())
{
    log.Flush();
    var worker = (Task)typeof(LogFile).GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log)!;
    Console.WriteLine($"Log writer: task field type={worker.GetType().Name}; completed while channel alive={worker.IsCompleted}");
    var job = typeof(LogFile).GetNestedType("Job", BindingFlags.NonPublic)!;
    var enqueue = typeof(LogFile).GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    void Queue(Action action) => enqueue.Invoke(log, [Activator.CreateInstance(job, [null, action, null])]);
    Queue(() => { entered.Set(); release.Wait(); });
    if (!entered.Wait(TimeSpan.FromSeconds(3))) { throw new InvalidOperationException("No log work"); }
    log.Write("queued-before-dispose", "info");
    Queue(drained.Set);
    var stopping = Task.Run(log.Dispose);
    var completed = await Task.WhenAny(stopping, Task.Delay(300)) == stopping;
    Console.WriteLine($"Log writer: Dispose completed before queued work drained={completed}");
    release.Set();
    if (!drained.Wait(TimeSpan.FromSeconds(3))) { throw new InvalidOperationException("Log did not drain"); }
    await stopping.WaitAsync(TimeSpan.FromSeconds(3));
    var reopened = false;
    try { using var exclusive = new FileStream(log.CurrentFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException) { reopened = true; }
    Console.WriteLine($"Log writer: file reopened and left held after Dispose={reopened}");
}

internal sealed class BlockingEngine : IVoiceAudioEngine
{
    public ManualResetEventSlim Entered { get; } = new();
    public ManualResetEventSlim Release { get; } = new();
    public VoiceAudioHandlers Handlers { get; set; } = VoiceAudioHandlers.None;
    public AudioLatencySnapshot Latency => default;
    public void Reconfigure(VoiceAudioConfiguration configuration) { }
    public void Apply(VoiceAudioConfiguration configuration) { }
    public void Start() { Entered.Set(); Release.Wait(TimeSpan.FromSeconds(5)); }
    public void Stop() { }
    public void Restart(string reason) { }
    public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
}
