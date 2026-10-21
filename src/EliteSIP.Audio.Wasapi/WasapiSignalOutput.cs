using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EliteSIP.Audio;

/// <summary>
/// Служебные звуки на Windows: вывод WASAPI и разбор файла через
/// <see cref="AudioFileReader"/> (MP3 — Media Foundation).
/// </summary>
///
/// <remarks>
/// До 21 октября 2026 это жило внутри <see cref="SignalSoundPlayer"/>; сам
/// проигрыватель уехал в общий <c>EliteSIP.Audio.Signals</c>, а сюда — то
/// единственное, что в нём было от Windows. Числа и порядок вызовов прежние.
/// </remarks>
public static class WasapiSignalSounds
{
    public static SignalOutputs Outputs { get; } = new(
        deviceId => new WasapiSignalOutput(deviceId),
        path => new AudioFileReader(path));

    /// <summary>Проигрыватель на WASAPI — то, что раньше давал <c>new SignalSoundPlayer(log)</c>.</summary>
    public static SignalSoundPlayer Create(Action<string>? log = null) => new(Outputs, log);
}

internal sealed class WasapiSignalOutput : ISignalOutput
{
    private readonly WasapiPlayer _player;

    internal WasapiSignalOutput(string? deviceId)
    {
        WasapiPlayerBuilder builder = new WasapiPlayerBuilder()
            .WithSharedMode()

            // Сотня миллисекунд, а не низкая задержка: служебному звуку
            // задержка безразлична, а низкая берётся не даром — MMCSS и
            // мелкий буфер здесь заняли бы то, что нужно разговору.
            .WithLatency(100)
            .WithEventSync();

        if (Device(deviceId) is { } device)
        {
            builder = builder.WithDevice(device);
        }

        _player = builder.Build();
    }

    public void Play(ISampleProvider source)
    {
        _player.Init(source);
        _player.Play();
    }

    public void Stop() => _player.Stop();

    public void Dispose() => _player.Dispose();

    /// <summary>Устройство по имени. <c>null</c> — играть системным.</summary>
    private static MMDevice? Device(string? deviceId)
    {
        if (deviceId is not { Length: > 0 })
        {
            return null;
        }

        using MMDeviceEnumerator enumerator = new();
        try
        {
            MMDevice device = enumerator.GetDevice(deviceId);

            // Устройства может уже не быть — гарнитуру выдернули между
            // настройкой и звонком. Звонить системным лучше, чем не звонить.
            return device.State is DeviceState.Active ? device : null;
        }
        catch (Exception error) when (error is ArgumentException
                                          or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
