namespace EliteSIP.MediaCore;

/// <summary>
/// Джиттер-буфер: превращает поток RTP-пакетов, приходящих неровно и не по
/// порядку, в ровную последовательность кадров для воспроизведения.
///
/// Нужен потому, что сеть не гарантирует ни порядок, ни равномерность. Без
/// буфера звук рассыпается на щелчки уже при джиттере в десяток миллисекунд, а
/// со слишком большим буфером разговор превращается в рацию. Отсюда две
/// настройки: целевая глубина (компромисс задержки и устойчивости) и
/// предельная, после которой буфер догоняет реальное время.
///
/// Тип намеренно синхронный и без состояния сети: он полностью тестируется без
/// сокетов и без звуковой карты.
/// </summary>
public sealed class JitterBuffer
{
    /// <summary>
    /// Сколько кадров подряд можно спрятать повтором. Пять — это 100 мс:
    /// дольше повтор перестаёт быть незаметным и превращается в дребезг.
    /// </summary>
    public const int MaximumConcealmentRun = 5;

    /// <summary>
    /// Тишина в текущем кодеке. Нужна на самый первый кадр, когда повторять
    /// ещё нечего.
    /// </summary>
    private readonly byte[] _silencePayload;

    private readonly int _samplesPerFrame;
    private readonly int _clockRate;
    private readonly Dictionary<ushort, JitterFrame> _frames = [];

    // Оценка джиттера по RFC 3550 §6.4.1: сглаженное среднее отклонение
    // интервалов прихода от интервалов меток времени.
    private (double Time, uint Timestamp)? _previousArrival;
    private double _jitterEstimate;

    /// <summary>
    /// Сколько выдач подряд запас оказывался избыточным. Глубина растёт сразу,
    /// уменьшается только после долгой спокойной жизни — иначе буфер начинает
    /// дёргаться туда-сюда и щёлкать на каждом изменении.
    /// </summary>
    private int _calmPops;

    /// <summary>
    /// Последний по-настоящему пришедший кадр. Им и затыкается дыра: повтор
    /// звучит несравнимо лучше тишины, потому что сохраняет и громкость, и
    /// основной тон голоса.
    /// </summary>
    private ReadOnlyMemory<byte>? _lastGoodPayload;

    /// <summary>
    /// Сколько кадров подряд уже спрятано. Дальше предела повторять нельзя —
    /// получится заевшая пластинка.
    /// </summary>
    private int _concealmentRun;

    /// <summary>Номер кадра, который должен выйти следующим.</summary>
    private ushort? _nextSequence;

    /// <summary>
    /// Самый свежий номер, который вообще приходил. Нужен только для учёта
    /// перестановок: до начала воспроизведения <see cref="_nextSequence"/> ещё
    /// не задан, а пакеты уже могут приходить не по порядку.
    /// </summary>
    private ushort? _highestSequence;

    /// <summary>Сколько раз номер обернулся через ноль. Нужен только для RTCP.</summary>
    private int _sequenceCycles;

    private uint? _firstSequence;
    private long _highestAtLastReport;
    private long _receivedAtLastReport;
    private bool _isPrimed;

    public JitterBuffer(
        int targetDepth = 3,
        int minimumDepth = 2,
        int maximumDepth = 12,
        AudioCodec codec = AudioCodec.Pcmu,
        int packetTimeMilliseconds = AudioCodecInfo.DefaultPacketTimeMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDepth, minimumDepth);

