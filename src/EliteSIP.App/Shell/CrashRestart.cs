using System.Runtime.InteropServices;

namespace EliteSIP.App.Shell;

/// <summary>
/// Перезапуск после падения на Windows. Что считать падением — решает
/// <see cref="CrashGuard"/> (App.Core); поднимает процесс система.
/// </summary>
internal static class CrashRestart
{
    /// <summary>
    /// Просит Windows поднять софтфон, если он всё-таки упал или завис.
    /// </summary>
    ///
    /// <remarks>
    /// Windows поднимает только процесс, проживший больше минуты, — так что
    /// падение на запуске в круг не уходит. Не поднимает после установки
    /// обновления и перезагрузки: первое делает обновляльщик, второе —
    /// автозапуск. Обычный выход и снятие задачи установщиком падением не
    /// считаются.
    ///
    /// Работает через отчёты об ошибках Windows; если они выключены групповой
    /// политикой, перезапуска не будет, и это стоит знать при разборе.
    /// </remarks>
    internal static void Register(Action<string> log)
    {
        const int restartNoPatch = 4;
        const int restartNoReboot = 8;

        var result = RegisterApplicationRestart("--after-crash", restartNoPatch | restartNoReboot);
        if (result != 0)
        {
            log($"перезапуск после падения не заказан: 0x{result:X8}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, int flags);
}
