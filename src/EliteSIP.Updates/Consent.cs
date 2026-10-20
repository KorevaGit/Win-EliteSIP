using System.Text;

namespace EliteSIP.Updater;

// Переносимая часть обновляльщика: ни путей, ни прав, ни способа установки —
// только правила. Вынесено из tools/Updater 20 октября 2026: те же правила
// нужны обновляльщику Linux-версии (служба systemd от root), и своя их копия
// означала бы второе место, где решается, когда машине ставить выпуск.

/// <summary>Отметка о согласии оператора.</summary>
///
/// <remarks>
/// Файлом, а не запуском задачи по требованию. Запуск по требованию потребовал
/// бы выдать оператору право выполнять задачу — то есть править её список
/// управления доступом из установщика, — а это лишний способ ошибиться в правах
/// ради того же результата. Задача просыпается сама и смотрит, есть ли файл.
///
/// Файл лежит в каталоге, доступном оператору на запись, и подделать его может
/// кто угодно. Это учтено: отметка не говорит, <i>что</i> ставить, — она говорит
/// только «поставить то, что объявлено подписанным манифестом».
///
/// <b>Отметку SYSTEM не удаляет</b> — см. <c>Paths</c> обновляльщика. Вместо этого
/// обработанная отметка запоминается у себя (<c>Paths.RequestHandled</c> обновляльщика)
/// версией и временем записи файла. Приложение переписывает отметку на каждое
/// согласие, так что повторное согласие на ту же версию — например, после
/// сорвавшейся установки — снова отличимо от уже обработанного.
/// </remarks>
internal static class Request
{
    /// <summary>
    /// Больше версии в отметке быть не может. Читаем не дальше: через подменённый
    /// путь SYSTEM иначе можно заставить загрузить в память чужой огромный файл.
    /// </summary>
    private const int MaximumBytes = 64;

    internal static Consent? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var written = File.GetLastWriteTimeUtc(path);

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[MaximumBytes + 1];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read > MaximumBytes)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read).Trim();

            return Version.TryParse(text, out var version) ? new Consent(version, written) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Обработана ли уже эта отметка.</summary>
    internal static bool IsHandled(Consent consent, string handledPath)
    {
        try
        {
            return File.Exists(handledPath)
                && string.Equals(File.ReadAllText(handledPath).Trim(), consent.Stamp, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Запоминает отметку как обработанную — у себя, а не в общем каталоге.</summary>
    internal static void MarkHandled(Consent consent, string handledPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(handledPath)!);
            File.WriteAllText(handledPath, consent.Stamp);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Не записалось — следующий заход увидит отметку снова, сверит
            // версию с установленной и выйдет. Круга установок не будет.
        }
    }
}

internal readonly record struct Consent(Version Version, DateTime WrittenUtc)
{
    /// <summary>Как согласие запоминается в <c>Paths.RequestHandled</c> обновляльщика.</summary>
    internal string Stamp => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{Version.ToString(3)} {WrittenUtc.Ticks}");
}