        TargetDepth = Math.Clamp(targetDepth, minimumDepth, maximumDepth);
        MinimumDepth = minimumDepth;
        MaximumDepth = maximumDepth;
        _samplesPerFrame = (int)codec.TimestampIncrement(packetTimeMilliseconds);
        _clockRate = (int)codec.RtpClockRate();
        _silencePayload = codec.SilencePayload(packetTimeMilliseconds);
    }

    /// <summary>
    /// Сколько кадров копить перед началом воспроизведения.
    ///
    /// Не константа: подстраивается под замеренный джиттер сети. Фиксированное
    /// значение всегда неверно — на хорошей сети оно добавляет задержку зря, на
    /// плохой не спасает. Замеры на нашем же стенде давали то 7 недоборов за
    /// 45 секунд, то 34 за 21, при одной и той же тройке кадров.
    /// </summary>
    public int TargetDepth { get; private set; }

    /// <summary>Нижняя граница подстройки. Ниже двух кадров любая неровность — недобор.</summary>
    public int MinimumDepth { get; }

    /// <summary>Глубина, после которой буфер догоняет реальное время, выбрасывая старое.</summary>
    public int MaximumDepth { get; }

    public JitterStatistics Statistics { get; private set; } = new();

    /// <summary>Замеренный джиттер сети в миллисекундах. Для журнала и для RTCP.</summary>
    public double JitterMilliseconds => _jitterEstimate / _clockRate * 1000;

    /// <summary>Джиттер в единицах часов RTP — в таком виде он уезжает в отчёт RTCP.</summary>
    public uint JitterInClockUnits => (uint)Math.Clamp(_jitterEstimate, 0, uint.MaxValue);

    /// <summary>
    /// Самый свежий принятый номер, расширенный до 32 бит.
    ///
    /// В отчёте RTCP старшая половина — счётчик оборотов шестнадцатибитного
    /// номера. Без него собеседник не отличит первый круг от двадцатого и
    /// посчитает потери неверно после двадцати двух минут разговора.
    /// </summary>
    public uint ExtendedHighestSequenceNumber => ((uint)_sequenceCycles << 16) | (_highestSequence ?? 0);

    /// <summary>
    /// Сколько пакетов не доехало за всё время, в терминах RFC 3550: сколько
    /// должно было прийти минус сколько пришло.
    /// </summary>
    public int CumulativePacketsLost
    {
        get
        {
            if (_firstSequence is not uint first)
            {
                return 0;
            }

            long expected = ExtendedHighestSequenceNumber - (long)first + 1;
            return (int)Math.Clamp(expected - Statistics.Received, int.MinValue, int.MaxValue);
        }
    }

    public int Depth => _frames.Count;

    public bool IsEmpty => _frames.Count == 0;

    /// <summary>Доля потерь с прошлого отчёта, 0…1.</summary>
    public double FractionLostSinceLastReport()
    {
        long expected = ExtendedHighestSequenceNumber - _highestAtLastReport;
        long received = Statistics.Received - _receivedAtLastReport;

        _highestAtLastReport = ExtendedHighestSequenceNumber;
        _receivedAtLastReport = Statistics.Received;

        if (expected <= 0)
        {
            return 0;
        }

        long lost = Math.Max(expected - received, 0);
        return Math.Min((double)lost / expected, 1);
    }

    /// <summary>
    /// Кладёт пакет в буфер.
    ///
    /// Время прихода передаётся явно, а не берётся внутри: без этого оценку
    /// джиттера нельзя проверить тестом, а именно она решает, какую задержку
    /// будет держать разговор. Часы монотонные — настройка системного времени
    /// посреди разговора не должна выглядеть как всплеск джиттера.
    /// </summary>
    public void Push(RtpPacket packet, double? arrivedAt = null)
    {
        ArgumentNullException.ThrowIfNull(packet);

        double arrival = arrivedAt ?? MonotonicSeconds();
        Statistics = Statistics with { Received = Statistics.Received + 1 };
        UpdateJitter(packet, arrival);

        // Опоздавший пакет: его время уже прошло, вставлять некуда.
        if (_nextSequence is ushort next && IsOlder(packet.SequenceNumber, next))
        {
            Statistics = Statistics with { Late = Statistics.Late + 1 };
            return;
        }

        if (_frames.ContainsKey(packet.SequenceNumber))
        {
            Statistics = Statistics with { Duplicated = Statistics.Duplicated + 1 };
            return;
        }

        if (_highestSequence is ushort highest && IsOlder(packet.SequenceNumber, highest))
        {
            Statistics = Statistics with { Reordered = Statistics.Reordered + 1 };
        }
        else
        {
            // Оборот счётчика: новый номер меньше прежнего, хотя он новее.
            if (_highestSequence is ushort previous && packet.SequenceNumber < previous)
            {
                _sequenceCycles++;
            }

            _highestSequence = packet.SequenceNumber;
        }

        if (_firstSequence is null)
        {
            _firstSequence = packet.SequenceNumber;
            _highestAtLastReport = packet.SequenceNumber;
        }

        _frames[packet.SequenceNumber] = new JitterFrame(
            packet.SequenceNumber,
            packet.Timestamp,
            packet.Payload);

        TrimIfOverflowing();
    }

    /// <summary>
    /// Следующий кадр или <c>null</c>, если играть пока нечего.
    ///
    /// <c>null</c> означает «подожди»: буфер набирает запас. Потерянный или ещё
    /// не доехавший кадр возвращается как сокрытие — повтор последнего
    /// хорошего, — и это заметно лучше тишины: сохраняются и громкость, и
    /// основной тон, так что одиночная потеря на слух почти не читается.
    /// Затухание накладывает воспроизведение, ему для этого и сообщается
    /// <see cref="JitterFrame.IsConcealment"/>.
    /// </summary>
    public JitterFrame? Pop()
    {
        if (!_isPrimed)
        {
            // Пока не набралась целевая глубина, не начинаем: иначе первый же
            // всплеск джиттера вызовет недобор.
            if (_frames.Count < TargetDepth)
            {
                return null;
            }

            _isPrimed = true;
            _nextSequence = OldestSequence();
            _concealmentRun = 0;
        }

        if (_nextSequence is not ushort next)
        {
            return null;
        }

        if (_frames.Remove(next, out JitterFrame? frame))
        {
            _nextSequence = unchecked((ushort)(next + 1));
            _lastGoodPayload = frame.Payload;
            _concealmentRun = 0;
            AdaptTargetDepth();
            return frame;
        }

        // Ожидаемого кадра нет. Пустой буфер — это недобор, непустой — потеря
        // одного кадра, но играть в обоих случаях всё равно что-то надо.
        if (_frames.Count == 0)
        {
            if (_concealmentRun == 0)
            {
                Statistics = Statistics with { Underruns = Statistics.Underruns + 1 };
            }

            // Дальше предела повторять нельзя: если поток встал совсем, повтор
            // превратится в дребезг. Отдаём null и копим заново.
            if (_concealmentRun >= MaximumConcealmentRun)
            {
                _isPrimed = false;
                _concealmentRun = 0;

                // Ожидаемый номер сбрасывается вместе с накоплением. Иначе он
                // остаётся впереди на все спрятанные кадры, и первый же пакет,
                // пришедший после перерыва, будет отвергнут как опоздавший —
                // разговор после короткого пропадания сети не восстановится.
                _nextSequence = null;
                return null;
            }
        }

        Statistics = Statistics with { Concealed = Statistics.Concealed + 1 };
        _concealmentRun++;
        _nextSequence = unchecked((ushort)(next + 1));
        return new JitterFrame(next, 0, _lastGoodPayload ?? _silencePayload, IsConcealment: true);
    }

    public void Reset()
    {
        _frames.Clear();
        _nextSequence = null;
        _highestSequence = null;
        _isPrimed = false;
        _lastGoodPayload = null;
        _concealmentRun = 0;
        _previousArrival = null;
        _jitterEstimate = 0;
        _calmPops = 0;
        _sequenceCycles = 0;
        _firstSequence = null;
        _highestAtLastReport = 0;
        _receivedAtLastReport = 0;
    }

    public void ResetStatistics() => Statistics = new JitterStatistics();

    /// <summary>
    /// Сравнение с учётом того, что номер шестнадцатибитный и переполняется.
    ///
    /// Наивное <c>a &lt; b</c> ломается ровно один раз на каждые 65536
    /// пакетов — это примерно раз в 22 минуты разговора при 20 мс на пакет.
    /// Ошибка выглядит как секунда тишины на ровном месте, и найти её потом
    /// почти невозможно.
    /// </summary>
    internal static bool IsOlder(ushort left, ushort right) => unchecked((short)(left - right)) < 0;

    /// <summary>
    /// Монотонные часы: настройка системного времени посреди разговора не
    /// должна выглядеть как всплеск джиттера. Аналог <c>systemUptime</c>.
    /// </summary>
    /// <remarks>
    /// Именно <c>Stopwatch</c>, а не <c>TickCount64</c>: у последнего шаг
    /// 15,6 мс, то есть крупнее самого джиттера, который мы измеряем.
    /// </remarks>
    internal static double MonotonicSeconds() =>
        (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Оценка джиттера по RFC 3550 §6.4.1.
    ///
    /// Считается разница между тем, насколько разошлись приходы пакетов, и тем,
    /// насколько разошлись их метки времени. Для ровного потока она нулевая;
    /// всё, что сеть добавила от себя, оседает здесь. Сглаживание с
    /// коэффициентом 1/16 — из того же параграфа: оно достаточно инертно, чтобы
    /// один опоздавший пакет не раздувал буфер.
    /// </summary>
    private void UpdateJitter(RtpPacket packet, double arrivedAt)
    {
        if (_previousArrival is (double time, uint timestamp))
        {
            double arrivalDelta = (arrivedAt - time) * _clockRate;

            // Метки времени тоже переполняются, поэтому разность берётся со
            // знаком через int — иначе один переход через ноль даёт джиттер в
            // сутки.
            double timestampDelta = unchecked((int)(packet.Timestamp - timestamp));
            double deviation = Math.Abs(arrivalDelta - timestampDelta);

            _jitterEstimate += (deviation - _jitterEstimate) / 16;
        }

        _previousArrival = (arrivedAt, packet.Timestamp);
    }

    /// <summary>
    /// Пересчитывает целевую глубину под замеренный джиттер.
    ///
    /// Запас — удвоенный джиттер плюс кадр. Удвоение не суеверие: оценка по RFC
    /// это среднее отклонение, а держать надо близко к пику, иначе половина
    /// всплесков окажется недоборами.
    ///
    /// Растёт глубина сразу, а уменьшается только после долгого спокойствия.
    /// Несимметрично намеренно: не набрать вовремя — это слышимый провал, а
    /// лишний кадр запаса — двадцать миллисекунд, которых никто не замечает.
    /// </summary>
    private void AdaptTargetDepth()
    {
        double needed = ((2 * _jitterEstimate) + _samplesPerFrame) / _samplesPerFrame;
        int desired = Math.Clamp((int)Math.Ceiling(needed), MinimumDepth, MaximumDepth);

        if (desired > TargetDepth)
        {
            TargetDepth = desired;
            _calmPops = 0;
            return;
        }

        if (desired >= TargetDepth)
        {
            _calmPops = 0;
            return;
        }

        // 250 выдач — это пять секунд разговора при 20 мс на кадр.
        _calmPops++;
        if (_calmPops >= 250)
        {
            TargetDepth--;
            _calmPops = 0;
        }
    }

    /// <summary>
    /// Если буфер распух, догоняем реальное время: иначе задержка растёт и уже
    /// не возвращается — разговор превращается в переписку.
    /// </summary>
    private void TrimIfOverflowing()
    {
        if (_frames.Count <= MaximumDepth)
        {
            return;
        }

        while (_frames.Count > TargetDepth && OldestSequence() is ushort oldest)
        {
            _frames.Remove(oldest);
            Statistics = Statistics with { Dropped = Statistics.Dropped + 1 };
        }

        _nextSequence = OldestSequence();
    }

    /// <summary>Самый старый номер в буфере с учётом переполнения счётчика.</summary>
    private ushort? OldestSequence()
    {
        ushort? oldest = null;
        foreach (ushort sequence in _frames.Keys)
        {
            if (oldest is not ushort current || IsOlder(sequence, current))
            {
                oldest = sequence;
            }
        }

        return oldest;
    }
}

/// <summary>Кадр на выходе джиттер-буфера.</summary>
/// <param name="IsConcealment">Кадр не пришёл и сгенерирован взамен потерянного.</param>
public sealed record JitterFrame(
    ushort SequenceNumber,
    uint Timestamp,
    ReadOnlyMemory<byte> Payload,
    bool IsConcealment = false);

/// <summary>Счётчики приёма. Показываются в журнале и уезжают в RTCP.</summary>
public sealed record JitterStatistics
{
    public int Received { get; init; }

    /// <summary>Пакеты, пришедшие после того, как их время уже прошло.</summary>
    public int Late { get; init; }

    public int Duplicated { get; init; }

    /// <summary>Кадры, которых не дождались и заменили заглушкой.</summary>
    public int Concealed { get; init; }

    /// <summary>Пакеты, выброшенные при переполнении буфера.</summary>
    public int Dropped { get; init; }

    /// <summary>Сколько раз буфер опустел и играть было нечего.</summary>
    public int Underruns { get; init; }

    /// <summary>Пакеты, пришедшие не в том порядке, но вовремя.</summary>
    public int Reordered { get; init; }
}
