namespace EliteSIP.MediaCore;

/// <summary>
/// Один шаг набора: тон или пауза.
///
/// Пауза — полноправный шаг, а не украшение: голосовые меню на той стороне
/// живые, и «набрать 2, дождаться приглашения, набрать добавочный» без пауз
/// превращается в один слипшийся набор, который меню не разбирает.
/// </summary>
public abstract record DtmfStep
{
    private protected DtmfStep()
    {
    }

    public sealed record Tone(byte Event) : DtmfStep;

    public sealed record Pause(int Milliseconds) : DtmfStep;
}

/// <summary>
/// Последовательность DTMF: то, что нажал оператор, или то, что записано в
/// макросе.
///
/// Разбор строки живёт здесь, а не в приложении, ровно по той же причине, по
/// которой здесь же лежит SDP: это часть протокола, а не интерфейса, и
/// проверяется она таблицей, а не глазами.
/// </summary>
public sealed class DtmfSequence
{
    /// <summary>
    /// Пауза по умолчанию — секунда.
    ///
    /// Формат макроса заказчиком пока не задан, и секунда взята как то, к чему
    /// привыкли по мобильным телефонам: там запятая в номере значит ровно это.
    /// Длительность настраивается.
    /// </summary>
    public const int DefaultPauseMilliseconds = 1000;

    public DtmfSequence(IEnumerable<DtmfStep> steps) => Steps = [.. steps];

    /// <summary>
    /// Разбирает запись макроса. Непонятные символы молча пропускаются —
    /// проверять их до сохранения должен тот, кто макрос вводит: см.
    /// <see cref="UnsupportedCharacters"/>.
    /// </summary>
    public DtmfSequence(string text, int pauseMilliseconds = DefaultPauseMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<DtmfStep> steps = [];
        foreach (char character in text)
        {
            if (IgnoredCharacters.Contains(character))
            {
                continue;
            }

            if (PauseCharacters.Contains(character))
            {
                // Паузы подряд складываются: «,,» — это две секунды, и так
                // записывать удобнее, чем заводить второй символ.
                if (steps.Count > 0 && steps[^1] is DtmfStep.Pause already)
                {
                    steps[^1] = new DtmfStep.Pause(already.Milliseconds + pauseMilliseconds);
                }
                else
                {
                    steps.Add(new DtmfStep.Pause(pauseMilliseconds));
                }

                continue;
            }

            if (TelephoneEventPayload.EventCode(character) is byte code)
            {
                steps.Add(new DtmfStep.Tone(code));
            }
        }

        Steps = steps;
    }

    /// <summary>
    /// Символы паузы. Запятая — как в номерах на телефоне, <c>p</c> — как в
    /// модемных строках набора; обе записи в ходу, и спорить о них незачем.
    /// </summary>
    public static IReadOnlySet<char> PauseCharacters { get; } = new HashSet<char> { ',', 'p', 'P' };

    /// <summary>Символы, которые в записи ничего не значат и просто повышают читаемость.</summary>
    public static IReadOnlySet<char> IgnoredCharacters { get; } = new HashSet<char> { ' ', '-', '\t' };

    public IReadOnlyList<DtmfStep> Steps { get; }

    public bool IsEmpty => Steps.Count == 0;

    /// <summary>Есть ли в записи хоть один тон. Макрос из одних пауз бессмыслен.</summary>
    public bool HasTones => Steps.Any(step => step is DtmfStep.Tone);

    /// <summary>Символы, которые разобрать не удалось. Для проверки ввода в настройках.</summary>
    public static IReadOnlyList<char> UnsupportedCharacters(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            .. text.Where(character =>
                !IgnoredCharacters.Contains(character)
                && !PauseCharacters.Contains(character)
                && TelephoneEventPayload.EventCode(character) is null),
        ];
    }

    /// <summary>Как последовательность выглядит для человека: тоны как есть, пауза точкой.</summary>
    public string DisplayText => string.Concat(Steps.Select(step => step switch
    {
        DtmfStep.Tone tone => TelephoneEventPayload.Character(tone.Event)?.ToString() ?? "?",
        _ => "·",
    }));
}

/// <summary>Сколько длится тон, пауза между тонами и как громко.</summary>
/// <param name="ToneMilliseconds">
/// Длительность самого тона. RFC 4733 требует минимум 40 мс, но телефонные меню
/// на той стороне бывают глухие; 120 мс — то, что шлют аппаратные телефоны, и
/// оно проходит везде.
/// </param>
/// <param name="GapMilliseconds">
/// Тишина между двумя тонами. Без неё две одинаковые цифры подряд принимающая
/// сторона слышит как одну длинную.
/// </param>
/// <param name="EndPacketRepeats">
/// Сколько раз повторить пакет конца события. Три — по RFC 4733 §2.5.1.2. Пакет
/// конца ничем не защищён от потери, а потерянный конец означает тон, который у
/// собеседника длится вечно.
/// </param>
/// <param name="Volume">Громкость в -dBm0: меньше значит громче. 10 — обычное значение.</param>
/// <param name="PacketTimeMilliseconds">
/// Такт отправки. Совпадает с пакетным временем звука: события идут в том же
/// потоке RTP и в том же ритме.
/// </param>
public sealed record DtmfTiming(
    int ToneMilliseconds = 120,
    int GapMilliseconds = 80,
    int EndPacketRepeats = 3,
    byte Volume = 10,
    int PacketTimeMilliseconds = AudioCodecInfo.DefaultPacketTimeMilliseconds);

