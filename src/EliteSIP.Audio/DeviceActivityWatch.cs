namespace EliteSIP.Audio;

/// <summary>Что на самом деле происходит с открытым устройством.</summary>
public enum DeviceActivity
{
    /// <summary>Только открылись. Судить рано.</summary>
    Warmup,

    /// <summary>Устройство отдаёт звук ровно.</summary>
    Working,

    /// <summary>
    /// Устройство есть и открылось, но звука почти не даёт.
    ///
    /// Это и есть выключенная гарнитура при воткнутом донгле, поднятый на
    /// штанге микрофон, гарнитура, вышедшая из радиуса. Для системы всё в
    /// порядке; для разговора — нет.
    /// </summary>
    SilentButPresent,

    /// <summary>
    /// Устройство отдаёт звук рвано: разрывы потока идут потоком.
    ///
    /// Отличается от предыдущего тем, что звук есть, но негодный. Лечится
    /// пересборкой тракта, а не сообщением оператору «включите гарнитуру».
    /// </summary>
    Faulty,
}

/// <summary>
/// Различает три состояния открытого устройства: работает, молчит, сбоит.
///
/// <b>Это требование W0, а не курьёз.</b> Донгл JBL Quantum350 остаётся в
/// системе, и его конечные точки числятся действующими, даже когда самой
/// гарнитуры рядом нет: поток открывается без ошибки и отдаёт мусор. В замерах
/// это выглядело так:
///
/// <list type="bullet">
/// <item>исправная проводная гарнитура: 99,98% отданного звука, 0–2 разрыва за
/// весь прогон;</item>
/// <item>донгл с выключенной гарнитурой: 79% за 57 с и 2923 разрыва, то есть
/// больше пятидесяти в секунду.</item>
/// </list>
///
/// Разница на два порядка, и другого способа её увидеть нет: система на оба
/// случая отвечает одинаково — «устройство действует». <b>Звонить будет
/// оператор, а не инженер</b>, и «ничего не слышно» он объяснить не сможет;
/// объяснить обязаны мы.
///
/// <b>Про первый разрыв.</b> Флаг разрыва на первом пакете после запуска
/// приходит всегда и означает начало потока, а не потерю звука. Считать его
/// сбоем — значит объявлять исправный тракт рваным на каждом прогоне и
/// привыкнуть не замечать настоящий разрыв. Здесь он не считается: см.
/// <see cref="NoteDiscontinuity"/>.
/// </summary>
public sealed class DeviceActivityWatch
{
    /// <summary>
    /// Ниже этой доли отданного звука устройство считается молчащим.
    ///
    /// Девять десятых — это середина между 99,98% исправного и 79% пустого
    /// донгла, ближе к исправному. Настоящая заминка планировщика столько не
    /// съедает: в замерах W0 худший исправный прогон отдал 99,977%.
    /// </summary>
    public const double SilenceThreshold = 0.9;

    /// <summary>
    /// Выше этого числа разрывов в секунду устройство считается сбоящим.
    ///
    /// Пять — это на порядок ниже пятидесяти у пустого донгла и на порядок выше
    /// того, что даёт исправное устройство (единицы за весь прогон).
    /// </summary>
    public const double FaultThreshold = 5.0;

    /// <summary>
    /// Сколько не судить после запуска.
    ///
    /// Захват разгоняется: первые пакеты приходят с задержкой открытия
    /// устройства, до секунды на Bluetooth. Судить по этому окну значит
    /// объявить молчащей любую беспроводную гарнитуру в первый же миг
    /// разговора.
    /// </summary>
    public static readonly TimeSpan WarmupWindow = TimeSpan.FromSeconds(2);

    private readonly int _sampleRate;
    private long _delivered;
    private long _discontinuities;
    private int _sawFirstPacket;

    public DeviceActivityWatch(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _sampleRate = sampleRate;
    }

    /// <summary>Сколько отсчётов устройство отдало.</summary>
    public long DeliveredSamples => Interlocked.Read(ref _delivered);

    /// <summary>Сколько разрывов потока насчитано, не считая начального.</summary>
    public long Discontinuities => Interlocked.Read(ref _discontinuities);

    public void NoteDelivered(int samples) => Interlocked.Add(ref _delivered, samples);

    /// <summary>
    /// Отмечает разрыв потока.
    ///
    /// Самый первый не считается: флаг на первом пакете после запуска приходит
    /// всегда и означает начало потока. Это замер W0, а не предположение.
    /// </summary>
    public void NoteDiscontinuity()
    {
        if (Interlocked.Exchange(ref _sawFirstPacket, 1) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _discontinuities);
    }

    /// <summary>Доля отданного звука от того, сколько должно было прийти за это время.</summary>
    public double DeliveryRatio(TimeSpan elapsed)
    {
        double expected = elapsed.TotalSeconds * _sampleRate;
        return expected <= 0 ? 0 : DeliveredSamples / expected;
    }

    /// <summary>Разрывов в секунду.</summary>
    public double DiscontinuitiesPerSecond(TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : Discontinuities / elapsed.TotalSeconds;

    /// <summary>
    /// Приговор.
    ///
    /// Сбой проверяется раньше молчания: у пустого донгла верно и то и другое,
    /// но пересборка тракта — действие, а «включите гарнитуру» — просьба к
    /// человеку, и беспокоить его надо, только когда сделать самим уже нечего.
    /// </summary>
    public DeviceActivity Assess(TimeSpan elapsed)
    {
        if (elapsed < WarmupWindow)
        {
            return DeviceActivity.Warmup;
        }

        if (DiscontinuitiesPerSecond(elapsed) > FaultThreshold)
        {
            return DeviceActivity.Faulty;
        }

        if (DeliveryRatio(elapsed) < SilenceThreshold)
        {
            return DeviceActivity.SilentButPresent;
        }

        return DeviceActivity.Working;
    }

    /// <summary>
    /// Что сказать оператору. Пустая строка — говорить нечего, всё в порядке.
    ///
    /// Формулировки без цифр и без нашей внутренней кухни: их читает тот, кто
    /// звонит, а не тот, кто чинит. Числа идут в диагностику через
    /// <see cref="Summary"/>.
    /// </summary>
    public static string Advice(DeviceActivity activity) => activity switch
    {
        DeviceActivity.SilentButPresent =>
            "Микрофон не даёт звука. Проверьте, включена ли гарнитура и не поднят ли микрофон.",
        DeviceActivity.Faulty =>
            "Связь с гарнитурой рвётся. Если она беспроводная — подойдите ближе к приёмнику.",
        _ => string.Empty,
    };

    public string Summary(TimeSpan elapsed) =>
        $"устройство: {Title(Assess(elapsed))}; отдано {DeliveryRatio(elapsed):P2}, "
        + $"разрывов {Discontinuities} ({DiscontinuitiesPerSecond(elapsed):F1}/с)";

    public void Reset()
    {
        Interlocked.Exchange(ref _delivered, 0);
        Interlocked.Exchange(ref _discontinuities, 0);
        Interlocked.Exchange(ref _sawFirstPacket, 0);
    }

    private static string Title(DeviceActivity activity) => activity switch
    {
        DeviceActivity.Warmup => "разгон",
        DeviceActivity.Working => "работает",
        DeviceActivity.SilentButPresent => "МОЛЧИТ",
        _ => "СБОИТ",
    };
}
