using System.Security.Principal;
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
///
/// <b>В общий каталог SYSTEM только читает — ничего не пишет и не удаляет.</b>
/// Оператор может заменить этот каталог связкой (junction на <c>\RPC Control</c>
/// плюс символическая ссылка диспетчера объектов), и тогда любая запись или
/// удаление от SYSTEM «в общем каталоге» на деле попадает в файл, который
/// выбрал оператор. Произвольное удаление от SYSTEM — известный путь к правам
/// администратора. До 0.1.67 так жили журнал обновляльщика (дописывался и
/// стирался по размеру) и отметка о согласии (стиралась после установки).
/// Поэтому всё, что обновляльщик пишет, — только в <see cref="TrustedUpdates"/>.
/// </remarks>
internal static class Paths
{
    internal static string Shared { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EliteSIP");

    /// <summary>Отметка «оператор согласился». Внутри — версия. Только читается.</summary>
    internal static string Request { get; } = Path.Combine(Shared, "update-requested");

    /// <summary>Куда качает приложение. Только читается.</summary>
    internal static string SharedUpdates { get; } = Path.Combine(Shared, "updates");

    // Порядок свойств значим: статические инициализаторы идут сверху вниз, и
    // всё, что лежит в своём каталоге, объявлено после него.

    /// <summary>Куда переносится проверенный выпуск. Только сюда и только отсюда запуск.</summary>
    internal static string TrustedUpdates { get; } = Path.Combine(AppContext.BaseDirectory, "updates");

    /// <summary>Журнал обновляльщика. Свой каталог, а не общий, — см. описание класса.</summary>
    internal static string Log { get; } = Path.Combine(TrustedUpdates, "updater.log");

    /// <summary>Какая отметка о согласии уже обработана. Вместо удаления самой отметки.</summary>
    internal static string RequestHandled { get; } = Path.Combine(TrustedUpdates, "request-handled");

    /// <summary>Отметка «поднять софтфон после установки». Внутри — версия.</summary>
    internal static string RelaunchMarker { get; } = Path.Combine(TrustedUpdates, "relaunch-pending");

    /// <summary>Когда обновляльщик впервые увидел установленный выпуск. См. <see cref="Recovery"/>.</summary>
    internal static string InstalledSeen { get; } = Path.Combine(TrustedUpdates, "installed-seen");

    /// <summary>О каком выпуске уже сказано, что он не запускается.</summary>
    internal static string RecoveryNoted { get; } = Path.Combine(TrustedUpdates, "recovery-noted");

    /// <summary>
    /// Отметка приложения «запустилось и прожило минуту». Внутри — версия.
    /// Пишет приложение, здесь только читается.
    /// </summary>
    ///
    /// <remarks>
    /// Объявлена после своего каталога только формально: она в общем, а не в
    /// своём, — как и <see cref="Request"/>, и так же ничему не доверяет.
    /// Подделав её, оператор лишь отключит аварийный путь своей машины.
    /// </remarks>
    internal static string Started { get; } = Path.Combine(Shared, "started");
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
///
/// <b>Отметку SYSTEM не удаляет</b> — см. <see cref="Paths"/>. Вместо этого
/// обработанная отметка запоминается у себя (<see cref="Paths.RequestHandled"/>)
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

/// <summary>Согласие оператора: на какую версию и когда оставлено.</summary>
/// <summary>
/// Аварийный путь: выпуск, который не запускается, лечится следующим выпуском
/// без согласия оператора.
/// </summary>
///
/// <remarks>
/// <para>
/// Обычная установка ждёт согласия, а согласие даёт приложение. Выпуск, который
/// роняет приложение на запуске, этот круг разрывает: исправленный выпуск на
/// канале есть, а спросить о нём некому. До 0.1.67 такую машину поднимал только
/// администратор руками — при двухстах машинах это выезд на весь парк.
/// </para>
/// <para>
/// Признак поломки — отметка приложения «запустилось и прожило минуту»
/// (<see cref="Paths.Started"/>): её нет для установленного выпуска дольше
/// <see cref="Grace"/> с того такта, когда обновляльщик этот выпуск впервые
/// увидел, и приложение сейчас не запущено ни в одном сеансе. Тогда ставится
/// выпуск с канала — по-прежнему только подписанный и только новее.
/// </para>
/// <para>
/// Ложное срабатывание обходится дёшево. Машина, где софтфоном просто не
/// пользуются, получит обновление без вопроса, но и без разговора, который оно
/// могло бы прервать: запущенное приложение путь выключает.
/// </para>
/// </remarks>
internal static class Recovery
{
    internal static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    /// <summary>
    /// Когда установленный выпуск впервые увиден. Новый выпуск запоминается
    /// текущим временем.
    /// </summary>
    internal static DateTime FirstSeen(Version installed, string path, DateTime nowUtc)
    {
        var release = installed.ToString(3);

        try
        {
            if (File.Exists(path))
            {
                var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2
                    && parts[0] == release
                    && long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
                    && ticks <= nowUtc.Ticks)
                {
                    return new DateTime(ticks, DateTimeKind.Utc);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{release} {nowUtc.Ticks}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Не записалось — отсчёт начнётся заново на следующем такте. Путь от
            // этого только запаздывает, а не срабатывает зря.
        }

        return nowUtc;
    }

    /// <summary>Застрял ли установленный выпуск.</summary>
    internal static bool IsStuck(Version installed, Version? started, DateTime firstSeenUtc, DateTime nowUtc, bool appRunning)
        => !appRunning
            && !SameRelease(started, installed)
            && nowUtc - firstSeenUtc >= Grace;

    /// <summary>
    /// Впервые ли замечена поломка этого выпуска. Нужно журналу: иначе строка
    /// повторялась бы каждые десять минут, пока на канале нет исправления.
    /// </summary>
    internal static bool NoteOnce(Version installed, string path)
    {
        var release = installed.ToString(3);

        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() == release)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, release);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return true;
    }

    private static bool SameRelease(Version? left, Version right) =>
        left is not null
            && left.Major == right.Major
            && left.Minor == right.Minor
            && left.Build == right.Build;
}

internal readonly record struct Consent(Version Version, DateTime WrittenUtc)
{
    /// <summary>Как согласие запоминается в <see cref="Paths.RequestHandled"/>.</summary>
    internal string Stamp => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{Version.ToString(3)} {WrittenUtc.Ticks}");
}

/// <summary>Единственный экземпляр обновляльщика.</summary>
///
/// <remarks>
/// Мьютекс в <c>Global\</c> может создать любой пользователь, и до 0.1.67
/// оператор, создавший его заранее, молча отключал обновления своей машины:
/// каждый такт видел «занято» и выходил. Теперь «занято» засчитывается, только
/// если мьютекс создан SYSTEM или администратором; чужой — работаем без него.
/// Второго настоящего экземпляра это не допустит: задача заведена с
/// <c>MultipleInstances IgnoreNew</c>, мьютекс — вторая линия обороны.
/// </remarks>
internal static class UpdaterInstance
{
    internal const string MutexName = @"Global\EliteSIP.Updater";

