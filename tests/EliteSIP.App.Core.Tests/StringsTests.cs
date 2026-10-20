using System.Globalization;
using EliteSIP.App.Resources;

namespace EliteSIP.App.Tests;

/// <summary>
/// Подписи находятся там, где их ищут.
///
/// Заведено 20 октября 2026 вместе с переездом подписей в EliteSIP.App.Core.
/// Ресурсы ищутся по имени <c>EliteSIP.App.Resources.Strings</c>, а имя это
/// собирается из корневого пространства имён проекта. Смени его — и сборка
/// пройдёт, а приложение заговорит ключами вместо слов: «PanelHistory» на
/// кнопке, и только на живой машине.
/// </summary>
public sealed class StringsTests
{
    [Fact]
    public void Русская_подпись_находится()
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            Assert.Equal("История", Strings.Get("PanelHistory"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void Английская_подпись_находится()
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Assert.Equal("History", Strings.Get("PanelHistory"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
