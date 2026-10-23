namespace EliteSIP.Audio.Tests;

public sealed class ConnectionToneTests
{
    [Fact]
    public void Подключение_короткое_тихое_и_без_щелчков()
    {
        var source = SignalSoundPlayer.CreateConnectedTone();
        Assert.Equal(48000, source.WaveFormat.SampleRate);
        Assert.Equal(1, source.WaveFormat.Channels);
        var samples = new List<float>();
        var buffer = new float[1024];
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            samples.AddRange(buffer.Take(count));
            Assert.True(samples.Count <= 24000, "Подсказка не должна продолжаться дольше полсекунды");
        }

        Assert.InRange((double)samples.Count / source.WaveFormat.SampleRate, 0.2, 0.5);
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample)));
        Assert.InRange(samples.Max(sample => Math.Abs(sample)), 0.015f, 0.04f);
        Assert.InRange(Math.Abs(samples[0]), 0f, 0.000001f);
        Assert.InRange(Math.Abs(samples[^1]), 0f, 0.000001f);
        for (int index = 1; index < samples.Count; index++)
        {
            Assert.InRange(Math.Abs(samples[index] - samples[index - 1]), 0f, 0.006f);
        }
        Assert.Equal(0, source.Read(buffer));
    }
}
