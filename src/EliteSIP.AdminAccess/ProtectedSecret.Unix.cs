using System.Security.Cryptography;
using System.Text;

namespace EliteSIP.AdminAccess;

/// <summary>
/// Вторая половина <see cref="ProtectedSecret"/>: Linux и macOS.
///
/// <b>Почему не открытым текстом, как в оригинале.</b> Права <c>0600</c> на
/// файле настроек на Linux снова работают — от других пользователей машины они
/// защищают так же, как на macOS. Но файл настроек уезжает с машины целиком:
/// в архиве поддержки, в бэкапе профиля, в письме «посмотрите, что у меня не
/// так». На Windows пароль в таком файле бесполезен без DPAPI этой учётной
/// записи, и терять это свойство при переезде незачем. Поэтому секрет
/// шифруется, а ключ лежит отдельным файлом, который сам никуда не уезжает.
///
/// <b>Почему не Secret Service (libsecret).</b> Связка ключей на рабочем месте
/// оператора бывает закрыта: при автовходе её нечем открыть, и софтфон, не
/// получивший пароль SIP, не зарегистрировался бы вовсе. Ровно поэтому
/// оригинал не взял Keychain (<c>docs/profiles.md</c>).
///
/// <b>Ключ</b> — 32 случайных байта в <see cref="KeyDirectory"/>, файл
/// <c>0600</c> в каталоге <c>0700</c>. Шифр — AES-256-GCM; та же примесь, что у
/// DPAPI, идёт дополнительными данными: значение, взятое из другого места того
/// же пользователя, здесь не расшифруется. Значение — base64 от
/// <c>nonce (12) | tag (16) | шифр</c>.
///
/// <b>Потеря ключа</b> равна потере профиля на Windows: все секреты отвечают
/// <c>null</c>, и приложение просит ввести пароль заново. Подставлять новый
/// ключ под старые значения нельзя — они от этого не расшифруются, а «пароль
/// есть, но неверный» хуже, чем «пароля нет».
/// </summary>
public static partial class ProtectedSecret
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string KeyFileName = "secret.key";

    private static readonly Lock KeyLock = new();
    private static byte[]? _key;
    private static string _keyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EliteSIP");

    /// <summary>
    /// Где лежит ключ. По умолчанию — <c>~/.local/share/EliteSIP</c>, рядом с
    /// журналом, а не рядом с настройками: каталог настроек копируют целиком
    /// чаще, и ключ не должен уезжать вместе с ними.
    ///
    /// Меняется только проверками: смена сбрасывает прочитанный ключ.
    /// </summary>
    internal static string KeyDirectory
    {
        get
        {
            lock (KeyLock)
            {
                return _keyDirectory;
            }
        }

        set
        {
            lock (KeyLock)
            {
                _keyDirectory = value;
                _key = null;
            }
        }
    }

    private static string ProtectUnix(string secret)
    {
        byte[] key = LoadKey(create: true)
            ?? throw new CryptographicException("ключ секретов не удалось ни прочитать, ни создать: " + KeyDirectory);

        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        byte[] packed = new byte[NonceSize + TagSize + plaintext.Length];
        try
        {
            Span<byte> nonce = packed.AsSpan(0, NonceSize);
            Span<byte> tag = packed.AsSpan(NonceSize, TagSize);
            Span<byte> cipher = packed.AsSpan(NonceSize + TagSize);
            RandomNumberGenerator.Fill(nonce);

            using AesGcm aes = new(key, TagSize);
            aes.Encrypt(nonce, plaintext, cipher, tag, Entropy);
            return Convert.ToBase64String(packed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string? UnprotectUnix(byte[] packed)
    {
        if (packed.Length < NonceSize + TagSize)
        {
            return null;
        }

        // Ключа нет — значит, и секретов, им зашифрованных, здесь не было.
        // Заводить новый ради чтения бессмысленно: старое им не откроется.
        if (LoadKey(create: false) is not { } key)
        {
            return null;
        }

        byte[] plaintext = new byte[packed.Length - NonceSize - TagSize];
        try
        {
            using AesGcm aes = new(key, TagSize);
            aes.Decrypt(
                packed.AsSpan(0, NonceSize),
                packed.AsSpan(NonceSize + TagSize),
                packed.AsSpan(NonceSize, TagSize),
                plaintext,
                Entropy);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[]? LoadKey(bool create)
    {
        lock (KeyLock)
        {
            if (_key is not null)
            {
                return _key;
            }

            string path = Path.Combine(_keyDirectory, KeyFileName);
            _key = ReadKey(path) ?? (create ? CreateKey(path) : null);
            return _key;
        }
    }

    private static byte[]? ReadKey(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            byte[] key = File.ReadAllBytes(path);
            if (key.Length != KeySize)
            {
                return null;
            }

            // Кто-то расширил права руками — сужаем обратно. Ключ при этом уже
            // мог быть прочитан, и это не повод его выбросить: секреты от этого
            // не станут нечитаемыми для чужого, а для своего — станут.
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return key;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Новый ключ — сначала во временный файл <c>0600</c>, потом переносом на
    /// место. Перенос без перезаписи: если вторая копия приложения успела
    /// раньше, берём её ключ, а не затираем — иначе её только что записанный
    /// пароль перестал бы читаться.
    /// </summary>
    private static byte[]? CreateKey(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        string directory = Path.GetDirectoryName(path)!;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using (FileStream stream = new(temporary, options))
            {
                stream.Write(key);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, path, overwrite: false);
                return key;
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temporary);
                return ReadKey(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }
}
