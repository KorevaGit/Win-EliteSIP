using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Рингтон и его прослушивание (0.1.68): свой файл, стандартный файл, отказ.
///
/// Вывод на устройство здесь не проверяется — его может не быть на машине
/// сборки, и проигрыватель в этом случае молча пишет в журнал. Проверяется
/// то, что решает, <i>что</i> зазвучит.
/// </summary>
public sealed class RingtoneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EliteSIP.Ringtone.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly SignalSoundPlayer _player;

    public RingtoneTests()
    {
        Directory.CreateDirectory(_root);
        _player = new SignalSoundPlayer(_log.Add);
    }

    public void Dispose()
    {
        _player.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Форматы_своего_рингтона_mp3_wav_ogg()
    {
        Assert.Equal([".wav", ".mp3", ".ogg"], SignalSoundPlayer.SupportedRingtoneExtensions);
    }

    [Fact]
    public void Стандартный_рингтон_лежит_рядом_с_программой()
    {
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "ringtone.wav"),
            SignalSoundPlayer.DefaultRingtonePath);
    }

    [Fact]
    public void Свой_WAV_читается()
    {
        var path = Path.Combine(_root, "ring.wav");
        WriteWav(path);

        Assert.True(_player.PreviewRingtone(path, 0.0, deviceId: null));
        Assert.DoesNotContain(_log, line => line.Contains("не читается", StringComparison.Ordinal));
        _player.StopPreview();
    }

    [Theory]
    [InlineData("broken.ogg")]
    [InlineData("broken.mp3")]
    [InlineData("broken.wav")]
    public void Испорченный_файл_уходит_на_стандартный_и_говорит_об_этом(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        Assert.False(_player.PreviewRingtone(path, 0.0, deviceId: null));
        Assert.Contains(_log, line => line.Contains("не читается", StringComparison.Ordinal));
        _player.StopPreview();
    }

    [Fact]
    public void Без_своего_файла_прослушивание_не_жалуется()
    {
        // До 0.1.68 при стандартном рингтоне прослушивание не играло ничего.
        Assert.True(_player.PreviewRingtone(null, 0.0, deviceId: null));
        _player.StopPreview();
    }

    /// <summary>Полсекунды тишины, 8 кГц, 16 бит, моно.</summary>
    private static void WriteWav(string path)
    {
        const int rate = 8000;
        const int samples = rate / 2;

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
    }
}
