using EliteSIP.App.Shell;

namespace EliteSIP.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void Second_launch_only_wakes_owner_then_new_launch_can_claim_after_exit()
    {
        var scope = ".test." + Guid.NewGuid();
        using var woke = new ManualResetEventSlim();
        var first = SingleInstance.Claim(scope);
        Assert.NotNull(first);
        first.Watch(() => woke.Set());
        try
        {
            SingleInstance? second = null;
            var launch = new Thread(() => second = SingleInstance.Claim(scope));
            launch.Start();
            Assert.True(launch.Join(TimeSpan.FromSeconds(3)));
            Assert.Null(second);
            Assert.True(woke.Wait(TimeSpan.FromSeconds(3)));
        }
        finally { first.Dispose(); }
        using var next = SingleInstance.Claim(scope);
        Assert.NotNull(next);
    }
}
