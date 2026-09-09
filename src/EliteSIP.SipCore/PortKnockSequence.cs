using System.Globalization;
using System.Text.Json.Serialization;

namespace EliteSIP.SipCore;

/// <summary>
/// Один шаг стука — то же, что одна строка <c>ping</c> в скрипте подключения.
/// </summary>
///
/// <remarks>
/// <see cref="PayloadBytes"/> — это <c>-s</c>, то есть данные ICMP без
/// восьмибайтового заголовка, ровно как их считает <c>ping</c>. Именно длина и
/// есть подпись: правило на шлюзе смотрит на размер пакета, а не на содержимое.
/// </remarks>
public sealed record PortKnockStep
{
    public PortKnockStep(int payloadBytes, string host = "", int count = 1)
    {
        Host = host;
        PayloadBytes = payloadBytes;
        Count = count;
    }

    /// <summary>
    /// Опознаётся так же, как клавиша: своим идентификатором, а не местом в
    /// списке. Редактору это нужно по существу — порядок шагов значим, и строка,
    /// привязанная к номеру, после удаления соседа начинает править чужой шаг.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Куда стучать. Пустая строка означает «адрес АТС из настроек» — в скрипте
    /// это боевой домен, но зашивать его в код нельзя: на стенде и в лаборатории
    /// сервер другой.
    /// </summary>
    public string Host { get; init; }

    /// <summary>Байты данных ICMP. Аналог <c>ping -s</c>.</summary>
    public int PayloadBytes { get; init; }

    /// <summary>Сколько пакетов подряд. Аналог <c>ping -c</c>.</summary>
    public int Count { get; init; }

    /// <summary>Хост с подставленным адресом сервера.</summary>
    public string ResolvedHost(string server) => Host.Length == 0 ? server : Host;
}

/// <summary>
/// Последовательность стука целиком.
/// </summary>
///
/// <remarks>
/// По умолчанию — то же, что делает боевой скрипт подключения, без его
/// оформления: без пауз «Connecting… 10 seconds», без открытия браузера и без
/// финального «Successfully connected!», которое печаталось независимо от того,
/// дошло ли хоть что-нибудь.
///
/// Значения вынесены в настройки сознательно: последовательность живёт на чужом
/// шлюзе, и менять её придётся не пересборкой приложения, а правкой настроек или
/// предустановкой от панели.
/// </remarks>
public sealed record PortKnockSequence
{
    /// <summary>
    /// Шаги боевой последовательности.
    /// </summary>
    ///
    /// <remarks>
    /// Отдельным полем, а не внутри <see cref="Production"/>: умолчание
    /// <see cref="Steps"/> ссылается на них же, и через <see cref="Production"/>
    /// это была бы круговая инициализация — тип, падающий при первом обращении к
    /// самому себе.
    /// </remarks>
    private static readonly PortKnockStep[] ProductionSteps =
    [
        new PortKnockStep(228, count: 2),
        new PortKnockStep(126, count: 2),
        new PortKnockStep(125),
        new PortKnockStep(228, "45.10.53.84"),
        new PortKnockStep(126, "45.10.53.86"),
        new PortKnockStep(125, "45.10.53.94"),
    ];

    /// <summary>
    /// Боевая последовательность из скрипта удалённого подключения.
    /// </summary>
    ///
    /// <remarks>
    /// Три адреса стучатся отдельно от домена сознательно: какой из них АТС, а
    /// какие CRM и прочее, на стороне заказчика ответить не смогли, поэтому
    /// повторяем скрипт целиком, а не ту его часть, которая кажется нужной.
    /// </remarks>
    public static PortKnockSequence Production { get; } = new();

    /// <summary>
    /// Шаги стука. Пустой список — стук выключен.
    /// </summary>
    ///
    /// <remarks>
    /// Умолчание — боевая последовательность, а пустой список в файле означает
    /// «стучать нечем», а не «взять умолчание». Иначе выключить стук правкой
    /// настроек было бы невозможно.
    /// </remarks>
    public IReadOnlyList<PortKnockStep> Steps { get; init; } = ProductionSteps;

