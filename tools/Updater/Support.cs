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
