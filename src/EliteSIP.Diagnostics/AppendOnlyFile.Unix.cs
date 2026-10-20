using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EliteSIP.Diagnostics;

/// <summary>
/// Вторая половина <see cref="AppendOnlyFile"/>: Linux и macOS.
///
/// Здесь гарантию даёт <c>O_APPEND</c> — ровно то, на чём стоял журнал
/// macOS-оригинала: ядро перед каждой записью само ставит смещение в конец
/// файла, и два дескриптора на одном файле друг друга не затирают.
///
/// <c>FileStream</c> с <c>FileMode.Append</c> и здесь не годится, по той же
/// причине, что на Windows: .NET открывает файл без <c>O_APPEND</c>, а позицию
/// хранит в объекте и пишет по ней (<c>pwrite</c>). Поэтому вызовы в libc
/// напрямую — <c>open</c>, <c>write</c>, <c>fsync</c>; закрывает дескриптор
/// <see cref="SafeFileHandle"/>, он на Unix умеет это сам.
/// </summary>
internal sealed partial class AppendOnlyFile
{
    /// <summary>
    /// Права нового файла — <c>0600</c>.
    ///
    /// В журнале маскируются пароли и ответы digest, но номера остаются как
    /// есть (<c>LogRedactionTests</c>): по ним разбирают «вызов не дошёл». А
    /// номера — это лиды. Умолчание <c>0644</c> отдало бы их любому
    /// пользователю машины; на Windows то же закрывает наследование прав от
    /// профиля, здесь — сам режим файла. На уже существующий файл не влияет.
    /// </summary>
    private const uint OwnerReadWrite = 0x180;

    private const int EINTR = 4;

    private static AppendOnlyFile? OpenUnix(string path)
    {
        int flags = UnixOpenFlags();
        if (flags == 0)
        {
            return null;
        }

        int descriptor = open(path, flags, OwnerReadWrite);
        if (descriptor < 0)
        {
            return null;
        }

        return new AppendOnlyFile(new SafeFileHandle(descriptor, ownsHandle: true));
    }

    /// <summary>
    /// Флаги <c>open(2)</c>. Числа у Linux и macOS разные, и это не опечатка:
    /// <c>O_APPEND</c> на Linux — <c>0x400</c>, на macOS — <c>0x8</c>.
    ///
    /// Ноль — «не знаем этой системы»: журнала тогда не будет, но и неверный
    /// флаг не откроет файл на запись с начала.
    /// </summary>
    private static int UnixOpenFlags()
    {
        const int writeOnly = 0x1;
        if (OperatingSystem.IsLinux())
        {
            const int create = 0x40, append = 0x400, closeOnExec = 0x80000;
            return writeOnly | create | append | closeOnExec;
        }

        if (OperatingSystem.IsMacOS())
        {
            const int create = 0x200, append = 0x8, closeOnExec = 0x1000000;
            return writeOnly | create | append | closeOnExec;
        }

        return 0;
    }

    /// <summary>
    /// То же правило, что у Windows-половины: ошибка проглатывается, короткая
    /// запись дописывается. Плюс одно своё: прерванный сигналом <c>write</c>
    /// (<c>EINTR</c>) — не ошибка, а повод повторить.
    /// </summary>
    private unsafe int AppendUnix(byte[] data)
    {
        int written = 0;
        bool added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            int descriptor = (int)_handle.DangerousGetHandle();
            fixed (byte* start = data)
            {
                while (written < data.Length)
                {
                    nint count = write(descriptor, start + written, data.Length - written);
                    if (count < 0 && Marshal.GetLastPInvokeError() == EINTR)
                    {
                        continue;
                    }

                    if (count <= 0)
                    {
                        break;
                    }

                    written += (int)count;
                }
            }
        }
        finally
        {
            if (added)
            {
                _handle.DangerousRelease();
            }
        }

        return written;
    }

    private void FlushUnix()
    {
        bool added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            _ = fsync((int)_handle.DangerousGetHandle());
        }
        finally
        {
            if (added)
            {
                _handle.DangerousRelease();
            }
        }
    }

    // Имена — как в libc: так их проще сверять с man-страницами.
#pragma warning disable IDE1006
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags, uint mode);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint write(int descriptor, byte* buffer, nint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fsync(int descriptor);
#pragma warning restore IDE1006
}
