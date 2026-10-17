using System.IO;

namespace EliteSIP.Audio.Tests;

/// <summary>Запись WAV для «Сохранить запись» проверки звука.</summary>
public sealed class WaveFileTests
{
    [Fact]
    public void Классический_заголовок_и_отсчёты_как_есть()
    {
        using MemoryStream stream = new();
        short[] samples = [0, 1000, -1000, short.MaxValue, short.MinValue];

        WaveFile.Write(stream, 8000, samples);
        byte[] bytes = stream.ToArray();

        Assert.Equal(44 + (samples.Length * 2), bytes.Length);
        Assert.Equal("RIFF"u8.ToArray(), bytes[0..4]);
        Assert.Equal(36 + (samples.Length * 2), BitConverter.ToInt32(bytes, 4));
        Assert.Equal("WAVEfmt "u8.ToArray(), bytes[8..16]);
        Assert.Equal(1, BitConverter.ToInt16(bytes, 20));      // PCM
        Assert.Equal(1, BitConverter.ToInt16(bytes, 22));      // моно
        Assert.Equal(8000, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(16000, BitConverter.ToInt32(bytes, 28));  // байт в секунду
        Assert.Equal(16, BitConverter.ToInt16(bytes, 34));
        Assert.Equal("data"u8.ToArray(), bytes[36..40]);
        Assert.Equal(samples.Length * 2, BitConverter.ToInt32(bytes, 40));

        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], BitConverter.ToInt16(bytes, 44 + (i * 2)));
        }
    }

    [Fact]
    public void Без_записи_сохранять_нечего()
    {
        using AudioSelfTest test = new(_ => throw new InvalidOperationException());

        Assert.False(test.HasRecording);
        Assert.False(test.SaveLastRecording(Path.Combine(Path.GetTempPath(), "never.wav")));
    }
}
