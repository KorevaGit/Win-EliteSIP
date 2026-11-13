using System.Reflection;
using System.Runtime.InteropServices;

namespace EliteSIP.Audio.Tests;

[CollectionDefinition("Audio worker safety", DisableParallelization = true)]
public sealed class AudioWorkerSafetyGroup;

[Collection("Audio worker safety")]
public sealed class AudioWorkerSafetyTests
{
#pragma warning disable CA2201 // Deliberately inject COM/device failures without touching a driver.
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void No_device_during_restart_exhausts_policy_without_escaping_timer()
    {
        var configuration = new VoiceAudioConfiguration
        {
            RestartSettings = new AudioRestartSettings(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)),
        };
        using var engine = new WasapiVoiceAudioEngine(configuration) { BuildOverride = () => throw new VoiceAudioException("no microphone after unplug") };
        var events = new List<VoiceAudioEvent>();
        engine.Handlers = new VoiceAudioHandlers { Event = events.Add };
        typeof(WasapiVoiceAudioEngine).GetField("_running", Fields)!.SetValue(engine, true);
        engine.Restart("unplug");
        Assert.Single(events, item => item is VoiceAudioEvent.Broken);
    }

    [Fact]
    public void Unexpected_managed_error_in_restart_callback_is_reported_without_escape()
    {
        using var engine = new WasapiVoiceAudioEngine(new VoiceAudioConfiguration()) { BuildOverride = () => throw new NullReferenceException("injected restart fault") };
        var events = new List<VoiceAudioEvent>();
        engine.Handlers = new VoiceAudioHandlers { Event = events.Add };
        typeof(WasapiVoiceAudioEngine).GetField("_running", Fields)!.SetValue(engine, true);
        typeof(WasapiVoiceAudioEngine).GetMethod("RunSupervisedRestart", Fields)!.Invoke(engine, ["device changed"]);
        Assert.Single(events, item => item is VoiceAudioEvent.Broken);
    }

    [Fact]
    public void Loose_cable_flapping_is_not_erased_by_active_notification_and_stops_cleanly()
    {
        using var engine = new WasapiVoiceAudioEngine(new VoiceAudioConfiguration());
        var route = typeof(WasapiVoiceAudioEngine).GetField("_binding", Fields)!;
        route.SetValue(engine, new AudioRouteBinding("mic", "headset", "mic", "headset"));
        var notify = typeof(WasapiVoiceAudioEngine).GetMethod("OnDeviceChanged", Fields)!;
        var unplugged = new AudioDeviceChange(AudioDeviceChangeKind.DeviceStateChanged, "headset", AudioDeviceDirection.Render, AudioDeviceAvailability.Unplugged);
        var active = unplugged with { Availability = AudioDeviceAvailability.Active };
        var scheduler = new ManualScheduler();
        var rebuilds = 0;
        using var supervisor = new RestartSupervisor(_ =>
        {
            rebuilds++;
            route.SetValue(engine, new AudioRouteBinding("mic", "headset", "mic", "headset"));
            notify.Invoke(engine, [unplugged with { Kind = AudioDeviceChangeKind.DeviceRemoved }]);
            notify.Invoke(engine, [unplugged]);
            notify.Invoke(engine, [active]);
            engine.AcknowledgeRouteBuild();
            scheduler.Fire();
        }, scheduler.Schedule);
        typeof(WasapiVoiceAudioEngine).GetField("_supervisor", Fields)!.SetValue(engine, supervisor);
        for (var cycle = 0; cycle < 100; cycle++)
        {
            notify.Invoke(engine, [unplugged]);
            notify.Invoke(engine, [active]);
            scheduler.Fire();
            scheduler.Fire();
            Assert.Equal(cycle + 1, rebuilds);
            Assert.Equal(0, scheduler.PendingCount);
        }
        notify.Invoke(engine, [unplugged]);
        engine.Stop();
        scheduler.FireEvenIfCancelled();
        Assert.Equal(100, rebuilds);
    }

    [Fact]
    public void Failed_worker_generation_is_not_acknowledged_as_healthy()
    {
        using var engine = new WasapiVoiceAudioEngine(new VoiceAudioConfiguration());
        var route = typeof(WasapiVoiceAudioEngine).GetField("_binding", Fields)!;
        var binding = new AudioRouteBinding("mic", "headset", "mic", "headset");
        route.SetValue(engine, binding);
        typeof(WasapiVoiceAudioEngine).GetMethod("HandleWorkerFailure", Fields)!.Invoke(engine, ["capture", new COMException("invalidated")]);
        engine.AcknowledgeRouteBuild();
        Assert.Same(binding, route.GetValue(engine));
    }

    [Fact]
    public void Managed_worker_fault_and_failed_diagnostics_do_not_escape_boundary()
    {
        Exception? captured = null;
        AudioWorkerBoundary.Run(() => throw new COMException("device invalidated"), error => captured = error);
        Assert.IsType<COMException>(captured);
        AudioWorkerBoundary.Run(() => throw new InvalidOperationException("bad callback"), _ => throw new IOException("bad log"));
    }

    [Fact]
    public void Corrupted_state_and_memory_exhaustion_are_not_swallowed()
    {
        Assert.False(AudioWorkerBoundary.IsRecoverableManaged(new OutOfMemoryException()));
        Assert.False(AudioWorkerBoundary.IsRecoverableManaged(new AccessViolationException()));
        Assert.False(AudioWorkerBoundary.IsRecoverableManaged(new SEHException()));
    }

    [Fact]
    public async Task Slow_worker_keeps_resources_alive_until_it_exits_even_after_dispose()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = new WasapiVoiceAudioEngine(new VoiceAudioConfiguration());
        var thread = new Thread(() => { entered.Set(); release.Wait(); }) { IsBackground = true };
        thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        typeof(WasapiVoiceAudioEngine).GetField("_captureThread", Fields)!.SetValue(engine, thread);
        typeof(WasapiVoiceAudioEngine).GetField("_stop", Fields)!.SetValue(engine, new CancellationTokenSource());
        var wake = (EventWaitHandle)typeof(WasapiVoiceAudioEngine).GetField("_feedWake", Fields)!.GetValue(engine)!;
        var handle = wake.SafeWaitHandle;
        try
        {
            engine.Dispose();
            Assert.False(handle.IsClosed);
            Assert.NotNull(typeof(WasapiVoiceAudioEngine).GetField("_stop", Fields)!.GetValue(engine));
        }
        finally { release.Set(); }
        await Task.Run(() => thread.Join());
        Assert.True(SpinWait.SpinUntil(() => handle.IsClosed, TimeSpan.FromSeconds(3)));
        Assert.Null(typeof(WasapiVoiceAudioEngine).GetField("_stop", Fields)!.GetValue(engine));
    }

    [Fact]
    public void Burst_of_device_faults_stops_recovery_instead_of_restarting_forever()
    {
        using var engine = new WasapiVoiceAudioEngine(new VoiceAudioConfiguration());
        var events = new List<VoiceAudioEvent>();
        engine.Handlers = new VoiceAudioHandlers { Event = events.Add };
        var handler = typeof(WasapiVoiceAudioEngine).GetMethod("HandleWorkerFailure", Fields)!;
        for (var index = 0; index < 4; index++)
        {
            typeof(WasapiVoiceAudioEngine).GetField("_binding", Fields)!.SetValue(engine, new AudioRouteBinding(null, null, "mic", "output"));
            handler.Invoke(engine, ["capture", new COMException("invalidated")]);
        }
        Assert.Single(events, item => item is VoiceAudioEvent.Broken);
    }
#pragma warning restore CA2201
}
