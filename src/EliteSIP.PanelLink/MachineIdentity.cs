using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;

namespace EliteSIP.PanelLink;

/// <summary>
/// Ключ машины: закрытый X25519, которым она открывает свою конфигурацию.
/// </summary>
///
/// <remarks>
/// <para>
/// Создаётся на машине один раз, в мастере, и стирается только сбросом.
/// Открытая половина уходит в Spark при открытии сессии привязки (или при
/// регистрации машины, поднятой ключом); закрытая не покидает машину никогда —
/// поэтому Spark может выложить номер и SIP-пароль на общий сервер, и никто,
/// кроме этой машины, их не прочтёт.
/// </para>
/// <para>
/// X25519 из BouncyCastle: в .NET кривой 25519 нет, а BouncyCastle уже стоит
/// здесь ради Ed25519. NSec отвергнут ещё на W10 — он тянет нативную libsodium.
/// </para>
/// </remarks>
public sealed class MachineKeyPair
{
    /// <summary>Длина ключей X25519 — и закрытого, и открытого.</summary>
    public const int KeySize = 32;

    private readonly X25519PrivateKeyParameters _private;

    private MachineKeyPair(X25519PrivateKeyParameters privateKey)
    {
        _private = privateKey;
        PublicKey = privateKey.GeneratePublicKey().GetEncoded();
    }

    /// <summary>Открытая половина, 32 байта. Её и знает Spark.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Открытая половина в base64 — так её принимает Spark.</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

    /// <summary>Новый ключ из криптографически случайного источника.</summary>
    public static MachineKeyPair Generate()
        => new(new X25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(KeySize)));

    /// <summary>Ключ из сохранённых 32 байт.</summary>
    public static MachineKeyPair FromPrivate(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeySize)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedKey);
        }

        return new(new X25519PrivateKeyParameters(privateKey.ToArray()));
    }

    /// <summary>Ключ из сохранённой строки base64.</summary>
    public static MachineKeyPair FromBase64(string privateKey)
    {
        try
        {
            return FromPrivate(Convert.FromBase64String(privateKey));
        }
        catch (FormatException error)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedKey, error);
        }
    }

    /// <summary>Закрытая половина в base64 — для хранения под DPAPI.</summary>
    public string PrivateKeyBase64 => Convert.ToBase64String(_private.GetEncoded());

    /// <summary>Общий секрет с чужим открытым ключом (X25519).</summary>
    internal byte[] Agree(ReadOnlySpan<byte> peerPublicKey)
    {
        var shared = new byte[KeySize];
        _private.GenerateSecret(new X25519PublicKeyParameters(peerPublicKey.ToArray()), shared, 0);
        return shared;
    }
}

/// <summary>
/// Ключ канала: пароль машины к серверу обновлений.
/// </summary>
///
/// <remarks>
/// 32 случайных байта строкой base64url без «=», как на macOS. Машина
/// предъявляет его парой Basic <c>installation_id:ключ</c> и за конфигурацией,
/// и за предустановками, и за отзывом. Spark получает только SHA-256 от этой
/// строки — сам ключ с машины не уходит ни разу.
/// </remarks>
public static class ChannelKey
{
    /// <summary>Новый ключ канала.</summary>
    public static string Generate()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    /// SHA-256 от строки ключа, шестнадцатеричный в нижнем регистре — поле
    /// <c>channel_key_hash</c> сессии привязки.
    /// </summary>
    public static string Hash(string channelKey)
    {
        ArgumentNullException.ThrowIfNull(channelKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(channelKey)));
    }
}
