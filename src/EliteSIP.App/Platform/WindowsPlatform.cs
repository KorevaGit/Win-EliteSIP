using EliteSIP.App.PanelLine;
using EliteSIP.App.Shell;
using EliteSIP.Audio;

namespace EliteSIP.App.Platform;

/// <summary>Звук Windows: WASAPI, MMDevice API, служебные звуки на NAudio.</summary>
internal sealed class WasapiAudioPlatform : IAudioPlatform
{
    public IVoiceAudioEngine CreateEngine(VoiceAudioConfiguration configuration) =>
        new WasapiVoiceAudioEngine(configuration);

    public ISignalSounds CreateSignalSounds(Action<string> log) => WasapiSignalSounds.Create(log);

    public IReadOnlyList<AudioDevice> Devices(AudioDeviceDirection direction) =>
        AudioDeviceCatalog.Devices(direction);

    public AudioDevice? DefaultDevice(AudioDeviceDirection direction) =>
        AudioDeviceCatalog.Default(direction);
}

/// <summary>Передача выпуска задаче планировщика от SYSTEM — <see cref="UpdateHandoff"/>.</summary>
internal sealed class WindowsUpdateHandoff : IUpdateHandoff
{
    public string UpdatesDirectory => UpdateHandoff.UpdatesDirectory;

    public bool Request(Version version, Action<string> log) => UpdateHandoff.Request(version, log);

    public bool Trigger(Action<string> log) => UpdateHandoff.Trigger(log);
}

/// <summary>Автозапуск через <c>HKCU\...\Run</c> — <see cref="AutoStart"/>.</summary>
internal sealed class WindowsAutoStart : IAutoStart
{
    public bool IsEnabled => AutoStart.IsEnabled;

    public bool TrySet(bool enabled) => AutoStart.Set(enabled);
}
