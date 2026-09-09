using System.Security.Cryptography;

namespace EliteSIP.SipCore;

/// <summary>
/// Сверка сертификата с прописанным отпечатком.
/// </summary>
///
/// <remarks>
/// Живёт в <c>SipCore</c>, а не в транспорте, по той же причине, что и тексты
/// отказов: это чистая функция, и здесь её видно тестам. Ошибка в ней не
/// выглядит ошибкой — она выглядит как «TLS работает», а на деле принимает чужой
/// сертификат, и поймать это без проверки нечем.
/// </remarks>
public static class SipTlsPinning
{
    /// <summary>Отпечаток SHA-256 сертификата в виде, в котором его сравнивают.</summary>
    ///
    /// <remarks>
    /// По байтам самого сертификата (DER), а не по цепочке: у самоподписанного
    /// цепочки нет, а подменивший сертификат подменит и её.
    /// </remarks>
    public static string Fingerprint(ReadOnlySpan<byte> derCertificate)
        => Convert.ToHexString(SHA256.HashData(derCertificate));

    /// <summary>Сходится ли отпечаток сертификата хоть с одним прописанным.</summary>
    ///
    /// <remarks>
    /// Написание не важно: отпечаток диктуют и вставляют с двоеточиями, с
    /// пробелами, в любом регистре. Требовать одного вида значит отказывать
    /// человеку, который всё сделал правильно, — а разбирать этот отказ он будет
    /// как «сертификат не подошёл».
    ///
    /// Пустой список не совпадает ни с чем. Это важнее, чем кажется: пиннинг
    /// заменяет системную проверку целиком, и «список пуст, значит пускаем
    /// всех» превратило бы забытую настройку в отключённую защиту.
    /// </remarks>
    public static bool Matches(IReadOnlySet<string> fingerprints, ReadOnlySpan<byte> derCertificate)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);

        if (fingerprints.Count == 0)
        {
            return false;
        }

        var actual = Fingerprint(derCertificate);

        foreach (var expected in fingerprints)
        {
            if (string.Equals(Normalized(expected), actual, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalized(string value)
        => value.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();
}
