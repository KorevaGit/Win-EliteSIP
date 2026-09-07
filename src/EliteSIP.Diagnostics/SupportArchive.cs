using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace EliteSIP.Diagnostics;

/// <summary>
/// Архив для поддержки: журнал плюс сведения о машине, одним файлом.
///
/// Существует затем, чтобы инструкция оператору состояла из одного шага.
/// «Найдите папку журналов, выделите нужные файлы, заархивируйте и пришлите» —
/// это четыре шага, на каждом из которых теряется половина обращений, а
/// присылают в итоге снимок экрана с одной строкой.
///
/// Перенесено из macOS-версии
/// (<c>Packages/Diagnostics/Sources/Diagnostics/SupportArchive.swift</c>).
/// Единственное изменение — упаковка. Там запускался <c>/usr/bin/ditto</c>,
/// потому что своей упаковки в Foundation нет, а тянуть зависимость в проект без
/// единой зависимости не хотелось. В .NET zip есть в самой платформе
/// (<see cref="ZipArchive"/>), поэтому ни внешнего процесса, ни промежуточного
/// каталога больше нет: вместе с ними ушёл и отдельный тип ошибки «архиватор
/// вернул N» — упаковка теперь бросает обычный <see cref="IOException"/>.
/// </summary>
public static class SupportArchive
{
    /// <summary>
    /// Собирает архив из файлов журнала и текстовой справки.
    ///
    /// Справка идёт вместе с журналом, потому что первый вопрос поддержки — это
    /// всегда «какая версия и какая система», и без неё разбор начинается с
    /// переписки, а не с журнала.
    /// </summary>
    /// <param name="logs">Файлы журнала, обычно <see cref="LogFile.Files"/>.</param>
    /// <param name="summary">
    /// Содержимое <c>summary.txt</c>. Секреты сюда класть нельзя — маскирование
    /// журнала на эту строку не распространяется.
    /// </param>
    /// <param name="destination">Куда положить готовый <c>.zip</c>.</param>
    /// <param name="extras">
    /// Что ещё положить рядом, именем файла к содержимому. Заведено под
    /// обезличенные настройки: до 27 августа 2026 они уходили в поддержку
    /// отдельной кнопкой, и половина обращений приезжала без них — потому что
    /// нажать надо было две кнопки, а очевидна одна. Секреты сюда класть нельзя
    /// по тому же правилу, что и в <paramref name="summary"/>.
    /// </param>
    public static string Make(
        IReadOnlyList<string> logs,
        string summary,
        string destination,
        IReadOnlyDictionary<string, byte[]>? extras = null)
    {
        ArgumentNullException.ThrowIfNull(logs);

        // Готовый архив с прошлого раза перезаписывается: имя несёт отметку
        // времени, и совпадение означает повтор в ту же минуту.
        File.Delete(destination);

        using (FileStream output = File.Create(destination))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "summary.txt", new UTF8Encoding(false).GetBytes(summary));

            if (extras is not null)
            {
                foreach (KeyValuePair<string, byte[]> extra in extras)
                {
                    WriteEntry(archive, extra.Key, extra.Value);
                }
            }

            foreach (string path in logs)
            {
                CopyLog(archive, path);
            }
        }

        return destination;
    }

    /// <summary>
    /// Имя архива с отметкой времени: два обращения подряд не должны
    /// перезаписывать друг друга.
    /// </summary>
    public static string SuggestedName(DateTime? now = null)
    {
        string stamp = (now ?? DateTime.Now).ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
        return $"EliteSIP-logs-{stamp}.zip";
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] contents)
    {
        using Stream entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        entry.Write(contents, 0, contents.Length);
    }

    private static void CopyLog(ZipArchive archive, string path)
    {
        // Текущий файл журнала открыт на запись прямо сейчас, поэтому читается с
        // FileShare.ReadWrite. Пропавший или занятый файл архив не роняет: цель
        // — довезти в поддержку то, что есть, а не отказаться из-за одного из
        // пяти файлов.
        FileStream source;
        try
        {
            source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        using (source)
        {
            using Stream entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal).Open();
            source.CopyTo(entry);
        }
    }
}
