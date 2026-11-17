using NAudio.Wave;

namespace EliteSIP.Audio.Tests;

public sealed class AudioDriverIsolationTests
{
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Receive_only_clock_keeps_twenty_millisecond_packets_across_irregular_buffers(int rate)
    {
        var clock = new ReceiveOnlyClock(rate, 20);
        var packets = 0;
        var remaining = rate * 10;
        while (remaining > 0)
        {
            var frames = Math.Min(137, remaining);
            packets += clock.Advance(frames);
            remaining -= frames;
        }
        Assert.Equal(500, packets);
        Assert.Equal(0, clock.Advance(0));
    }

    [Fact]
    public void Missing_microphone_return_retries_once_per_route_generation()
    {
        var route = new AudioRouteBinding(null, null, null, "speakers");
        var change = new AudioDeviceChange(AudioDeviceChangeKind.DeviceAdded, "usb-mic", AudioDeviceDirection.Capture, null);
        Assert.True(DeviceChangeRelevance.AffectsRoute(change, route));
        Assert.True(DeviceChangeRelevance.StillRequiresRebuild(change, route, route));
        Assert.False(DeviceChangeRelevance.StillRequiresRebuild(change, route, route with { }));
        Assert.False(DeviceChangeRelevance.AffectsRoute(change with { Direction = AudioDeviceDirection.Render }, route));
    }

    [Fact]
    public async Task Stop_does_not_wait_for_opening_driver_and_late_open_never_plays()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var output = new BlockedOutput(entered, release);
        using var player = new SignalSoundPlayer { OutputFactory = _ => output };
        player.StartRingback(null);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try { await Task.Run(player.Stop).WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { release.Set(); }
        await output.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, output.PlayCount);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task Self_test_cancel_and_late_completion_survive_disposal_timeout()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = new FakeVoiceAudioEngine { BeforeStart = () => { entered.Set(); release.Wait(); } };
        var test = new AudioSelfTest(_ => engine);
        var running = test.RunAsync(new VoiceAudioConfiguration());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            Assert.True(await Task.Run(test.RequestAbort).WaitAsync(TimeSpan.FromMilliseconds(500)));
            await Task.Run(test.Dispose).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, engine.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => test.RunAsync(new VoiceAudioConfiguration()));
    }

    private sealed class BlockedOutput(ManualResetEventSlim entered, ManualResetEventSlim release) : ISignalOutput
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PlayCount { get; private set; }
        public void Init(ISampleProvider provider) { entered.Set(); release.Wait(); }
        public void Play() => PlayCount++;
        public void Stop() { }
        public void Dispose() => Disposed.TrySetResult();
    }
}
