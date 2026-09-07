using System.Security.Cryptography;

namespace EliteSIP.SipCore;

/// <summary>
/// Генераторы одноразовых идентификаторов SIP.
///
/// Все токены обязаны быть непредсказуемыми: call-id и tag на публичном
/// интерфейсе — это часть защиты от подмешивания чужих сообщений в диалог.
/// Поэтому берём криптографический ГПСЧ, а не <see cref="Random"/>.
/// </summary>
public static class SipToken
{
    /// <summary>
    /// RFC 3261 §8.1.1.7: branch на новой транзакции обязан начинаться с этой
    /// строки — по ней сервер отличает клиента, соблюдающего RFC 3261, от
    /// древних реализаций RFC 2543.
    /// </summary>
    public const string BranchMagicCookie = "z9hG4bK";

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Случайная строка из букв и цифр — безопасна во всех позициях, где SIP
    /// ждёт token, и не требует экранирования.
    /// </summary>
    public static string Random(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return RandomNumberGenerator.GetString(Alphabet, length);
    }

    /// <summary>Значение для <c>Via: ...;branch=</c>.</summary>
    public static string Branch() => BranchMagicCookie + Random(16);

    /// <summary>
    /// Значение для заголовка <c>Call-ID</c>.
    ///
    /// Хост-часть опциональна: RFC её разрешает, но она раскрывает внутреннее
    /// имя машины, поэтому по умолчанию не добавляем.
    /// </summary>
    public static string CallId(string? host = null) =>
        string.IsNullOrEmpty(host) ? Random(32) : $"{Random(24)}@{host}";

    /// <summary>Значение для параметра <c>tag=</c> в From и To.</summary>
    public static string Tag() => Random(12);
}
