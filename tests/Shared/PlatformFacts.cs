namespace EliteSIP.Tests;

/// <summary>
/// Проверка, которая имеет смысл только на Windows.
///
/// <b>Почему атрибут, а не <c>if (!OperatingSystem.IsWindows()) return;</c></b>
/// в начале проверки. Так было до 20 октября 2026, и на Linux такие проверки
/// честно отчитывались «пройдено», ничего не проверив: защита секретов и права
/// файла истории выглядели зелёными на системе, где их не было вовсе. Атрибут
/// ставит проверку в «пропущено» с причиной — отчёт говорит правду.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "только Windows";
        }
    }
}

/// <summary>Проверка, которая имеет смысл только вне Windows. См. <see cref="WindowsFactAttribute"/>.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "только Linux и macOS";
        }
    }
}
