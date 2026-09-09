using System.Diagnostics;
using System.Globalization;
using System.Resources;

namespace EliteSIP.CallGuard.Resources;

/// <summary>Подписи пакета: то, что защита говорит оператору при отказе.</summary>
///
/// <remarks>
/// Свои, а не приложения, и это правило оригинала: пакет остаётся
/// самодостаточным — он сам называет свои отказы, и приложению не приходится
/// знать, какими они бывают, чтобы их подписать.
/// </remarks>
internal static class PackageStrings
{
    private static readonly ResourceManager Manager =
        new("EliteSIP.CallGuard.Resources.Strings", typeof(PackageStrings).Assembly);

    public static string Get(string key)
    {
        var value = Manager.GetString(key, CultureInfo.CurrentUICulture);
        Debug.Assert(value is not null, $"нет подписи {key}");
        return value ?? key;
    }
}
