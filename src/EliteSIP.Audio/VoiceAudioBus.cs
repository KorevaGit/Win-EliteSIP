namespace EliteSIP.Audio;

/// <summary>
/// Один аудиотракт на приложение, который берут по очереди.
///
/// <b>Зачем.</b> Микрофон, выход и обработка голоса у оператора одни, а
/// разговоров до трёх, и активная аудиолиния всегда одна. Раньше это выражалось
/// объектом тракта на каждый звонок: фоновые линии держали свои остановленные
/// движки, активная — работающий.
///
/// <b>Почему это не перенос причины, а перенос решения.</b> На macOS общий
/// тракт появился после падения: разбор <c>AVAudioEngine</c> приходился ровно
/// на виток отбоя и накладывался на отложенный блок приватной очереди
/// CoreAudio. Этой причины на Windows нет — приватных очередей AVFAudio здесь
/// не существует. Но само решение остаётся верным по другой, своей причине, и
/// она записана в замерах W0: <b>открытие потока захвата переводит
/// Bluetooth-гарнитуру в режим связи</b> — моно 8 кГц и приглушённый звук во
/// всей системе. Тракт, который собирается и разбирается на каждый звонок,
/// щёлкает этим режимом на каждом звонке, и слышит это не только оператор, а
/// вся система.
///
/// <b>Арбитраж маршрута не переносится.</b> В оригинале шина умела просить
/// систему отдать ей ближайшую беспроводную гарнитуру
/// (<c>AVAudioRoutingArbiter</c>) — так FaceTime забирает AirPods у соседнего
/// iPhone. В Windows такого понятия нет вовсе: гарнитура принадлежит той
/// машине, с которой сопряжена, и договариваться не с кем и не о чем. Здесь от
/// этого остаётся ровно ничего — не заглушка, а отсутствие: подпирать
/// несуществующее API пустым методом значит однажды дать кому-то повод его
/// «починить».
///
/// <b>Замок один, а не два.</b> В оригинале порядок операций держала
/// последовательная очередь, а ключ владельца жил под отдельным
/// <c>os_unfair_lock</c>, потому что захват занимает до восьми десятых секунды
/// на открытии устройства, а спин-замок столько держать нельзя. Здесь замок
/// обычный, и держать его долго — законно; двухуровневая схема воспроизвела бы
/// только сложность, а не её причину.
/// </summary>
public sealed class VoiceAudioBus : IDisposable
{
    /// <summary>
    /// Через сколько отдавать устройство после того, как тракт освободили.
    ///
    /// Секунда — это компромисс из оригинала, и он не про скорость. Меньше —
    /// устройство пересобирается между двумя быстрыми звонками, и оператор
    /// платит паузой в начале второго. Больше — гарнитура заметно дольше висит
    /// в режиме связи, а на Windows это значит приглушённый звук во всей
    /// системе.
    /// </summary>
    public static readonly TimeSpan DefaultRetirementDelay = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Func<VoiceAudioConfiguration, IVoiceAudioEngine> _factory;
    private readonly DelayedWorkScheduler _schedule;
    private readonly TimeSpan _retirementDelay;
    private readonly AudioOwnership _ownership = new();

    private IVoiceAudioEngine _current;
    private IDisposable? _retirement;
    private VoiceAudioConfiguration? _lastConfiguration;
    private bool _disposed;

    /// <param name="engine">Тракт, с которого начинаем.</param>
    /// <param name="factory">Чем собирать сменный после освобождения устройства.</param>
    /// <param name="scheduler">Чем откладывать освобождение. По умолчанию — таймер.</param>
    /// <param name="retirementDelay">Отсрочка освобождения.</param>
    public VoiceAudioBus(
        IVoiceAudioEngine engine,
        Func<VoiceAudioConfiguration, IVoiceAudioEngine> factory,
        DelayedWorkScheduler? scheduler = null,
        TimeSpan? retirementDelay = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(factory);

        _current = engine;
        _factory = factory;
        _schedule = scheduler ?? ScheduleOnTimer;
        _retirementDelay = retirementDelay ?? DefaultRetirementDelay;
    }

