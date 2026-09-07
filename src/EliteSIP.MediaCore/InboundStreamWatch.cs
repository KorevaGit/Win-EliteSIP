namespace EliteSIP.MediaCore;

/// <summary>
/// Что происходит с входящим потоком, без подробностей.
///
/// Отдельный тип понадобился после живого прогона: в архиве для поддержки
/// 62 строки из 266 оказались одним и тем же предупреждением, повторённым
/// двадцать раз в секунду. Причина — счётчик секунд внутри состояния: он растёт
/// на каждом опросе, и сравнение «состояние то же?» отвечало «нет» всегда.
/// Замысел был обратный — сказать один раз на переходе.
/// </summary>
public enum InboundStreamKind
{
    /// <summary>Пакеты идут. Обычное состояние, сообщать не о чем.</summary>
    Flowing,

    /// <summary>Разговор идёт, но не пришло ещё ни одного пакета.</summary>
    NeverStarted,

    /// <summary>Поток шёл и прекратился.</summary>
    Stalled,
}

/// <summary>
/// Состояние входящего потока. Секунды нужны в самом сообщении, но в решении
/// «говорить ли» не участвуют — для него есть <see cref="Kind"/>.
/// </summary>
public sealed record InboundStreamState(InboundStreamKind Kind, double Seconds = 0);

/// <summary>
/// Следит за тем, идёт ли вообще поток от собеседника.
///
/// <b>Зачем.</b> Разговор, в котором не пришло ни одного RTP-пакета, снаружи
/// выглядит ровно так же, как сломанный звук: в трубке тишина. Разница
/// принципиальная — в первом случае чинить надо сеть или собеседника, во втором
/// нас, — но по звуку она неразличима, и разбор каждый раз начинается с
/// нескольких неверных гипотез.
///
/// Случай не выдуманный. Разговор на стенде 3 августа 2026: 29 секунд, принято
/// <b>ноль</b> пакетов, при этом RTCP собеседника исправно приходил и сообщал
/// 0 % потерь на нашем потоке. То есть мы его слышать не могли по причине, к
/// аудиотракту отношения не имеющей, — а приложение за все 29 секунд не сказало
/// об этом ни слова.
///
/// <b>Что это не.</b> Не замена статистике потерь: одиночные потери и джиттер —
/// дело джиттер-буфера, он их скрывает и считает. Здесь ловится грубое —
/// «поток не начинался» и «поток кончился», то есть состояния, в которых
/// скрывать уже нечего.
///
/// Тип синхронный и без часов внутри: момент передаётся аргументом, поэтому
/// проверяется тестом целиком.
/// </summary>
public sealed class InboundStreamWatch
{
    private int _lastCount;
    private double? _lastGrowth;
    private double? _start;
    private bool _hasEverReceived;

    public InboundStreamWatch(double startupGrace = 3, double stallTimeout = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startupGrace);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stallTimeout);

        StartupGrace = startupGrace;
        StallTimeout = stallTimeout;
    }

    /// <summary>
    /// Сколько ждать первого пакета, прежде чем сказать о его отсутствии.
    ///
    /// Три секунды: RTP начинается сразу за подтверждением разговора, и любая
    /// законная задержка старта укладывается в доли секунды. Меньше — поймаем
    /// нормальный разбег и напугаем оператора зря.
    /// </summary>
    public double StartupGrace { get; }

    /// <summary>
    /// Сколько терпеть перерыв в уже идущем потоке.
    ///
    /// Две секунды: сокрытие потерь работает до 60 мс, джиттер-буфер держит
    /// запас в кадрах, то есть в десятках миллисекунд. Две секунды — это уже не
    /// сеть дрогнула, а поток встал.
    /// </summary>
    public double StallTimeout { get; }

    /// <summary>
    /// Принимает очередной замер счётчика принятых пакетов.
    ///
    /// Считает по приросту, а не по абсолютному значению: важно не сколько
    /// пришло всего, а идёт ли поток прямо сейчас.
    /// </summary>
    public InboundStreamState Update(int received, double? at = null)
    {
        double now = at ?? JitterBuffer.MonotonicSeconds();
        double start = _start ??= now;

        if (received > _lastCount)
        {
            _lastCount = received;
            _lastGrowth = now;
            _hasEverReceived = true;
            return new InboundStreamState(InboundStreamKind.Flowing);
        }

        if (!_hasEverReceived)
        {
            double waiting = Math.Max(now - start, 0);
            return waiting >= StartupGrace
                ? new InboundStreamState(InboundStreamKind.NeverStarted, waiting)
                : new InboundStreamState(InboundStreamKind.Flowing);
        }

        double quiet = Math.Max(now - (_lastGrowth ?? start), 0);
        return quiet >= StallTimeout
            ? new InboundStreamState(InboundStreamKind.Stalled, quiet)
            : new InboundStreamState(InboundStreamKind.Flowing);
    }

    public void Reset()
    {
        _lastCount = 0;
        _lastGrowth = null;
        _start = null;
        _hasEverReceived = false;
    }
}
