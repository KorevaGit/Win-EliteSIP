using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EliteSIP.Updater;

/// <summary>
/// Запуск приложения в сеансе того, кто сидит за машиной.
/// </summary>
///
/// <remarks>
/// <b>Зачем это вообще нужно.</b> Обновляльщик работает от SYSTEM, то есть в
/// нулевом сеансе. Всё, что он запустит обычным <c>Process.Start</c>, окажется
/// там же: процесс будет жить, окна рисоваться некому, и оператор увидит, что
/// софтфон после обновления пропал. Поэтому приложение поднимается явно — с
/// маркером активного сеанса.
///
/// Порядок ровно такой, каким его требует Windows:
/// <list type="number">
///   <item><description>номер активного сеанса консоли;</description></item>
///   <item><description>маркер сидящего в нём пользователя (нужна привилегия
///   <c>SE_TCB_NAME</c> — у SYSTEM она есть);</description></item>
///   <item><description>копия маркера первичной (<c>CreateProcessAsUser</c> с
///   маркером олицетворения не работает);</description></item>
///   <item><description>блок окружения пользователя — иначе процесс получит
///   переменные SYSTEM, и <c>%LOCALAPPDATA%</c> у него укажет на чужой профиль,
///   то есть настройки оператора он не найдёт;</description></item>
///   <item><description>рабочий стол <c>winsta0\default</c> — тот, на который
///   смотрит человек.</description></item>
/// </list>
///
/// Отказ здесь не смертелен: автозапуск поднимет софтфон при следующем входе в
/// систему. Поэтому наружу отдаётся <c>bool</c> с причиной, а не исключение.
/// </remarks>
internal static class UserSession
{
    private const int TokenDuplicate = 0x0002;
    private const int TokenQuery = 0x0008;
    private const int TokenAssignPrimary = 0x0001;
    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenAdjustDefault = 0x0080;
    private const int TokenAdjustSessionId = 0x0100;

    private const uint CreateUnicodeEnvironment = 0x0000_0400;
    private const uint CreateNoWindow = 0x0800_0000;

    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    private const uint NoActiveSession = 0xFFFF_FFFF;

    private const uint CreateBreakawayFromJob = 0x0100_0000;
    private const int AccessDenied = 5;

    /// <summary>Работает ли программа уже в сеансе того, кто за машиной.</summary>
    ///
    /// <remarks>
    /// Страховочный подъём (см. `Relaunch` в Program.cs) обязан это знать:
    /// вторая копия софтфона не поднимется — первая её разбудит, — но лишний
    /// запуск это всё равно окно, мелькнувшее у оператора.
    /// </remarks>
    internal static bool IsRunningInActiveSession(string processName)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == NoActiveSession)
        {
            return false;
        }

        foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.SessionId == session)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool TryStartInActiveSession(string executable, out string reason, string? arguments = null)
    {
        // Буфер, а не строка: CreateProcessW вправе писать в командную строку.
        char[]? commandLine = arguments is null ? null : $"\"{executable}\" {arguments}\0".ToCharArray();

        var session = WTSGetActiveConsoleSessionId();
        if (session == NoActiveSession)
        {
            reason = "за машиной никого нет";
            return false;
        }

        var token = IntPtr.Zero;
        var primary = IntPtr.Zero;
        var environment = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken(session, out token))
            {
                // Сеанс есть, а пользователя в нём нет: экран блокировки до
                // первого входа или сеанс, из которого только что вышли.
                reason = $"маркер сеанса {session} не получен: {LastError()}";
                return false;
            }

            const int access = TokenDuplicate | TokenQuery | TokenAssignPrimary
                | TokenAdjustPrivileges | TokenAdjustDefault | TokenAdjustSessionId;

            if (!DuplicateTokenEx(token, access, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
            {
                reason = $"маркер не скопировался: {LastError()}";
                return false;
            }

            if (!CreateEnvironmentBlock(out environment, primary, inherit: false))
            {
                // Без окружения процесс получил бы переменные SYSTEM — то есть
                // чужой профиль. Лучше не запускать вовсе, чем запустить
                // софтфон, который не найдёт своих настроек.
                reason = $"окружение пользователя не собралось: {LastError()}";
                return false;
            }

            StartupInfo startup = new()
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = @"winsta0\default",
            };

            // Сперва — с выходом из объекта задания.
            //
            // Обновляльщик запущен планировщиком, а тот держит процессы задачи
            // в своём объекте задания; всё, что мы создадим, по умолчанию
            // окажется там же. Закрытие задания по концу задачи унесло бы и
            // только что поднятый софтфон — со стороны это выглядело бы ровно
            // как «после обновления не запустился». Задание может выход и не
            // разрешать — тогда пробуем обычным порядком.
            var started = CreateProcessAsUser(
                primary,
                executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                CreateUnicodeEnvironment | CreateNoWindow | CreateBreakawayFromJob,
                environment,
                Path.GetDirectoryName(executable),
                ref startup,
                out var information);

            if (!started && Marshal.GetLastWin32Error() == AccessDenied)
            {
                started = CreateProcessAsUser(
                    primary,
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    CreateUnicodeEnvironment | CreateNoWindow,
                    environment,
                    Path.GetDirectoryName(executable),
                    ref startup,
                    out information);
            }

            if (!started)
            {
                reason = $"процесс не создался: {LastError()}";
                return false;
            }

            CloseHandle(information.hThread);
            CloseHandle(information.hProcess);

            reason = string.Empty;
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (primary != IntPtr.Zero)
            {
                CloseHandle(primary);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        int desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        [In, Out] char[]? commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
