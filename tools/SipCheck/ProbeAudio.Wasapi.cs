using EliteSIP.Audio;

namespace EliteSIP.Tools.SipCheck;

/// <summary>Движок тракта Windows. Linux-сборка стенда ставит на место этого файла свой.</summary>
internal static partial class ProbeAudio
{
    private static partial IVoiceAudioEngine Open(VoiceAudioConfiguration configuration) =>
        new WasapiVoiceAudioEngine(configuration);
}
