using System.Threading;

namespace EliteSIP.App.Shell;

/// <summary>
/// Идёт ли сейчас установка EliteSIP.
/// </summary>
///
/// <remarks>
/// Установщик держит глобальный мьютекс всё время работы (`SetupMutex` в
/// <c>installer.iss</c>). Глобальный, потому что установщик обновления работает
/// от SYSTEM в нулевом сеансе, а приложение — в сеансе оператора.
///
/// Мьютекс, созданный SYSTEM, оператору может быть закрыт на открытие, и отказ
/// в доступе здесь тоже ответ: мьютекс есть — значит, есть и установщик.
/// </remarks>
internal static class SetupInProgress
{
    /// <summary>Имя — то же, что в <c>SetupMutex</c> установщика.</summary>
    internal const string MutexName = @"Global\EliteSIP.Setup";

    internal static bool IsRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
