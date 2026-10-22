using System.Reflection;

namespace EliteSIP.App.Platform;

/// <summary>
/// Версия приложения — та, что у сборки приложения, а не у <c>App.Core</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// До 22 октября 2026 линия обновлений, заголовок <c>X-EliteSIP-App</c> и
/// строка «Версия» в настройках брали версию у своей сборки. Пока приложение
/// было одно, это была одна и та же версия. Linux-версия собирает
/// <c>App.Core</c> из подмодуля — с версией Windows, — а выпуски у неё свои:
/// Linux 0.1.3 сравнивался бы с «установленной» 0.1.78 и не встал бы никогда.
/// </para>
/// <para>
/// Сборка приложения — входная, если её имя начинается с <c>EliteSIP.App</c>
/// (<c>EliteSIP.App</c> на Windows, <c>EliteSIP.App.Linux</c>). Иначе —
/// проверки, стенды — своя: там версия приложения ни при чём.
/// </para>
/// </remarks>
public static class ProductVersion
{
    private static readonly Assembly Product =
        Assembly.GetEntryAssembly() is { } entry
            && entry.GetName().Name?.StartsWith("EliteSIP.App", StringComparison.Ordinal) == true
            ? entry
            : typeof(ProductVersion).Assembly;

    /// <summary>Версия выпуска: с ней сравнивается манифест.</summary>
    public static Version Current { get; } = Product.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Осведомительная версия без хвоста после «+».
    /// </summary>
    ///
    /// <remarks>
    /// Сборка кладёт в осведомительную версию отпечаток последнего коммита, и
    /// человек по телефону читал бы поддержке сорок знаков шестнадцатеричного
    /// мусора вместо «ноль один один».
    /// </remarks>
    public static string Display
    {
        get
        {
            var full = Product
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrEmpty(full))
            {
                return "—";
            }

            var plus = full.IndexOf('+', StringComparison.Ordinal);

            return plus < 0 ? full : full[..plus];
        }
    }
}
