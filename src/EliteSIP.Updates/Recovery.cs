namespace EliteSIP.Updater;

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
/// (на Windows — <c>Paths.Started</c> обновляльщика): её нет для установленного выпуска дольше
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
