using System.Windows.Markup;

namespace EliteSIP.App.Resources;

// Подписи в разметке WPF. Сами подписи — Strings в EliteSIP.App.Core: с
// 20 октября 2026 их читают и модели окон, и Linux-версия.

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
