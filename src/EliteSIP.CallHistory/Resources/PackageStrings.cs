using System.Diagnostics;
using System.Globalization;
using System.Resources;

namespace EliteSIP.CallHistory.Resources;

/// <summary>Подписи пакета: слова исходов и названия фильтров.</summary>
///
/// <remarks>
/// Свои, а не приложения, и это правило оригинала: пакет остаётся
/// самодостаточным — <c>CallHistory</c> сам подписывает исходы звонка,
/// <c>AdminAccess</c> сам подписывает свои отказы. Приложению не приходится
/// знать, какие у пакета бывают исходы, чтобы их назвать.
///
/// В оригинале это стоило отдельной возни: SwiftPM не умеет каталоги
/// <c>.xcstrings</c>, и их приходилось собирать скриптом в <c>ru.lproj</c> и
/// <c>en.lproj</c>, иначе в собранном пакете не оказывалось ничего и
/// <c>NSLocalizedString</c> возвращал ключ. В .NET спутниковые сборки собирает
/// сам SDK, и возни не остаётся никакой.
/// </remarks>
internal static class PackageStrings
{
    private static readonly ResourceManager Manager =
        new("EliteSIP.CallHistory.Resources.Strings", typeof(PackageStrings).Assembly);

    public static string Get(string key)
    {
        var value = Manager.GetString(key, CultureInfo.CurrentUICulture);
        Debug.Assert(value is not null, $"нет подписи {key}");
        return value ?? key;
    }
}
