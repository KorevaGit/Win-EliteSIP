using Microsoft.Win32;

namespace EliteSIP.App.Shell;

/// <summary>
/// Запускать ли софтфон вместе с входом в систему.
/// </summary>
///
/// <remarks>
/// <b>Почему это вообще нужно.</b> Софтфон, не поднявшийся после ночной
/// перезагрузки, пропускает первые звонки смены — ровно тот случай, ради
/// которого в приложении есть автоподключение. Оператор при этом не виноват: он
/// не обязан помнить, что телефон надо запускать.
///
/// <b>Правда живёт в реестре, а не в наших настройках.</b> Windows показывает
/// автозапуск в диспетчере задач и даёт его там выключить; настройка в файле
/// разошлась бы с реальностью в тот же день. Поэтому здесь нет ни поля в
/// <c>settings.json</c>, ни кэша: спрашиваем систему и говорим системе.
///
/// <b>Ветка пользователя, а не машины.</b> <c>HKEY_CURRENT_USER</c> не требует
/// прав администратора и означает «запускать у этого пользователя» — то, что и
/// нужно: за машиной работают разные люди, и добавочный у каждого свой.
/// </remarks>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EliteSIP";

    /// <summary>Стоит ли автозапуск и указывает ли он на эту же копию.</summary>
    ///
    /// <remarks>
    /// Сравнение с путём нынешней копии не педантизм: после переустановки в
    /// другой каталог запись остаётся прежней и запускает то, чего уже нет.
    /// Такой автозапуск честнее показать выключенным — включение его перепишет.
    /// </remarks>
    internal static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);

                return key?.GetValue(ValueName) is string command
                    && command.Contains(Executable, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is System.Security.SecurityException
                                              or UnauthorizedAccessException)
            {
                // Реестр закрыт политикой — значит, автозапуска нет и не будет.
                return false;
            }
        }
    }

    /// <summary>Включает или снимает автозапуск. Возвращает, что получилось.</summary>
    internal static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                // Путь в кавычках: в нём бывают пробелы, а без кавычек Windows
                // прочитает первое слово как имя программы, а остальное — как
                // её аргументы.
                key.SetValue(ValueName, $"\"{Executable}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception error) when (error is System.Security.SecurityException
                                          or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Executable => Environment.ProcessPath ?? string.Empty;
}
