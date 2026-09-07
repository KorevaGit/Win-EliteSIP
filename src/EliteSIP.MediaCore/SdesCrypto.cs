using System.Security.Cryptography;

namespace EliteSIP.MediaCore;

/// <summary>
/// Единственный профиль SDES, который нужен <c>chan_sip</c> Asterisk 13.
///
/// Он использует AES-128 в режиме counter для шифрования и HMAC-SHA1,
/// усечённый до 80 бит, для аутентификации каждого RTP-пакета.
/// </summary>
public enum SrtpCryptoSuite
{
    AesCm128HmacSha1Tag80,
}

public static class SrtpCryptoSuiteInfo
{
    public const string SuiteName = "AES_CM_128_HMAC_SHA1_80";

    public static int MasterKeyByteCount(this SrtpCryptoSuite suite) => 16;

    public static int MasterSaltByteCount(this SrtpCryptoSuite suite) => 14;

    public static int AuthenticationTagByteCount(this SrtpCryptoSuite suite) => 10;

    public static string Name(this SrtpCryptoSuite suite) => SuiteName;

    public static SrtpCryptoSuite? FromName(string name) =>
        name == SuiteName ? SrtpCryptoSuite.AesCm128HmacSha1Tag80 : null;
}

/// <summary>Ключ SRTP не той длины.</summary>
public sealed class SrtpKeyLengthException : Exception
{
    public SrtpKeyLengthException(int byteCount)
        : base($"Ключ SRTP должен занимать {SrtpMasterKey.ByteCount} байт, получено {byteCount}.") =>
        ByteCount = byteCount;

    public SrtpKeyLengthException()
        : this(0)
    {
    }

    public SrtpKeyLengthException(string message)
        : base(message)
    {
    }

    public SrtpKeyLengthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int ByteCount { get; }
}

/// <summary>
/// 128-битный master key и 112-битный master salt в форме <c>inline:</c> из
/// SDES.
///
/// Само шифрование потока приезжает на этапе W11. Здесь только то, без чего не
/// работает согласование SDP: разбор, сборка и длина ключа.
/// </summary>
public sealed class SrtpMasterKey : IEquatable<SrtpMasterKey>
{
    public const int ByteCount = 30;

    private readonly byte[] _bytes;

    public SrtpMasterKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount)
        {
            throw new SrtpKeyLengthException(bytes.Length);
        }

        _bytes = bytes.ToArray();
    }

    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>
    /// Ключ обязан быть криптографически случайным: на нём стоит вся защита
    /// потока, и предсказуемый источник обесценивает её целиком.
    /// </summary>
    public static SrtpMasterKey Random() => new(RandomNumberGenerator.GetBytes(ByteCount));

    internal ReadOnlyMemory<byte> EncryptionKey => _bytes.AsMemory(0, 16);

    internal ReadOnlyMemory<byte> Salt => _bytes.AsMemory(16);

    public bool Equals(SrtpMasterKey? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as SrtpMasterKey);

    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }
}

/// <summary>Значение строки <c>a=crypto</c> по RFC 4568.</summary>
public sealed record SdesCryptoLine(uint Tag, SrtpCryptoSuite Suite, SrtpMasterKey Key)
{
    private const string InlinePrefix = "inline:";

    public SdesCryptoLine(SrtpMasterKey key)
        : this(1, SrtpCryptoSuite.AesCm128HmacSha1Tag80, key)
    {
    }

    public static SdesCryptoLine? Parse(string value)
    {
        string[] fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3
            || !uint.TryParse(fields[0], out uint tag)
            || tag == 0
            || tag > int.MaxValue
            || SrtpCryptoSuiteInfo.FromName(fields[1]) is not SrtpCryptoSuite suite
            || !fields[2].StartsWith(InlinePrefix, StringComparison.Ordinal)
            || fields[2].Contains('|', StringComparison.Ordinal))
        {
            return null;
        }

        // Lifetime, MKI и ослабляющие session parameters не поддерживаем:
        // проигнорировать их означало бы согласиться на семантику, которую
        // криптографический контекст не выполняет.
        // Буфер ровно в длину ключа: ключ длиннее не поместится и разбор
        // отвергнет его, а не молча обрежет до правильного размера.
        byte[] bytes = new byte[SrtpMasterKey.ByteCount];
        string keyParameters = fields[2][InlinePrefix.Length..];
        if (!Convert.TryFromBase64String(keyParameters, bytes, out int written)
            || written != SrtpMasterKey.ByteCount)
        {
            return null;
        }

        return new SdesCryptoLine(tag, suite, new SrtpMasterKey(bytes));
    }

    public string Value => $"{Tag} {Suite.Name()} {InlinePrefix}{Convert.ToBase64String(Key.Bytes.Span)}";
}

/// <summary>Результат согласования защиты медиа.</summary>
public sealed record MediaSecurity
{
    private MediaSecurity(SrtpMasterKey? local, SrtpMasterKey? remote)
    {
        LocalKey = local;
        RemoteKey = remote;
    }

    /// <summary>Поток идёт открытым RTP.</summary>
    public static MediaSecurity None { get; } = new(null, null);

    /// <summary>
    /// Наш ключ, если поток защищён.
    ///
    /// Нужен пересогласованию: отвечая на повторный INVITE, ключ надо
    /// повторить, а не выпустить новый. Новый означал бы пересборку потока на
    /// каждое удержание — со сменой SSRC и слышимым разрывом на ровном месте.
    /// </summary>
    public SrtpMasterKey? LocalKey { get; }

    public SrtpMasterKey? RemoteKey { get; }

    public bool IsEncrypted => LocalKey is not null;

    public static MediaSecurity Sdes(SrtpMasterKey local, SrtpMasterKey remote) => new(local, remote);
}
