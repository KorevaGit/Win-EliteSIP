using System.Windows.Media;
using Microsoft.Win32;

namespace EliteSIP.App.Theme;

/// <summary>Акцентный цвет Windows и производные от него кисти.</summary>
///
/// <remarks>
/// На macOS этого не требовалось: там системный акцент — это <c>NSColor
/// .controlAccentColor</c>, и он сам подставляется в кнопки. В WPF системного
/// акцента нет вовсе (<c>SystemColors</c> застыли на временах Windows 7), и
/// цвет приходится читать из реестра самому.
///
/// Читается <c>AccentPalette</c>, а не <c>DWM\AccentColor</c>: система хранит
/// там же готовые осветлённые и затемнённые ступени того же цвета, и на тёмном
/// фоне нужна именно ступень, а не базовый цвет — фиолетовый #6B69D6 на
/// #202020 читается как грязное пятно, его же ступень #A6A4F0 читается как
/// цвет. Сама Windows в тёмной теме показывает ровно эти ступени.
///
/// Фон окна не заливается акцентом, а подкрашивается им: сплошной акцент за
/// текстом разговора — это не оформление, а нечитаемое окно. Восьми процентов
/// хватает, чтобы чёрный перестал быть чёрным, и мало, чтобы что-то испортить.
/// </remarks>
internal static class SystemAccent
{
    /// <summary>Ступени <c>AccentPalette</c>: по четыре байта BGRA на каждую.</summary>
    ///
    /// <remarks>
    /// Порядок ступеней — от самой светлой к самой тёмной, восемь штук; третья
    /// (индекс 3) и есть то, что «Параметры» показывают как выбранный цвет, а
    /// дальше идут затемнения. Для тёмной темы нужна ступень выше — индекс 2:
    /// ровно её Windows показывает в тёмной теме как «ваш цвет».
    ///
    /// Проверено на живом ключе рядом с `DWM\AccentColor` = #0078D7: ступени
    /// идут #A6D8FF, #76B9ED, #429CE3, #0078D7, #005A9E — сверху вниз.
    /// </remarks>
    private const int BaseStep = 3;
    private const int LightStep = 2;

    /// <summary>Акцент системы под нужную палитру. <c>null</c> — прочитать не вышло.</summary>
    internal static Color? Read(bool dark)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");

        if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 32)
        {
            var step = dark ? LightStep : BaseStep;
            var at = step * 4;

            // RGBA, а не BGRA — в отличие от `DWM\AccentColor` ниже. Перепутанный
            // порядок стоил выпуска 0.1.45: синий #0078D7 читался как оранжевый
            // #D77800, и у оператора с чёрной темой и синим акцентом окно
            // выходило бежевым.
            return Color.FromRgb(palette[at], palette[at + 1], palette[at + 2]);
        }

        // Запасной путь: у DWM лежит тот же цвет, но одной ступенью — без
        // светлого и тёмного вариантов. Подгоняем сами.
        using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (dwm?.GetValue("AccentColor") is int packed)
        {
            // ABGR, а не ARGB: DWM хранит цвет в порядке, обратном привычному.
            var raw = unchecked((uint)packed);
            var color = Color.FromRgb((byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)((raw >> 16) & 0xFF));

            return dark ? Mix(color, Colors.White, 0.35) : color;
        }

        return null;
    }

    /// <summary>Смешивает два цвета: <paramref name="amount"/> — доля второго.</summary>
    internal static Color Mix(Color under, Color over, double amount)
        => Color.FromRgb(
            (byte)Math.Round(under.R + ((over.R - under.R) * amount)),
            (byte)Math.Round(under.G + ((over.G - under.G) * amount)),
            (byte)Math.Round(under.B + ((over.B - under.B) * amount)));
}
