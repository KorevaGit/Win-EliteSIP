using System.IO;
using System.Text;

namespace EliteSIP.Audio;

/// <summary>Запись WAV: PCM, моно, 16 бит.</summary>
///
/// <remarks>
/// Своя, а не из NAudio: там она тянет за собой поток формата и пишет
/// расширенный заголовок, который часть проигрывателей и мессенджеров не
/// понимает. Здесь — классические 44 байта, которые открывает что угодно.
/// </remarks>
public static class WaveFile
{
    /// <summary>Пишет отсчёты в файл, заменяя его, если он есть.</summary>
    public static void WriteMono16(string path, int sampleRate, IReadOnlyList<short> samples)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(file, sampleRate, samples);
    }

    /// <summary>То же в поток — для проверок.</summary>
    public static void Write(Stream stream, int sampleRate, IReadOnlyList<short> samples)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(samples);

        const short Channels = 1;
        const short BitsPerSample = 16;
        const short BlockAlign = Channels * BitsPerSample / 8;
        int dataBytes = samples.Count * BlockAlign;

        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write(Channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * BlockAlign);
        writer.Write(BlockAlign);
        writer.Write(BitsPerSample);

        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }
    }
}
