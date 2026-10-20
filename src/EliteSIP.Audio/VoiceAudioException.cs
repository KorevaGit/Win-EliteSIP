namespace EliteSIP.Audio;

// Общая для всех движков: телефон ловит её, не зная, какой движок под ним.
// До 20 октября 2026 лежала рядом с WASAPI-движком.

/// <summary>Тракт не поднялся или развалился.</summary>
public sealed class VoiceAudioException : Exception
{
    public VoiceAudioException(string message)
        : base(message)
    {
    }

    public VoiceAudioException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public VoiceAudioException()
    {
    }
}
