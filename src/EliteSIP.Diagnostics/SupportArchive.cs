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
    /// <summary>Собирает текущий, ротированные и аварийный журналы, даже без активного писателя.</summary>
    public static string MakeFromDirectory(string directory, string summary, string destination, LogFile? current = null,
        IReadOnlyDictionary<string, byte[]>? extras = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        current?.Flush();
        var paths = new List<string>(current?.Files() ?? []);
        foreach (var folder in new[] { directory, Path.Combine(directory, "Logs") })
        {
            try
            {
                paths.AddRange(Directory.GetFiles(folder).Where(path =>
                    Path.GetFileName(path) is LogFile.CurrentFileName or "crash.log"
                    || (Path.GetFileName(path).StartsWith("elitesip-", StringComparison.OrdinalIgnoreCase)
                        && Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase))));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return Make(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), summary, destination, extras);
    }

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
        // времени, и совпадение означает повтор с тем же явно заданным именем.
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

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "summary.txt", "collection.txt" };
            if (extras is not null) { used.UnionWith(extras.Keys); }
            var report = new StringBuilder();
            var copied = 0;
            foreach (string path in logs)
            {
                var name = Path.GetFileName(path);
                var index = 2;
                var entryName = name;
                while (!used.Add(entryName)) { entryName = $"logs-{index++}/{name}"; }
                var included = CopyLog(archive, path, entryName);
                if (included) { copied++; }
                report.AppendLine(CultureInfo.InvariantCulture, $"{(included ? "добавлен" : "не удалось прочитать")}: {entryName}");
            }
            report.Insert(0, $"Файлов журнала добавлено: {copied} из {logs.Count}{Environment.NewLine}");
            if (logs.Count == 0) { report.AppendLine("Файлы журнала не найдены. Проверьте, включена ли запись журнала в Управлении."); }
            WriteEntry(archive, "collection.txt", new UTF8Encoding(false).GetBytes(report.ToString()));
        }

        return destination;
    }

    /// <summary>
    /// Имя архива с отметкой времени: два обращения подряд не должны
    /// перезаписывать друг друга.
    /// </summary>
    public static string SuggestedName(DateTime? now = null)
    {
        string stamp = (now ?? DateTime.Now).ToString("yyyy-MM-dd-HHmmss-fff", CultureInfo.InvariantCulture);
        return $"EliteSIP-logs-{stamp}.zip";
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] contents)
    {
        using Stream entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        entry.Write(contents, 0, contents.Length);
    }

    private static bool CopyLog(ZipArchive archive, string path, string entryName)
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
            return false;
        }

        using (source)
        {
            using Stream entry = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
            using var reader = new StreamReader(source);
            using var writer = new StreamWriter(entry, new UTF8Encoding(false));
            while (reader.ReadLine() is { } line) { writer.WriteLine(LogRedaction.Redact(line)); }
        }
        return true;
    }
}
