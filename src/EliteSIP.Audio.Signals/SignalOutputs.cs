using NAudio.Wave;

namespace EliteSIP.Audio;

/// <summary>
/// Вывод служебного звука на устройство — то, чем <see cref="SignalSoundPlayer"/>
/// отличается на Windows и Linux.
/// </summary>
// CA1716 про Stop — то же решение, что у IVoiceAudioEngine и IUiTimer.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "продукт на C#; Stop — имя WasapiPlayer.Stop и потока Pulse")]
public interface ISignalOutput : IDisposable
{
    /// <summary>Начать играть источник. Повторно не зовётся.</summary>
    public void Play(ISampleProvider source);

    /// <summary>Перестать играть. Исчезнувшее устройство — не ошибка.</summary>
    public void Stop();
}

/// <summary>Что проигрывателю нужно от ОС: вывод и разбор файла рингтона.</summary>
///
/// <param name="Open">
/// вывод на устройство по идентификатору; <see langword="null"/> — системное.
/// Исчезнувшее устройство — тоже системное: звонить им лучше, чем не звонить.
/// </param>
/// <param name="OpenFile">
/// читатель WAV и MP3. OGG разбирает сам проигрыватель (NVorbis одинаков на
/// обеих ОС), а MP3 — нет: декодер NAudio системный (ACM на Windows), на
/// Linux его нет.
/// </param>
public sealed record SignalOutputs(Func<string?, ISignalOutput> Open, Func<string, WaveStream> OpenFile);