    /// <summary>
    /// Пауза между пакетами.
    /// </summary>
    ///
    /// <remarks>
    /// Секунда — не осторожность, а воспроизведение: <c>ping</c> шлёт с таким
    /// интервалом по умолчанию, и правило на шлюзе годами видело стук именно в
    /// этом темпе. Порядок пакетов для стука значим, и торопиться незачем.
    /// </remarks>
    public double SpacingSeconds { get; init; } = 1;

    /// <summary>
    /// Как часто повторять стук, пока всё работает.
    /// </summary>
    ///
    /// <remarks>
    /// Точный срок, на который шлюз открывает доступ, неизвестен — известно
    /// только, что при смене публичного адреса он почти наверняка перестаёт
    /// действовать, а адрес у большинства сотрудников динамический. Десять минут
    /// — заведомо меньше любого правдоподобного таймаута списка на MikroTik и
    /// при этом восемь пакетов за десять минут, то есть цена вопроса не
    /// обсуждается.
    /// </remarks>
    public double RepeatIntervalSeconds { get; init; } = 600;

    /// <summary>Сколько всего пакетов уйдёт.</summary>
    [JsonIgnore]
    public int PacketCount => Steps.Sum(step => Math.Max(0, step.Count));

    /// <summary>
    /// Сколько времени займёт стук.
    /// </summary>
    ///
    /// <remarks>
    /// Нужно тому, кто его ждёт: это задержка перед первым REGISTER, и она
    /// должна быть предсказуемой, а не сюрпризом. Считаются промежутки, а не
    /// пакеты: после последнего ждать нечего.
    /// </remarks>
    [JsonIgnore]
    public TimeSpan EstimatedDuration
        => TimeSpan.FromSeconds(Math.Max(0, PacketCount - 1) * SpacingSeconds);

    [JsonIgnore]
    public bool IsEmpty => PacketCount == 0;
}

