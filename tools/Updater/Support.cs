using System.Text;
using System.Text.Json;
using EliteSIP.PanelLink;

namespace EliteSIP.Updater;

/// <summary>Куда обновляльщик смотрит и куда пишет.</summary>
///
/// <remarks>
/// Два каталога, и разница между ними — это вся защита здесь.
///
/// <b>Общий</b> (<c>%PROGRAMDATA%\EliteSIP</c>) открыт оператору на запись:
/// иначе приложение не смогло бы ни скачать туда выпуск, ни оставить отметку.
/// Всё, что лежит там, — непроверенное.
///
/// <b>Свой</b> (<c>Program Files</c>) оператору недоступен. Там лежит открытый
/// ключ линии, которым проверяется подпись, и туда переносится выпуск перед
/// запуском.
/// </remarks>
internal static class Paths
{
    internal static string Shared { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EliteSIP");

    /// <summary>Отметка «оператор согласился». Внутри — версия.</summary>
    internal static string Request { get; } = Path.Combine(Shared, "update-requested");

    /// <summary>Куда качает приложение.</summary>
    internal static string SharedUpdates { get; } = Path.Combine(Shared, "updates");

    internal static string Log { get; } = Path.Combine(Shared, "updater.log");

    /// <summary>Куда переносится проверенный выпуск. Только сюда и только отсюда запуск.</summary>
    internal static string TrustedUpdates { get; } = Path.Combine(AppContext.BaseDirectory, "updates");

    /// <summary>Отметка «поднять софтфон после установки». Внутри — версия.</summary>
    internal static string RelaunchMarker { get; } = Path.Combine(TrustedUpdates, "relaunch-pending");
}

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
/// </remarks>
internal static class Request
{
    internal static Version? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();

            return Version.TryParse(text, out var version) ? version : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Clear(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Не снялась — следующий заход увидит её снова, сверит версию с
            // установленной и выйдет. Круга не будет.
        }
    }
}

/// <summary>Журнал обновляльщика — свой, потому что работает он от SYSTEM.</summary>
///
/// <remarks>
/// В общий журнал приложения писать нельзя: тот живёт в профиле оператора, а у
/// SYSTEM профиль свой, и записи ушли бы туда, где их никто не ищет.
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

/// <summary>
/// Заводская настройка — <b>только</b> рядом с собой.
/// </summary>
///
/// <remarks>
/// Приложение ищет этот файл в двух местах: рядом с собой и в каталоге настроек
/// пользователя. Здесь второго места нет и быть не должно: каталог пользователя
/// оператору доступен, а из этого файла берётся ключ, которым проверяется
/// подпись выпуска. Позволить подменить его значило бы отдать установку под
/// правами SYSTEM тому, кто подменил.
/// </remarks>
internal sealed class Provisioning
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public UpdateChannel? Updates { get; init; }

    public string? PresetsPublicKey { get; init; }

    /// <summary>Ключ линии выпусков. Им и только им проверяется манифест.</summary>
    public string? ReleasesPublicKey { get; init; }

    internal static Provisioning? Read(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "provisioning.json");

            return File.Exists(path)
                ? JsonSerializer.Deserialize<Provisioning>(File.ReadAllText(path), Format)
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ключ, которым проверяется манифест выпуска.
    /// </summary>
    ///
    /// <remarks>
    /// Линии выпусков, а не панели. Обновляльщику ключ панели не нужен вовсе:
    /// он не читает ни предустановок, ни отзывов.
    /// </remarks>
    internal PanelPublicKey? PublicKey()
    {
        if (string.IsNullOrEmpty(ReleasesPublicKey))
        {
            return null;
        }

        try
        {
            return PanelPublicKey.FromBase64(ReleasesPublicKey);
        }
        catch (PanelLinkException)
        {
            return null;
        }
    }

    internal sealed class UpdateChannel
    {
        public string BaseUrl { get; init; } = string.Empty;

        public string User { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        internal Uri? ReleasesUrl()
        {
            var root = BaseUrl.EndsWith('/') ? BaseUrl : BaseUrl + "/";

            return Uri.TryCreate(root + "releases/current.json", UriKind.Absolute, out var url)
                ? url
                : null;
        }

        internal string BasicHeader()
            => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
    }
}
