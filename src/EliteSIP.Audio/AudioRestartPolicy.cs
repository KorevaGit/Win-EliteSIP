namespace EliteSIP.Audio;

/// <summary>Что делать после очередной неудачной пересборки.</summary>
public enum RestartAction
{
    /// <summary>Пробуем ещё раз.</summary>
    Retry,

    /// <summary>Отступаемся: звука больше не будет, разговор пора закрывать.</summary>
    GiveUp,
}

/// <summary>Решение по одной неудаче.</summary>
/// <param name="Action">Что делать.</param>
/// <param name="Delay">Через сколько повторять. Осмысленно только при <see cref="RestartAction.Retry"/>.</param>
/// <param name="Attempt">Номер попытки, начиная с первой. Нужен для сообщения оператору.</param>
/// <param name="Elapsed">Сколько прошло с первой неудачи. Осмысленно только при <see cref="RestartAction.GiveUp"/>.</param>
public readonly record struct RestartDecision(
    RestartAction Action,
    TimeSpan Delay,
    int Attempt,
    TimeSpan Elapsed);

/// <summary>
/// Что делать, когда тракт не удалось пересобрать.
///
/// <b>Зачем отдельный тип.</b> До этого политики не было вовсе: одна неудачная
/// пересборка означала конец разговора. Движок ловил любую ошибку, объявлял
/// тракт сломанным, и приложение вешало трубку. Для настоящей потери
/// устройства это верно — молчащий разговор хуже завершённого, — но для
/// <b>временной</b> это приговор без вины.
///
/// А временная — обычный случай. На macOS его давал перевод AirPods на телефон
/// и обратно; на Windows тот же вид отказа даёт смена режима Bluetooth-канала
/// (см. <c>docs/W0-AUDIO.md</c>: открытие микрофона переводит гарнитуру в
/// узкополосный режим связи, и это пересоздание конечной точки, а не изменение
/// её свойств), а также перезапуск аудиослужбы и приход
/// <c>AUDCLNT_E_DEVICE_INVALIDATED</c> на исправном устройстве. В этом окне
/// система законно отказывает, и отказ окончательным не является.
///
/// <b>Почему это отдельный тип, а не пара полей в движке.</b> Ровно затем,
/// чтобы эту логику можно было проверить тестом. Движок без звуковой карты не
/// заводится, поэтому всё, что живёт внутри него, проверяется только руками — и
/// именно поэтому дефект дожил до боя. Здесь нет ни устройства, ни времени из
/// системных часов: момент передаётся аргументом.
///
/// <b>Класс, а не структура</b> — по той же причине, что и
/// <see cref="AudioOwnership"/>: изменяемая структура в C# молча копируется, и
/// серия попыток считалась бы заново на каждой копии, то есть не кончалась бы
/// никогда.
/// </summary>
public sealed class AudioRestartPolicy
{
    /// <summary>
    /// Отсрочка перед первой повторной попыткой.
    ///
    /// Совпадает с окном склейки уведомлений о смене конфигурации, и это не
    /// совпадение: если устройство ещё в переходе, повторить раньше — значит
    /// получить тот же отказ и потратить попытку зря.
    /// </summary>
    public TimeSpan FirstDelay { get; }

    /// <summary>
    /// Потолок отсрочки. Дальше растить бессмысленно: разговор идёт, и редкие
    /// попытки означают лишние секунды тишины после того, как устройство уже
    /// вернулось.
    /// </summary>
    public TimeSpan MaximumDelay { get; }

    /// <summary>
    /// Сколько всего терпим, считая от первой неудачи.
    ///
    /// Десять секунд — это компромисс между двумя одинаково плохими исходами.
    /// Меньше — обрыв на обычном переходе Bluetooth, ради которого всё и
    /// затевалось. Больше — оператор десятки секунд говорит в тишину и всё
    /// равно теряет разговор, только позже и злее.
    /// </summary>
    public TimeSpan Budget { get; }

    private int _attempts;
    private TimeSpan? _firstFailure;
    private TimeSpan _nextDelay;

    public AudioRestartPolicy()
        : this(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10))
    {
    }

    public AudioRestartPolicy(TimeSpan firstDelay, TimeSpan maximumDelay, TimeSpan budget)
    {
        if (firstDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(firstDelay), "нулевая отсрочка — это цикл на отказе");
        }

        if (maximumDelay < firstDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay), "потолок не может быть меньше первой отсрочки");
        }

        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "нулевой запас терпения — это прежнее поведение");
        }


        FirstDelay = firstDelay;
        MaximumDelay = maximumDelay;
        Budget = budget;
        _nextDelay = firstDelay;
    }

    /// <summary>
    /// Идёт ли сейчас серия попыток. По этому признаку движок отличает
    /// «тракта нет, но мы его чиним» от «тракта нет и не будет».
    /// </summary>
    public bool IsRecovering => _firstFailure is not null;

    /// <summary>Сколько попыток уже сделано в текущей серии.</summary>
    public int AttemptCount => _attempts;

    /// <summary>
    /// Записывает неудачу и говорит, что делать дальше.
    ///
    /// Момент передаётся аргументом, а не берётся из часов: иначе проверить
    /// исчерпание запаса можно было бы только реальным ожиданием. Часы обязаны
    /// быть монотонными — перевод системного времени посреди разговора не
    /// должен ни продлевать запас, ни сжигать его. На Windows это
    /// <see cref="System.Diagnostics.Stopwatch"/>, а не <c>DateTime.Now</c>:
    /// у второго перевод часов и переход на летнее время выглядят как скачок
    /// на час, и запас терпения сгорел бы мгновенно.
    /// </summary>
    public RestartDecision RecordFailure(TimeSpan now)
    {
        _attempts++;
        var start = _firstFailure ?? now;
        _firstFailure = start;

        var elapsed = now - start;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        // Проверяем ДО выдачи отсрочки, а не после неё: иначе последняя попытка
        // назначается на момент, когда запас уже кончился, и оператор ждёт
        // отсрочку впустую.
        if (elapsed + _nextDelay > Budget)
        {
            return new RestartDecision(RestartAction.GiveUp, TimeSpan.Zero, _attempts, elapsed);
        }

        var delay = _nextDelay;
        var doubled = _nextDelay + _nextDelay;
        _nextDelay = doubled > MaximumDelay ? MaximumDelay : doubled;
        return new RestartDecision(RestartAction.Retry, delay, _attempts, elapsed);
    }

    /// <summary>Тракт собрался — серия закончена, счётчики в исходное.</summary>
    public void RecordSuccess()
    {
        _attempts = 0;
        _firstFailure = null;
        _nextDelay = FirstDelay;
    }
}
