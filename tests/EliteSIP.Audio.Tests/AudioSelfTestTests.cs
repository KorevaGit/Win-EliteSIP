namespace EliteSIP.Audio.Tests;

/// <summary>
/// Самопроверка звука: устройство занято только на время проверки, снимается
/// сразу и отпускает его до возврата, ползунки действуют на ходу.
/// </summary>
public sealed class AudioSelfTestTests
{
    [Fact]
    public async Task Снятие_возвращается_только_после_отпускания_устройства()
    {
        FakeVoiceAudioEngine engine = new();
        using AudioSelfTest test = new(_ => engine, duration: TimeSpan.FromSeconds(30));

        Task<AudioSelfTestResult> running = test.RunAsync(new VoiceAudioConfiguration());
        Assert.True(await WaitUntilAsync(() => engine.StartCount == 1));

        // Ради этого снятие и синхронное: звонок откроет то же устройство
        // следующей строкой, и тракт проверки к этому моменту обязан уйти.
        Assert.True(test.Abort());
        Assert.Equal(1, engine.StopCount);
        Assert.Equal(1, engine.DisposeCount);

        AudioSelfTestResult result = await running;
        Assert.False(result.IsSuccess);
        Assert.False(test.IsRunning);
    }

    [Fact]
    public void Снятие_без_проверки_ничего_не_делает()
    {
        using AudioSelfTest test = new(_ => new FakeVoiceAudioEngine());

        Assert.False(test.Abort());
        Assert.Null(test.TakeLevels());
    }

    [Fact]
    public async Task Ползунки_во_время_проверки_применяются_сразу_и_видны_на_шкале()
    {
        FakeVoiceAudioEngine engine = new() { Levels = new AudioLevels(0.4f, 0.2f) };
        using AudioSelfTest test = new(_ => engine, duration: TimeSpan.FromSeconds(30));

        Task<AudioSelfTestResult> running = test.RunAsync(new VoiceAudioConfiguration());
        Assert.True(await WaitUntilAsync(() => engine.StartCount == 1));

        test.Apply(new VoiceAudioConfiguration { MicrophoneGain = 1.5f });
        Assert.Equal(1.5f, engine.LastApplied?.MicrophoneGain);
        Assert.Equal(new AudioLevels(0.4f, 0.2f), test.TakeLevels());

        test.Abort();
        await running;

        // После проверки шкалам читать нечего — и тракта у неё нет.
        Assert.Null(test.TakeLevels());
    }

    [Fact]
    public async Task Записанное_проигрывается_тем_же_трактом()
    {
        FakeVoiceAudioEngine engine = new();
        using AudioSelfTest test = new(_ => engine, duration: TimeSpan.FromMilliseconds(200));

        Task<AudioSelfTestResult> running = test.RunAsync(new VoiceAudioConfiguration());
        Assert.True(await WaitUntilAsync(() => engine.Handlers.EncodedFrame is not null));

        byte[] frame = [1, 2, 3];
        engine.Handlers.EncodedFrame!(frame);

        Assert.True(await WaitUntilAsync(() => engine.Handlers.NeedsFrame is not null));
        PlaybackFrame? played = engine.Handlers.NeedsFrame!();

        Assert.NotNull(played);
        Assert.Equal(frame, played.Value.Payload.ToArray());
        Assert.True((await running).IsSuccess);
        Assert.Equal(1, engine.DisposeCount);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }
}
