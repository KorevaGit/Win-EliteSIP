using EliteSIP.Audio;

namespace EliteSIP.Tools.SipCheck;

/// <summary>
/// Общий тракт разговора — тот же, что будет у приложения.
///
/// Движок устройства стенд не выбирает сам: его даёт файл платформы
/// (<c>ProbeAudio.Wasapi.cs</c> здесь, свой файл в Linux-репозитории, который
/// собирает эти же исходники). Частичный метод, а не настройка во время
/// работы: без реализации стенд не соберётся, и забыть подставить движок
/// нельзя, а Linux-сборке не нужно ни подменять имя класса, ни тянуть WASAPI.
/// </summary>
internal static partial class ProbeAudio
{
    public static VoiceAudioBus MakeBus()
    {
        VoiceAudioConfiguration configuration = new();
        return new VoiceAudioBus(Open(configuration), Open);
    }

    /// <summary>Открывает движок тракта платформы.</summary>
    private static partial IVoiceAudioEngine Open(VoiceAudioConfiguration configuration);
}