/// <summary>Пакет события в готовом к отправке виде.</summary>
/// <param name="IsFirst">
/// Маркер RTP. Ставится на первом пакете события — по нему принимающая сторона
/// понимает, что начался новый тон, а не продолжается прежний.
/// </param>
/// <param name="CompletesEvent">Последний пакет события. После него поток возвращается к звуку.</param>
/// <param name="TimestampAdvance">
/// На сколько тактов сдвинуть метку времени, когда событие закончится.
///
/// Внутри события метка не растёт (RFC 4733 §2.5.1: все пакеты одного нажатия
/// несут время его начала), но время-то идёт. Если не досдвинуть метку на
/// длительность тона, весь остаток разговора уедет назад относительно часов, и
/// джиттер-буфер собеседника будет разгребать это как рассинхронизацию.
/// </param>
public sealed record DtmfPacket(
    TelephoneEventPayload Payload,
    bool IsFirst,
    bool CompletesEvent,
    uint TimestampAdvance);

/// <summary>Что делать дальше: отправить пакет или подождать.</summary>
public abstract record DtmfAction
{
    private protected DtmfAction()
    {
    }

    public sealed record Send(DtmfPacket Packet) : DtmfAction;

    public sealed record Wait(int Milliseconds) : DtmfAction;
}

/// <summary>
/// Раскладка набора на пакеты RTP.
///
/// Чистая функция от последовательности и таймингов — ровно по той же границе,
/// по которой в CallGuard отделена проверяемая часть от часов и мыши. Здесь
/// проверяется таблица пакетов, а сеть и сон остаются снаружи.
/// </summary>
public static class DtmfPlanner
{
    /// <summary>Пакеты одного тона: нарастающая длительность, потом повторённый конец.</summary>
    public static IReadOnlyList<DtmfAction> Actions(byte eventCode, DtmfTiming? timing = null)
    {
        timing ??= new DtmfTiming();

        uint ticks = TicksPerPacket(timing.PacketTimeMilliseconds);
        int packetCount = Math.Max(1, timing.ToneMilliseconds / Math.Max(timing.PacketTimeMilliseconds, 1));
        uint totalTicks = (uint)packetCount * ticks;

        List<DtmfAction> actions = [];

        for (int index = 1; index <= packetCount; index++)
        {
            actions.Add(new DtmfAction.Send(new DtmfPacket(
                new TelephoneEventPayload(
                    eventCode,
                    (ushort)Math.Min((uint)index * ticks, ushort.MaxValue),
                    isEnd: false,
                    volume: timing.Volume),
                IsFirst: index == 1,
                CompletesEvent: false,
                TimestampAdvance: 0)));
            actions.Add(new DtmfAction.Wait(timing.PacketTimeMilliseconds));
        }

        // Пакеты конца идут подряд, без пауз: это три копии одного и того же
        // сообщения, страховка от потери, а не три события.
        int repeats = Math.Max(1, timing.EndPacketRepeats);
        for (int index = 1; index <= repeats; index++)
        {
            actions.Add(new DtmfAction.Send(new DtmfPacket(
                new TelephoneEventPayload(
                    eventCode,
                    (ushort)Math.Min(totalTicks, ushort.MaxValue),
                    isEnd: true,
                    volume: timing.Volume),
                IsFirst: false,
                CompletesEvent: index == repeats,
                TimestampAdvance: totalTicks)));
        }

        return actions;
    }

    /// <summary>Раскладка всей последовательности.</summary>
    public static IReadOnlyList<DtmfAction> Actions(DtmfSequence sequence, DtmfTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        timing ??= new DtmfTiming();

        List<DtmfAction> actions = [];
        bool previousWasTone = false;

        foreach (DtmfStep step in sequence.Steps)
        {
            switch (step)
            {
                case DtmfStep.Tone tone:
                    // Пауза между тонами нужна только между ними: перед первым
                    // тоном она была бы задержкой на ровном месте.
                    if (previousWasTone && timing.GapMilliseconds > 0)
                    {
                        actions.Add(new DtmfAction.Wait(timing.GapMilliseconds));
                    }

                    actions.AddRange(Actions(tone.Event, timing));
                    previousWasTone = true;
                    break;

                case DtmfStep.Pause pause:
                    actions.Add(new DtmfAction.Wait(pause.Milliseconds));

                    // Своя пауза заменяет междуцифровую: складывать их значит
                    // получить секунду с хвостиком там, где просили секунду.
                    previousWasTone = false;
                    break;

                default:
                    break;
            }
        }

        return actions;
    }

    /// <summary>
    /// Такты часов telephone-event на один пакет.
    ///
    /// Часы события — всегда 8000 Гц (RFC 4733), независимо от кодека звука. У
    /// нас это совпадает с шагом метки времени и у G.711, и у G.722: последний
    /// по RFC 3551 тоже тактируется от 8000, хотя отсчётов в пакете вдвое
    /// больше.
    /// </summary>
    internal static uint TicksPerPacket(int packetTimeMilliseconds) =>
        (uint)packetTimeMilliseconds * TelephoneEvent.ClockRate / 1000;
}
