using System.Reflection;

namespace EliteSIP.Audio.Tests;

public sealed class SignalSoundResponsivenessTests
{
    [Fact]
    public async Task Connected_cue_and_shutdown_do_not_wait_for_busy_cue_device()
    {
        var player = new SignalSoundPlayer();
        var gate = typeof(SignalSoundPlayer).GetField("_cueGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var driver = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(); } });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            await Task.Run(() =>
            {
                for (var i = 0; i < 100; i++) { player.PlayConnected(null); }
                player.Dispose();
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
        await driver;
    }
}