    /// <summary>Держит ли тракт хоть кто-нибудь.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _ownership.IsBusy;
            }
        }
    }

    /// <summary>
    /// Наш ли звук сейчас.
    ///
    /// Нужен фоновым линиям: своего тракта у них больше нет, и это единственный
    /// способ не тронуть чужой.
    /// </summary>
    public bool IsOwner(AudioOwnerToken token)
    {
        lock (_gate)
        {
            return _ownership.IsOwner(token);
        }
    }

    /// <summary>
    /// Забирает тракт себе и запускает его.
    ///
    /// Прежний владелец отпускается здесь же и до запуска, а не оставляется на
    /// совесть вызывающего. Порядок обязателен: два запущенных тракта на одном
    /// устройстве делят его между собой, а Bluetooth-гарнитуру держат в режиме
    /// связи всё время, пока жив хоть один.
    /// </summary>
    public void Claim(
        AudioOwnerToken token,
        VoiceAudioConfiguration configuration,
        VoiceAudioHandlers handlers)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(handlers);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Освобождение устройства отменяется: оно снова нужно. Заодно это
            // быстрый путь для звонка сразу после отбоя — устройство не успело
            // закрыться.
            CancelRetirement();
            _lastConfiguration = configuration;

            IVoiceAudioEngine engine = _current;
            engine.Stop();

            // Владение снимается до перестройки, а не после успешного запуска:
            // пока тракт перестраивается, звук не идёт ни у кого, и считать его
            // своим не должна ни прежняя линия, ни новая.
            _ownership.ReleaseAny();

            engine.Reconfigure(configuration);
            engine.Handlers = handlers;

            try
            {
                engine.Start();
            }
            catch
            {
                // Тракт не поднялся — владельцем никто не становится, иначе
                // линия считала бы своим звук, которого нет, и не отдала бы его
                // следующей.
                engine.Handlers = VoiceAudioHandlers.None;
                throw;
            }

            _ownership.Take(token);
        }
    }

    /// <summary>
    /// Отпускает тракт, если он всё ещё за этим владельцем.
    ///
    /// Чужой ключ — тихий отказ, а не ошибка: сюда приходят и по отбою, и от
    /// запоздавшей финализации снятой сессии, и второй раз подряд. Единственное,
    /// чего делать нельзя, — заглушить разговор, который сейчас идёт на другой
    /// линии.
    /// </summary>
    public bool Release(AudioOwnerToken token)
    {
        lock (_gate)
        {
            if (_disposed || !_ownership.Release(token))
            {
                return false;
            }

            _current.Stop();
            _current.Handlers = VoiceAudioHandlers.None;
            ScheduleRetirement();
            return true;
        }
    }

    /// <summary>
    /// Даёт добраться до тракта тому, кто им владеет.
    ///
    /// Возврат <c>false</c> означает «звук сейчас не ваш» — для фоновой линии
    /// это обычное состояние, а не отказ.
    /// </summary>
    public bool WithEngine(AudioOwnerToken token, Action<IVoiceAudioEngine> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return TryWithEngine<object?>(token, engine => { body(engine); return null; }, out _);
    }

    /// <summary>То же, но с ответом: уровни, счётчики, состояние.</summary>
    public bool TryWithEngine<TResult>(
        AudioOwnerToken token,
        Func<IVoiceAudioEngine, TResult> body,
        out TResult result)
    {
        ArgumentNullException.ThrowIfNull(body);

        IVoiceAudioEngine engine;
        lock (_gate)
        {
            if (_disposed || !_ownership.IsOwner(token))
            {
                result = default!;
                return false;
            }

            engine = _current;
        }

        // Замок отпущен до вызова: держать его, пока чужой код читает уровни,
        // незачем. Подменить тракт в этот момент некому — смена бывает только
        // на свободном.
        result = body(engine);
        return true;
    }

    /// <summary>Пересобирает тракт по требованию владельца.</summary>
    public bool Restart(AudioOwnerToken token, string reason) =>
        WithEngine(token, engine => engine.Restart(reason));

    public void Dispose()
    {
        IVoiceAudioEngine engine;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelRetirement();
            engine = _current;
        }

        engine.Stop();
        engine.Dispose();
    }

    /// <summary>
    /// Ставит устройство на освобождение. Вызывается под замком.
    ///
    /// <b>Зачем освобождать то, ради чего всё затевалось.</b> Общий тракт
    /// появился, чтобы устройство не пересобиралось на каждом звонке, — и это
    /// по-прежнему так. Но замеры W0 говорят, что Bluetooth-гарнитуру
    /// возвращает из режима связи только закрытие потока захвата, а не его
    /// остановка. Значит вопрос не в том, освобождать ли, а <b>когда</b>: не в
    /// витке отбоя, где оператор ждёт, и не сразу, потому что следующий звонок
    /// может начаться через секунду.
    ///
    /// Звонок, начатый раньше срока, освобождение отменяет.
    /// </summary>
    private void ScheduleRetirement()
    {
        CancelRetirement();
        _retirement = _schedule(_retirementDelay, Retire);
    }

    private void CancelRetirement()
    {
        _retirement?.Dispose();
        _retirement = null;
    }

    /// <summary>Отдаёт устройство системе и заводит сменный тракт.</summary>
    private void Retire()
    {
        IVoiceAudioEngine? previous = null;

        lock (_gate)
        {
            _retirement = null;

            // Отмена не останавливает работу, которая уже началась: и таймер, и
            // очередь оригинала ведут себя одинаково. Поэтому проверка здесь —
            // не перестраховка, а единственное, что отделяет освобождение
            // устройства от разговора, начавшегося миллисекунду назад.
            if (_disposed || _ownership.IsBusy)
            {
                return;
            }

            // Выключенное «отпускать устройство» означает ровно это: тракт
            // живёт дальше и держит устройство. На проводной гарнитуре
            // отпускать нечего, а лишняя пересборка стоит паузы в начале
            // следующего звонка.
            if (_lastConfiguration?.ReleasesDeviceWhenIdle == false)
            {
                return;
            }

            IVoiceAudioEngine replacement;
            try
            {
                replacement = _factory(_lastConfiguration ?? new VoiceAudioConfiguration());
            }
#pragma warning disable CA1031 // намеренно: см. комментарий
            catch
            {
                // Свежий не собрался — оставляем прежний. Разговор без
                // эхоподавления плох, разговор без тракта невозможен вовсе.
                return;
            }
#pragma warning restore CA1031

            previous = _current;
            _current = replacement;
        }

        // Прежний разбирается вне замка: на Windows закрытие потоков WASAPI —
        // это ожидание звуковой службы, и держать на нём захват тракта значило
        // бы добавить эту паузу к началу следующего звонка. Гонки нет: из
        // общего состояния он уже вынут, и достать его больше неоткуда.
        previous.Stop();
        previous.Dispose();
    }

    private static Timer ScheduleOnTimer(TimeSpan delay, Action work)
    {
        // Таймер освобождает себя сам после срабатывания, а до него его
        // освобождает отмена. Двойное освобождение таймера безопасно, и на нём
        // здесь всё и держится: отмена и срабатывание могут прийти
        // одновременно.
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