    /// <summary>
    /// Занимает мьютекс. <see langword="false"/> — уже работает настоящий
    /// обновляльщик, и этому такту делать нечего.
    /// </summary>
    ///
    /// <remarks>
    /// <paramref name="mutex"/> бывает <see langword="null"/> и при
    /// <see langword="true"/>: мьютекс чужой или недоступен, работаем без него.
    /// </remarks>
    internal static bool TryClaim(UpdaterLog log, out Mutex? mutex, string name = MutexName)
    {
        try
        {
            mutex = new Mutex(initiallyOwned: true, name, out var mine);
            if (mine)
            {
                return true;
            }

            var genuine = CreatedByService(mutex);
            mutex.Dispose();
            mutex = null;

            if (genuine)
            {
                return false;
            }

            log.Write("мьютекс обновляльщика создан не SYSTEM, а пользователем — работаю без него");
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Чужой мьютекс с запретом доступа — тот же случай, только грубее.
            mutex = null;
            log.Write($"мьютекс обновляльщика недоступен ({error.Message}) — работаю без него");
            return true;
        }
    }

    /// <summary>Создан ли мьютекс SYSTEM или администратором, а не пользователем.</summary>
    internal static bool CreatedByService(Mutex mutex)
    {
        try
        {
            var owner = mutex.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

            return owner is not null
                && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Журнал обновляльщика — свой, потому что работает он от SYSTEM.</summary>
///
/// <remarks>
/// В общий журнал приложения писать нельзя: тот живёт в профиле оператора, а у
/// SYSTEM профиль свой, и записи ушли бы туда, где их никто не ищет.
///
/// Лежит в <c>{app}\updates</c>, а не в общем каталоге, хотя там его было бы
/// проще найти: журнал дописывается и стирается по размеру, и делать это от
/// SYSTEM можно только там, куда оператор писать не может (см. <see cref="Paths"/>).
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

        /// <summary>Корень канала выпусков; не задан — <see cref="BaseUrl"/>. См. приложение, <c>Provisioning.UpdateChannel</c>.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("releasesURL")]
        public string? ReleasesBaseUrl { get; init; }

        public string User { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        internal Uri? ReleasesUrl()
        {
            var baseUrl = string.IsNullOrWhiteSpace(ReleasesBaseUrl) ? BaseUrl : ReleasesBaseUrl;
            var root = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

            return Uri.TryCreate(root + "releases/current.json", UriKind.Absolute, out var url)
                ? url
                : null;
        }

        internal string BasicHeader()
            => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
    }
}
