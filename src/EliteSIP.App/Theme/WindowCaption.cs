using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EliteSIP.App.Theme;

/// <summary>
/// Красит полосу заголовка обычного окна в тон выбранной темы.
/// </summary>
///
/// <remarks>
/// В оригинале этого не было и быть не могло: на macOS полоса заголовка берёт
/// оформление у окна, а окно — у приложения (<c>NSApp.appearance</c>), и один
/// вызов красит всё.
///
/// На Windows полоса принадлежит системе и живёт по системной теме, а не по
/// нашей. Живая проверка это и показала: настройки со светлой палитрой
/// открывались с чёрной полосой заголовка — окно выглядело собранным из двух
/// половин. Панель этой беды не знает, потому что рисует полосу сама.
///
/// Просить систему приходится через DWM. Номер свойства менялся: до Windows 10
/// 1903 это 19, начиная с неё — 20. Пробуем сначала нынешний, потом прежний;
/// отказ не проверяется намеренно — на системе, которая не понимает ни того ни
/// другого, полоса просто остаётся системной.
/// </remarks>
internal static class WindowCaption
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore1903 = 19;

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    /// <summary>Ставит окну тёмную или светлую полосу заголовка.</summary>
    public static void Apply(Window window, bool isDark)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            // Окно ещё не создано в системе — красить нечего. Зовущий обязан
            // повторить в `OnSourceInitialized`.
            return;
        }

        var value = isDark ? 1 : 0;
        if (DwmSetWindowAttribute(source.Handle, UseImmersiveDarkMode, ref value, sizeof(int)) == 0)
        {
            return;
        }

        // Отказ и второй попытки не проверяется намеренно: на системе, которая
        // не знает ни нынешнего номера свойства, ни прежнего, полоса остаётся
        // системной — а это ровно то, что там и рисуется у всех окон.
        _ = DwmSetWindowAttribute(source.Handle, UseImmersiveDarkModeBefore1903, ref value, sizeof(int));
    }
}
