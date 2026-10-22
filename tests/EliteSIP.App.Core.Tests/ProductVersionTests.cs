using EliteSIP.App.Platform;

namespace EliteSIP.App.Tests;

/// <summary>
/// Версия приложения. Входная сборка проверок — не приложение, поэтому здесь
/// видна только запасная ветка; сама подмена видна на Linux-сборке, где
/// <c>App.Core</c> и приложение собраны с разными версиями.
/// </summary>
public sealed class ProductVersionTests
{
    [Fact]
    public void Вне_приложения_версия_своей_сборки()
    {
        Assert.Equal(typeof(ProductVersion).Assembly.GetName().Version, ProductVersion.Current);
    }

    [Fact]
    public void Отпечаток_коммита_не_показывается()
    {
        Assert.DoesNotContain('+', ProductVersion.Display);
    }
}
