namespace EliteSIP.App.Theme;

/// <summary>Оформление: светлое, тёмное или как в системе.</summary>
///
/// <remarks>
/// Три значения, а не тумблер «тёмная тема»: «как в системе» — не середина
/// между двумя другими, а отказ выбирать, и он же значение по умолчанию.
/// </remarks>
public enum Appearance
{
    System,
    Light,
    Dark,
}
