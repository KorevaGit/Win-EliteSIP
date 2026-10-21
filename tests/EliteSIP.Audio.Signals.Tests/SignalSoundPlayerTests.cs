using NAudio.Wave;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Проигрыватель служебных звуков без устройства.
/// </summary>
///
/// <remarks>
/// Вывод подменён немым, который только запоминает, что ему дали, и вычитывает
/// из источника кусок — так видно, что источник собран и звучит, а не только
/// создан. Файлы WAV разбирает <see cref="WaveFileReader"/>: MP3 зависит от
/// ОС и проверяется у каждой своей платформы.
/// </remarks>
public sealed class SignalSoundPlayerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EliteSIP.Signals.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly List<SilentOutput> _outputs = [];
    private readonly SignalSoundPlayer _player;

    public SignalSoundPlayerTests()
    {
        Directory.CreateDirectory(_root);
        _player = new SignalSoundPlayer(
            new SignalOutputs(
                deviceId =>
                {
                    SilentOutput output = new(deviceId);
                    _outputs.Add(output);
                    return output;
                },
                path => new WaveFileReader(path)),
            _log.Add);
    }

    public void Dispose()
    {
        _player.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Гудки_идут_на_выбранное_устройство_и_снимаются()
    {
        _player.StartRingback("гарнитура");

        var output = Assert.Single(_outputs);
        Assert.Equal("гарнитура", output.DeviceId);
        Assert.True(output.HeardSound);
        Assert.True(_player.IsPlaying);

        _player.StopRingback();

        Assert.False(_player.IsPlaying);
        Assert.True(output.Disposed);
    }

    [Fact]
    public void Отбой_гудков_не_снимает_рингтон()
    {
        _player.StartRingtone(null, 0.5, null);
        _player.StopRingback();

        Assert.True(_player.IsPlaying);
    }

    [Fact]
    public void Свой_WAV_читается()
    {
        var path = Path.Combine(_root, "ring.wav");
        WriteWav(path);

        Assert.True(_player.PreviewRingtone(path, 0.0, deviceId: null));
        Assert.DoesNotContain(_log, line => line.Contains("не читается", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("broken.ogg")]
    [InlineData("broken.wav")]
    public void Испорченный_файл_уходит_на_стандартный_и_говорит_об_этом(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        Assert.False(_player.PreviewRingtone(path, 0.0, deviceId: null));
        Assert.Contains(_log, line => line.Contains("не читается", StringComparison.Ordinal));
        Assert.True(Assert.Single(_outputs).HeardSound);
    }

    [Fact]
    public void Клавиша_набора_звучит_а_буква_молчит()
    {
        _player.PlayKeyTone('A', null);
        Assert.Empty(_outputs);

        _player.PlayKeyTone('5', null);
        Assert.True(Assert.Single(_outputs).HeardSound);
    }

    /// <summary>Полсекунды тишины, 8 кГц, 16 бит, моно.</summary>
    internal static void WriteWav(string path)
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

    private sealed class SilentOutput(string? deviceId) : ISignalOutput
    {
        public string? DeviceId { get; } = deviceId;

        public bool HeardSound { get; private set; }

        public bool Disposed { get; private set; }

        public void Play(ISampleProvider source)
        {
            // Тон клавиши короче десятой секунды, тишина WAV — полсекунды:
            // десятой доли хватает, чтобы источник отдал хоть что-то.
            var buffer = new float[source.WaveFormat.SampleRate / 10];
            HeardSound = source.Read(buffer) > 0;
        }

        public void Stop()
        {
        }

        public void Dispose() => Disposed = true;
    }
}
