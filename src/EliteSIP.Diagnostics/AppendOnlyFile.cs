using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EliteSIP.Diagnostics;

/// <summary>
/// Файл, открытый только на дозапись в конец.
///
/// Существует ради одного свойства, которого нет ни у <c>FileStream</c>, ни у
/// <c>File.AppendAllText</c>: запись должна садиться в конец файла атомарно,
/// даже если файл держат два экземпляра <see cref="LogFile"/> сразу.
///
/// В macOS-версии это давал <c>O_APPEND</c>, и причина была не в стройности.
/// Смена настроек журнала пересоздаёт <see cref="LogFile"/>, и короткое время
/// старый экземпляр ещё дописывает то, что осталось у него в очереди, а новый
/// уже открыл тот же файл. У каждого своё смещение, и записи затирали бы друг
/// друга — то есть терялись бы ровно те строки, ради которых журнал заводили.
///
/// <c>FileStream</c> в режиме <c>FileMode.Append</c> этого не даёт: .NET на
/// Windows открывает файл с <c>GENERIC_WRITE</c>, переставляет собственную
/// позицию в конец при открытии и дальше пишет по этой позиции. Смещение
/// принадлежит объекту, а не файлу, — то же самое, от чего уходили в оригинале.
/// Windows даёт нужную гарантию на уровне ядра, если дескриптор открыт с
/// <c>FILE_APPEND_DATA</c> и <b>без</b> <c>FILE_WRITE_DATA</c>: тогда
/// <c>WriteFile</c> игнорирует позицию и всегда дописывает в конец.
///
/// Отсюда и вызовы напрямую в <c>kernel32</c>. Это единственное место в
/// <c>EliteSIP.Diagnostics</c>, которое знает, что операционная система —
/// Windows, и вынесено оно отдельным типом именно поэтому.
/// </summary>
internal sealed partial class AppendOnlyFile : IDisposable
{
    private const uint FileAppendData = 0x0004;
    private const uint FileShareReadWrite = 0x0001 | 0x0002;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x80;

    private readonly SafeFileHandle _handle;

    private AppendOnlyFile(SafeFileHandle handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Открывает файл на дозапись, создавая его при необходимости.
    /// Возвращает <c>null</c>, если открыть не удалось: журнал не имеет права
    /// уронить приложение — см. <see cref="LogFile"/>.
    /// </summary>
    public static AppendOnlyFile? Open(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            FileAppendData,
            FileShareReadWrite,
            nint.Zero,
            OpenAlways,
            FileAttributeNormal,
            nint.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return new AppendOnlyFile(handle);
    }

    /// <summary>
    /// Дописывает буфер в конец файла и отдаёт, сколько байт легло.
    ///
    /// Ошибка записи проглатывается намеренно: полный диск не повод уронить
    /// софтфон из-за его собственной диагностики. Короткая запись дописывается —
    /// <c>WriteFile</c> вправе записать меньше запрошенного, и остаток в этом
    /// случае не мусор, а хвост строки.
    /// </summary>
    public int Append(byte[] data)
    {
        int written = 0;
        while (written < data.Length)
        {
            if (!WriteFile(_handle, ref data[written], (uint)(data.Length - written), out uint count, nint.Zero)
                || count == 0)
            {
                break;
            }

            written += (int)count;
        }

        return written;
    }

    public void Flush()
    {
        _ = FlushFileBuffers(_handle);
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(
        SafeFileHandle handle,
        ref byte buffer,
        uint bytesToWrite,
        out uint bytesWritten,
        nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);
}
