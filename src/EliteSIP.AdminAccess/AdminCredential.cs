using System.Text.Json.Serialization;

namespace EliteSIP.AdminAccess;

/// <summary>
/// Всё, что рабочее место помнит про административный пароль.
///
/// Одно значение: <see cref="LoginHash"/> — проверочное. По нему проверяется
/// вход, и восстановить из него пароль нельзя. Файл настроек уезжает в бэкапы,
/// и пароля в нём быть не должно.
///
/// <b>Кода восстановления здесь нет</b> (снят в оригинале 25 августа 2026). Он
/// держал рядом с хешем тот же пароль, зашифрованный шестизначным кодом, и код
/// этот лежал открытым текстом в бандле каждого приложения — то есть
/// «восстановление» работало у всякого, кто вскрыл <c>.app</c>. Забывший пароль
/// администратор смотрит в панель, откуда пароль и приезжает.
///
/// План этапа W7 всё ещё называет код восстановления частью работы: план
/// составлялся по аудиту, а код к тому времени был уже удалён. Переносить
/// нечего.
/// </summary>
public sealed class AdminCredential : IEquatable<AdminCredential>
{
    /// <summary>
    /// Итераций PBKDF2 для новых учётных данных. Подробности выбора — в
    /// <c>KeyDerivation</c>.
    /// </summary>
    public static int DefaultIterations => KeyDerivation.DefaultIterations;

    [JsonConstructor]
    public AdminCredential(int iterations, byte[] loginSalt, byte[] loginHash)
    {
        ArgumentNullException.ThrowIfNull(loginSalt);
        ArgumentNullException.ThrowIfNull(loginHash);

        Iterations = iterations;
        LoginSalt = loginSalt;
        LoginHash = loginHash;
    }

    /// <summary>
    /// Итераций PBKDF2. Хранится, а не берётся из константы: поднять цену
    /// перебора на новых установках нужно, не сломав старые.
    /// </summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; }

    /// <summary>Соль проверочного значения.</summary>
    [JsonPropertyName("loginSalt")]
    public byte[] LoginSalt { get; }

    /// <summary>PBKDF2 от пароля. Сам пароль отсюда не достаётся.</summary>
    [JsonPropertyName("loginHash")]
    public byte[] LoginHash { get; }

    /// <summary>Новые учётные данные для пароля.</summary>
    public static AdminCredential Create(string password, int iterations = KeyDerivation.DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            throw new AdminAccessException(AdminAccessFailure.EmptyPassword);
        }

        byte[] salt = KeyDerivation.RandomSalt();
        byte[] hash = KeyDerivation.Derive(password, salt, iterations);
        return new AdminCredential(iterations, salt, hash);
    }

    /// <summary>
    /// Тот ли это пароль.
    ///
    /// Отказ вывода ключа проглатывается в <c>false</c>: для вызывающего «не
    /// удалось проверить» и «не подошёл» — одно и то же закрытое окно, а два
    /// разных сообщения на одном экране только подсказывали бы подбирающему.
    /// </summary>
    public bool Matches(string password)
    {
        if (string.IsNullOrEmpty(password) || LoginSalt.Length == 0 || LoginHash.Length == 0)
        {
            return false;
        }

        byte[] candidate;
        try
        {
            candidate = KeyDerivation.Derive(
                password,
                LoginSalt,
                // Число из файла — см. `KeyDerivation.BoundedIterations`. Без
                // границ одна правка в JSON вешает вход намертво, и выглядит это
                // как зависшее приложение, а не как испорченный файл.
                KeyDerivation.BoundedIterations(Iterations),
                LoginHash.Length);
        }
        catch (AdminAccessException)
        {
            return false;
        }

        return KeyDerivation.ConstantTimeEquals(candidate, LoginHash);
    }

    /// <summary>
    /// Равенство по содержимому, а не по ссылке: учётные данные проходят через
    /// запись и чтение файла настроек, и сравнивают их именно так.
    /// </summary>
    public bool Equals(AdminCredential? other)
        => other is not null
            && Iterations == other.Iterations
            && LoginSalt.AsSpan().SequenceEqual(other.LoginSalt)
            && LoginHash.AsSpan().SequenceEqual(other.LoginHash);

    public override bool Equals(object? obj) => Equals(obj as AdminCredential);

    /// <summary>
    /// Хеш-код берётся от итераций и длин, а не от самих байтов: тип попадает в
    /// словари только вместе с полным сравнением, а раскладывать секрет по
    /// корзинам незачем.
    /// </summary>
    public override int GetHashCode() => HashCode.Combine(Iterations, LoginSalt.Length, LoginHash.Length);

    public static bool operator ==(AdminCredential? left, AdminCredential? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(AdminCredential? left, AdminCredential? right) => !(left == right);
}
