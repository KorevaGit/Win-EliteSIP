using System.Diagnostics;
using System.IO;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Передача обновления обновляльщику.
/// </summary>
///
/// <remarks>
/// <b>Почему приложение больше не ставит обновление само.</b> Машины заказчика
/// заперты политикой ограниченного использования программ: разрешён запуск
/// только из <c>Program Files</c>, <c>Program Files (x86)</c> и
/// <c>Windows</c>, а прав администратора у оператора нет. Скачанный установщик
/// лежит там, откуда запуск запрещён, и ставить его надо туда, куда оператор
/// писать не может, — то есть сделать это отсюда нельзя ни при каком раскладе.
///
/// Ставит <c>EliteSIP.Updater</c>: он лежит рядом с программой в
/// <c>Program Files</c> и запускается задачей планировщика от SYSTEM. Отсюда
/// уходят ровно две вещи — отметка о согласии оператора и попытка разбудить
/// задачу, чтобы не ждать её десятиминутного такта.
///
/// Решение отменяет запись W12 об установке в профиль пользователя: под этой
/// политикой программа из профиля не запустилась бы вовсе.
/// </remarks>
internal static class UpdateHandoff
{
    /// <summary>Имя задачи. То же, что заводит установщик.</summary>
    private const string TaskName = @"EliteSIP\Update";

    /// <summary>Общий каталог: сюда качает приложение, отсюда читает SYSTEM.</summary>
    internal static string SharedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EliteSIP");

    internal static string UpdatesDirectory { get; } = Path.Combine(SharedDirectory, "updates");

    private static string RequestPath => Path.Combine(SharedDirectory, "update-requested");

    /// <summary>
    /// Оставляет отметку «оператор согласился на эту версию».
    /// </summary>
    ///
    /// <remarks>
    /// Версией, а не пустым файлом: обновляльщик ставит ровно то, на что
    /// согласились. Если на канале за это время появился выпуск новее, он
    /// откажется и будет ждать нового согласия: спрашивать человека — не его
    /// дело, а наше.
    /// </remarks>
    internal static bool Request(Version version, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(SharedDirectory);
            File.WriteAllText(RequestPath, version.ToString(3));

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Каталога нет или он закрыт — значит программа поставлена не нашим
            // установщиком. Обновиться она не сможет, и сказать об этом надо.
            log($"обновление не передано: {error.Message}");

            return false;
        }
    }

    /// <summary>
    /// Будит задачу, чтобы не ждать её такта.
    /// </summary>
    ///
    /// <remarks>
    /// Не беда, если не вышло: задача просыпается сама каждые десять минут и
    /// увидит отметку тогда. Поэтому отказ здесь возвращается, а не бросается —
    /// от него зависит только то, уходить ли нам сейчас с дороги.
    ///
    /// Аргументов задаче не передаётся, и это намеренно: задача работает от
    /// SYSTEM, а аргументы от вызывающего означали бы запуск произвольного кода
    /// с её правами.
    /// </remarks>
    internal static bool Trigger(Action<string> log)
    {
        try
        {
            ProcessStartInfo start = new("schtasks.exe")
            {
                Arguments = $"/Run /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(10_000))
            {
                return false;
            }

            if (process.ExitCode != 0)
            {
                log($"задача обновления не запустилась (код {process.ExitCode}) — подождём её такта");

                return false;
            }

            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            log($"задача обновления не запустилась: {error.Message} — подождём её такта");

            return false;
        }
    }
}
