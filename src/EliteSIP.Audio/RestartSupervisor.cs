namespace EliteSIP.Audio;

/// <summary>
/// Склеивает пачку поводов пересобрать тракт в одну пересборку.
///
/// <b>Зачем склейка.</b> Одно выдёргивание гарнитуры порождает не одно
/// уведомление, а очередь: устройство сменило состояние, устройство исчезло,
/// сменилось умолчание для связи, — и это только по одной конечной точке, а у
/// гарнитуры их две. Плюс к тому потоки звука в этот же момент получают отказ
/// от драйвера и тоже просят пересобрать. Пересобирать на каждый повод значит
/// открывать и закрывать устройство пять раз подряд, и оператор услышит пять
/// пауз вместо одной.
///
/// <b>Окно склейки — те же триста миллисекунд, что и первая отсрочка
/// <see cref="AudioRestartPolicy"/>, и это не совпадение.</b> Пока устройство в
/// переходе, пересобирать бесполезно: получим тот же отказ и потратим попытку
/// зря. Окно и отсрочка меряют одно и то же — сколько система приходит в себя.
///
/// <b>Первое уведомление задаёт срок, последующие только добавляют повод.</b>
/// Не «сдвинуть срок ещё на окно»: непрерывный поток уведомлений — а Bluetooth
/// умеет их лить — откладывал бы пересборку бесконечно, и тракт не собрался бы
/// никогда. Так пересборка гарантированно случается не позже чем через окно
/// после первого повода.
///
/// <b>Одна пересборка за раз.</b> Пересборка занимает до восьми десятых секунды
/// на открытии устройства, а поводы за это время придут снова — уже от неё
/// самой. Вторая, начатая поверх первой, разбирала бы то, что первая собирает.
/// </summary>
public sealed class RestartSupervisor : IDisposable
{
    /// <summary>Окно склейки. См. описание типа.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(300);

    private readonly Lock _gate = new();
    private readonly DelayedWorkScheduler _schedule;
    private readonly TimeSpan _window;
    private readonly Action<string> _rebuild;

    private IDisposable? _pending;
    private string? _reason;
    private bool _running;
    private bool _disposed;

    /// <param name="rebuild">
    /// Что делать по истечении окна. Вызывается не на том потоке, который
    /// сообщил о поводе: уведомления приходят на рабочем потоке звуковой
    /// службы, и пересобирать тракт на нём нельзя — он держит замок, пока
    /// разносит уведомления.
    /// </param>
    /// <param name="scheduler">Чем откладывать. По умолчанию — таймер.</param>
    /// <param name="window">Окно склейки.</param>
    public RestartSupervisor(
        Action<string> rebuild,
        DelayedWorkScheduler? scheduler = null,
        TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(rebuild);

        _rebuild = rebuild;
        _schedule = scheduler ?? ScheduleOnTimer;
        _window = window ?? DefaultWindow;
    }

    /// <summary>Сколько пересборок надзиратель запустил. Для журнала и для проверок.</summary>
    public int RebuildCount { get; private set; }

    /// <summary>Сколько поводов было склеено в уже назначенные пересборки.</summary>
    public int CoalescedCount { get; private set; }

    /// <summary>Сообщает повод пересобрать тракт.</summary>
    /// <param name="reason">Что случилось. Первый повод в пачке и попадёт в журнал.</param>
    public void Notify(string reason)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pending is not null)
            {
                // Срок уже назначен. Второй повод его не двигает — см. описание
                // типа.
                CoalescedCount++;
                return;
            }

            _reason = reason;
            _pending = _schedule(_window, Fire);
        }
    }

    public void Dispose()
    {
        IDisposable? pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pending;
            _pending = null;
        }

        pending?.Dispose();
    }

    private void Fire()
    {
        string reason;
        lock (_gate)
        {
            _pending = null;

            // Отмена не останавливает работу, которая уже началась. Проверка
            // здесь — единственное, что отделяет пересборку от тракта, который
            // секунду назад остановили.
            if (_disposed || _running)
            {
                return;
            }

            reason = _reason ?? "смена устройств";
            _reason = null;
            _running = true;
            RebuildCount++;
        }

        try
        {
            _rebuild(reason);
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private static Timer ScheduleOnTimer(TimeSpan delay, Action work)
    {
        Timer? timer = null;
        timer = new Timer(
            _ =>
            {
                timer!.Dispose();
                work();
            },
            state: null,
            dueTime: delay,
            period: Timeout.InfiniteTimeSpan);

        return timer;
    }
}
