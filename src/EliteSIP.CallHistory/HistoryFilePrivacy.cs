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
/// <b>Вне Windows</b> — права <c>0600</c>, как в оригинале. Там они снова
/// означают то, что означали на macOS: файл читает только владелец (и
/// <c>root</c>). Умолчание без этого — <c>0644</c> по <c>umask</c>, то есть
/// номера лидов любому пользователю машины.
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
            CreateOwnerOnly(databasePath);
        }

        RestrictOne(databasePath);
        foreach (string suffix in Companions)
        {
            RestrictOne(databasePath + suffix);
        }
    }

    /// <summary>
    /// Пустая база. Вне Windows права задаются в самом <c>open(2)</c>, а не
    /// после: щели, о которой выше, там нет вовсе. На Windows список доступа
    /// ставится следом, в <see cref="RestrictOne"/>.
    /// </summary>
    private static void CreateOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using (File.Create(path))
            {
            }

            return;
        }

        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        try
        {
            using (new FileStream(path, options))
            {
            }
        }
        catch (IOException) when (File.Exists(path))
        {
            // Успел кто-то другой — права всё равно поправит RestrictOne.
        }
    }

    private static void RestrictOne(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                ApplyOwnerOnlyAcl(path);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
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