/// <summary>
/// Нужен ли стук вообще.
/// </summary>
///
/// <remarks>
/// Решает пометка рабочего места, а когда её не ставили — адрес сервера, а не
/// адрес машины. В офисе в настройки вписывают внутренний адрес, снаружи —
/// внешний домен; проверять при этом собственный адрес машины было бы хуже,
/// потому что он у сотрудника тоже приватный (домашняя сеть), и различить по
/// нему офис от дома нельзя в принципе.
/// </remarks>
public static class PortKnockPolicy
{
    /// <summary>
    /// Внутренний ли адрес сервера.
    /// </summary>
    ///
    /// <remarks>
    /// Обобщение сверх «не равно боевому адресу» намеренное: под правило
    /// попадают и лаборатория на <c>127.0.0.1</c>, и стенд в любой приватной
    /// сети. Стучать туда бессмысленно, а на локальный адрес ещё и вредно — там
    /// нет ни шлюза, ни правила, зато есть семь секунд задержки на каждом
    /// запуске.
    /// </remarks>
    public static bool IsInternal(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        host = host.Trim().ToLowerInvariant();

        // Пустой адрес — это ненастроенная машина, а не внешний сервер. Стучать
        // в никуда семь секунд перед заведомо провальной регистрацией незачем.
        if (host.Length == 0)
        {
            return true;
        }

        if (host is "localhost" or "::1"
            || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.StartsWith("fe80:", StringComparison.Ordinal)
            || host.StartsWith("fc", StringComparison.Ordinal)
            || host.StartsWith("fd", StringComparison.Ordinal))
        {
            return true;
        }

        var octets = host.Split('.');
        if (octets.Length != 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var index = 0; index < 4; index++)
        {
            if (!int.TryParse(octets[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value > 255)
            {
                return false;
            }

            numbers[index] = value;
        }

        return numbers[0] switch
        {
            10 or 127 => true,
            192 => numbers[1] == 168,
            169 => numbers[1] == 254,

            // 172.16.0.0/12 — это 172.16–172.31, а не весь 172.
            172 => numbers[1] is >= 16 and <= 31,
            _ => false,
        };
    }

    /// <summary>
    /// Стучать ли перед регистрацией.
    /// </summary>
    ///
    /// <remarks>
    /// Явно заданное рабочее место сильнее адреса — в этом весь смысл пометки:
    /// офис за внешним доменом не платит семью секундами за каждое подключение,
    /// а удалённое место с внутренним адресом сервера (чужой туннель, проброс)
    /// всё-таки стучит.
    /// </remarks>
    public static bool NeedsKnocking(string serverHost, WorkplaceSite site = WorkplaceSite.Automatic)
        => site switch
        {
            WorkplaceSite.Office => false,
            WorkplaceSite.Remote => true,
            _ => !IsInternal(serverHost),
        };

    /// <summary>
    /// Во что превращается «не выбирали» на самом деле.
    /// </summary>
    ///
    /// <remarks>
    /// Нужно интерфейсу: выбора «определять по адресу» человеку не предлагают —
    /// он и так выбирает вручную, и третья кнопка означала бы «не выбирать».
    /// Вместо этого система решает сама, показывает решение выбранным и даёт его
    /// переопределить.
    /// </remarks>
    public static WorkplaceSite ResolvedSite(string serverHost, WorkplaceSite site)
        => site switch
        {
            WorkplaceSite.Office or WorkplaceSite.Remote => site,
            _ => IsInternal(serverHost) ? WorkplaceSite.Office : WorkplaceSite.Remote,
        };

    /// <summary>
    /// Чем решение объясняется в журнале.
    /// </summary>
    ///
    /// <remarks>
    /// Нужно тому, кто разбирает «почему не стучим» или «почему ждём семь
    /// секунд»: догадка по адресу и явная настройка выглядят одинаково, пока их
    /// не назвать по-разному.
    /// </remarks>
    public static string Explanation(string serverHost, WorkplaceSite site) => site switch
    {
        WorkplaceSite.Office => "рабочее место помечено как офисное",
        WorkplaceSite.Remote => "рабочее место помечено как удалённое",
        _ => IsInternal(serverHost) ? "адрес сервера внутренний" : "адрес сервера внешний",
    };
}

/// <summary>Где стоит рабочее место. Решает, стучать ли перед регистрацией.</summary>
public enum WorkplaceSite
{
    /// <summary>Не выбирали — решает адрес сервера.</summary>
    Automatic,

    Office,

    Remote,
}

/// <summary>
/// Пропускать ли очередной стук.
/// </summary>
///
/// <remarks>
/// Вынесено из самого стучащего отдельным значением, чтобы правило проверялось
/// проверкой, а не живой сетью: единственное, что здесь можно испортить, — это
/// отношение «когда стучали в прошлый раз» к «зачем стучим сейчас».
/// </remarks>
public sealed class PortKnockThrottle
{
    private DateTimeOffset? _lastKnockAt;

    public PortKnockThrottle(TimeSpan minimumInterval) => MinimumInterval = minimumInterval;

    public TimeSpan MinimumInterval { get; }

    /// <summary>Повтор после отказа не пропускается никогда.</summary>
    public bool ShouldKnock(SipPathOpenReason reason, DateTimeOffset now)
        => reason is SipPathOpenReason.Retry
            || _lastKnockAt is not { } last
            || now - last >= MinimumInterval;

    public void RecordKnock(DateTimeOffset moment) => _lastKnockAt = moment;

    /// <summary>
    /// Забыть о прошлом стуке: сеть сменилась, и открытым может быть уже не наш
    /// адрес.
    /// </summary>
    public void Invalidate() => _lastKnockAt = null;
}
