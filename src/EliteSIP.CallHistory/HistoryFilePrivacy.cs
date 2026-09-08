using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace EliteSIP.CallHistory;

/// <summary>
/// Закрывает файлы истории от всех, кроме владельца.
///
/// <b>Зачем это вообще.</b> В базе лежат номера лидов — те самые персональные
/// данные, ради которых у истории есть срок хранения и отдельная политика
/// удаления. В оригинале файл закрывался правами <c>0600</c>; на Windows их нет,
/// и умолчание — наследование прав от каталога, то есть чтение всем, кому
/// доступен каталог, плюс «Администраторы» как класс.
///
/// <b>Чем заменено.</b> Наследование снимается (<c>SetAccessRuleProtection(true,
/// false)</c> — не копируя унаследованное), после чего в списке остаётся ровно
/// одно правило: полный доступ владельцу. Это ближайший точный аналог
/// <c>0600</c>: администратор машины по-прежнему может взять файл, сменив
/// владельца, — ровно как <c>root</c> в оригинале.
///
/// <b>Три файла, а не один:</b> в режиме WAL рядом с базой живут <c>-wal</c> и
/// <c>-shm</c>, и данные в них те же самые. Отсутствующие молча пропускаются —
/// их SQLite ещё не создал либо уже убрал за собой.
/// </summary>
internal static class HistoryFilePrivacy
{
    /// <summary>Спутники базы в режиме WAL.</summary>
    internal static readonly string[] Companions = ["-wal", "-shm"];

    internal static void Restrict(string databasePath)
    {
        // Саму базу заводим сами: SQLite создал бы её с наследованными правами,
        // и между созданием и правкой списка существовала бы щель, в которую
        // файл читается. Спутники так не завести — их формат знает только
        // SQLite, — поэтому их права правятся после того, как он их создал.
        if (!File.Exists(databasePath))
        {
            using (File.Create(databasePath))
            {
            }
        }

        RestrictOne(databasePath);
        foreach (string suffix in Companions)
        {
            RestrictOne(databasePath + suffix);
        }
    }

    private static void RestrictOne(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            ApplyOwnerOnlyAcl(path);
        }
        catch (Exception error) when (error is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            // Том без списков доступа — сетевой диск или флешка — прав не
            // примет. Отказаться от истории из-за этого хуже, чем записать её:
            // выбор «где лежит файл» делает администратор, и он же отвечает за
            // том.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyAcl(string path)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } user)
        {
            return;
        }

        FileInfo file = new(path);
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(security);
    }
}
