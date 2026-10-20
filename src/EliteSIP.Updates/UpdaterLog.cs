using System.Text;

namespace EliteSIP.Updater;

/// <summary>Журнал обновляльщика — свой, потому что работает он от SYSTEM.</summary>
///
/// <remarks>
/// В общий журнал приложения писать нельзя: тот живёт в профиле оператора, а у
/// SYSTEM профиль свой, и записи ушли бы туда, где их никто не ищет.
///
/// Лежит в <c>{app}\updates</c>, а не в общем каталоге, хотя там его было бы
/// проще найти: журнал дописывается и стирается по размеру, и делать это от
/// SYSTEM можно только там, куда оператор писать не может (см. <c>Paths</c> обновляльщика).
/// </remarks>
internal sealed class UpdaterLog
{
    private const long SizeLimit = 256 * 1024;

    private readonly string _path;

    internal UpdaterLog(string path)
    {
        _path = path;
    }

    internal void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Обрезается целиком, а не хвостом: обновление — событие редкое, и
            // разбирают всегда последнее. Возить половину файла ради истории,
            // которая старше прошлого выпуска, незачем.
            if (File.Exists(_path) && new FileInfo(_path).Length > SizeLimit)
            {
                File.Delete(_path);
            }

            File.AppendAllText(
                _path,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Журнал — не работа: не записалось, значит не записалось.
        }
    }
}
