using System.Diagnostics;
using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace EliteSIP.App.Resources;

/// <summary>Подписи интерфейса. Русский — язык исходников, английский — запасной.</summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("EliteSIP.App.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>Подпись по имени ключа.</summary>
    public static string Get(string key)
    {
        var value = Manager.GetString(key, CultureInfo.CurrentUICulture);

        // Ненайденный ключ отдаётся своим же именем, а не пустотой: пустая
        // кнопка читается как «так задумано» и живёт в сборке месяцами, а
        // «PanelHistory» посреди панели видно с первого запуска.
        Debug.Assert(value is not null, $"нет подписи {key}");
        return value ?? key;
    }

    /// <summary>
    /// Выбирает язык интерфейса по правилу оригинала: русский там, где в
    /// системе есть русский, английский во всех остальных случаях.
    /// </summary>
    ///
    /// <param name="chosen">
    /// Выбор оператора в настройках: <c>null</c> — как в системе.
    /// </param>
    ///
    /// <remarks>
    /// Никакой третий язык не должен приводить к русскому интерфейсу — француз
    /// и японец видят английский, а не язык, который им ещё незнакомее.
    /// На macOS это держалось на запасном языке бандла; в .NET запасной — это
    /// нейтральный <c>Strings.resx</c>, то есть английский, и правило
    /// выполняется само. Русский включает только совпадение по языку, а не по
    /// стране: <c>ru-KZ</c> — тоже русский.
    /// </remarks>
    public static void Apply(CultureInfo? chosen)
    {
        var culture = chosen ?? CultureInfo.InstalledUICulture;

        // Задаётся именно UI-культура: форматы чисел и дат — это культура
        // текущая, и она остаётся системной. Оператор с русской Windows и
        // английским интерфейсом ждёт даты в своём виде, а не в чужом.
        CultureInfo.DefaultThreadCurrentUICulture =
            culture.TwoLetterISOLanguageName is "ru"
                ? CultureInfo.GetCultureInfo("ru")
                : CultureInfo.InvariantCulture;

        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture;
    }
}

/// <summary>Подпись из каталога прямо в разметке: <c>{res:Loc PanelHistory}</c>.</summary>
///
/// <remarks>
/// Расширение разметки, а не привязка к свойству класса-обёртки: обёртку в
/// SDK-проекте пришлось бы генерировать средствами Visual Studio, которой в
/// сборке этого проекта нет (см. W0 — всё собирается через <c>dotnet</c>).
/// Цена — ключ строкой, то есть опечатка, не видимая компилятору; ловится тем
/// же способом, что и у значка, — проверкой в отладочной сборке.
/// </remarks>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
