using EliteSIP.Audio;

namespace EliteSIP.App.Platform;

/// <summary>
/// Звук, который зависит от ОС: движок тракта, список устройств, сигналы.
/// </summary>
///
/// <remarks>
/// Телефон и окно настроек до 20 октября 2026 создавали WASAPI-движок,
/// <c>SignalSoundPlayer</c> и спрашивали <c>AudioDeviceCatalog</c> сами. Всё
/// остальное в звуке — шина, медиа-сессия, самопроверка — общее и живёт в
/// <c>EliteSIP.Audio</c>.
/// </remarks>
public interface IAudioPlatform
{
    /// <summary>Новый движок тракта под эти настройки. Устройство он открывает на <c>Start</c>.</summary>
    public IVoiceAudioEngine CreateEngine(VoiceAudioConfiguration configuration);

    /// <summary>Проигрыватель служебных звуков: гудки, отбой, рингтон.</summary>
    public ISignalSounds CreateSignalSounds(Action<string> log);

    /// <summary>Устройства, на которых сейчас можно говорить.</summary>
    public IReadOnlyList<AudioDevice> Devices(AudioDeviceDirection direction);

    /// <summary>Устройство, выбранное в системе по умолчанию.</summary>
    public AudioDevice? DefaultDevice(AudioDeviceDirection direction);
}
