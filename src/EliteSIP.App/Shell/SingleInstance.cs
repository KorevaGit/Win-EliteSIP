using System.Threading;

namespace EliteSIP.App.Shell;

/// <summary>
/// Один софтфон на пользователя, и не больше.
/// </summary>
///
/// <remarks>
/// <b>Зачем это нужно именно на Windows.</b> Приложение прячется в область
/// уведомлений, а спрятанное выглядит незапущенным: ярлык на рабочем столе,
/// плитка в меню «Пуск» и автозапуск — три двери в одну комнату, и рано или
/// поздно в них входят дважды.
///
/// Две копии — это не два окна, а испорченные настройки: обе пишут один
/// <c>settings.json</c> на каждую правку, и правки администратора теряются
/// молча — выигрывает та копия, которая записала последней. Плюс две
/// регистрации одного добавочного, два звонка на один входящий и драка за
/// микрофон.
///
/// <b>Замок на пользователя, а не на машину.</b> Имя без приставки
/// <c>Global\</c>: за одной машиной могут работать двое (быстрое переключение
/// пользователей), и у каждого свой профиль, свои настройки и свой добавочный.
/// Запрещать второму запускаться было бы запретом работать.
///
/// Второй запуск не молчит: он будит первого — тот показывает панель, — и
/// выходит. Так двойной щелчок по ярлыку делает ровно то, чего от него ждут.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\EliteSIP.SingleInstance";
    private const string WakeName = @"Local\EliteSIP.ShowPanel";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _wake;
    private readonly CancellationTokenSource _watching = new();

    private SingleInstance(Mutex mutex, EventWaitHandle wake)
    {
        _mutex = mutex;
        _wake = wake;
    }

    /// <summary>
    /// Занимает место единственной копии.
    /// </summary>
    ///
    /// <remarks>
    /// <see langword="null"/> означает «мы вторые»: первому уже сказано
    /// показаться, и звать больше нечего — надо просто выйти.
    /// </remarks>
    internal static SingleInstance? Claim()
    {
        // Событие заводится до захвата замка и обеими копиями: первая на нём
        // ждёт, вторая его дёргает. `EventWaitHandle` с именем — тот же объект
        // ядра у обоих процессов.
        EventWaitHandle wake = new(false, EventResetMode.AutoReset, WakeName);
        Mutex mutex = new(initiallyOwned: true, MutexName, out var isFirst);

        if (isFirst)
        {
            return new SingleInstance(mutex, wake);
        }

        // Разбудить первого и уйти. Ответа не ждём: первый мог зависнуть, а
        // второй запуск не должен из-за этого висеть на экране.
        _ = wake.Set();

        mutex.Dispose();
        wake.Dispose();

        return null;
    }

    /// <summary>
    /// Начинает слушать, не постучится ли вторая копия.
    /// </summary>
    ///
    /// <param name="showPanel">
    /// чем показать панель. Зовётся не в потоке интерфейса — перебрасывать в
    /// него дело вызывающего: у этого класса нет ни окна, ни диспетчера.
    /// </param>
    internal void Watch(Action showPanel)
    {
        // Своим потоком, а не задачей из пула: он почти всё время спит на
        // ожидании, и держать под это поток пула — значит держать его занятым
        // всю жизнь приложения.
        Thread thread = new(() =>
        {
            var handles = new[] { _wake, _watching.Token.WaitHandle };

            while (!_watching.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) == 0)
                {
                    showPanel();
                }
            }
        })
        {
            IsBackground = true,
            Name = "EliteSIP.SingleInstance",
        };

        thread.Start();
    }

    public void Dispose()
    {
        _watching.Cancel();

        // Замок отпускается явно: брошенный `Mutex` достаётся следующему
        // ожидающему с признаком «прежний владелец умер», и второй запуск
        // разбирался бы с этим вместо того, чтобы просто запуститься.
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Не наш — значит, уже отпущен. Дальше по списку.
        }

        _mutex.Dispose();
        _wake.Dispose();
        _watching.Dispose();
    }
}
