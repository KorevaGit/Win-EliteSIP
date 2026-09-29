using System.Globalization;
using System.Threading.Channels;

namespace EliteSIP.SipCore;

/// <summary>
/// Состояние звонка в любую сторону.
///
/// Строки причин здесь и ниже пока русские литералы. Перевод (ru и en) — работа
/// этапа W8: там появятся ресурсы и станет, чему их подставлять. В оригинале ту
/// же роль играл <c>NSLocalizedString</c> с бандлом пакета.
/// </summary>
public abstract record SipCallState
{
    private SipCallState()
    {
    }

    public sealed record Dialing : SipCallState;

    /// <summary>
    /// Пришёл 180 или 183: на той стороне звонит. Что при этом слышно, говорят
    /// отдельные события — <see cref="SipCallEvent.EarlyMedia"/> и
    /// <see cref="SipCallEvent.LocalRingback"/>.
    /// </summary>
    public sealed record Ringing : SipCallState;

    /// <summary>Нам звонят: INVITE принят, мы ответили 180, решение за оператором.</summary>
    public sealed record Incoming : SipCallState;

    public sealed record Answered : SipCallState;

    public sealed record Ending : SipCallState;

    public sealed record Ended(string Reason) : SipCallState;

    public bool IsActive => this is not Ended;
}

/// <summary>
/// События звонка для того, кто им управляет.
///
/// Тело SDP передаётся байтами: слой сигнализации не разбирает медиа и не
/// зависит от кодеков. Согласование делает вызывающий — так пакеты не начинают
/// зависеть друг от друга, а SipCore остаётся тестируемым без аудио.
/// </summary>
public abstract record SipCallEvent
{
    private SipCallEvent()
    {
    }

    public sealed record State(SipCallState Value) : SipCallEvent;

    /// <summary>Собеседник ответил. Тело — SDP-ответ, его нужно разобрать и запустить медиа.</summary>
    ///
    /// <remarks>
    /// Если до ответа пришло <see cref="EarlyMedia"/>, поток уже идёт, и ответ
    /// его продолжает, а не начинает заново: тело может повторить раннее,
    /// уточнить адрес или ключи, а изредка — сменить кодек. Пустое тело при
    /// поднятом раннем медиа значит «всё как было».
    /// </remarks>
    public sealed record Answered(ReadOnlyMemory<byte> Body, string? ContentType) : SipCallEvent;

    /// <summary>
    /// Ранние медиа: предварительный ответ (обычно 183, бывает 180) с SDP.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// Станция сама играет в этот поток — гудки, IVR, «абонент недоступен» — и
    /// поднять его надо сразу, до ответа. Пока звук поднимался только на 200 OK,
    /// на направлениях с ранними медиа оператор слышал тишину или обрывок
    /// объявления: жалоба «гудки слышны через раз» на macOS была ровно этим.
    /// </para>
    /// <para>
    /// <b>Одно событие на тело, а не на ответ.</b> Asterisk повторяет 183 с тем
    /// же SDP, пока идёт вызов, и каждое событие наверху означало бы
    /// перезапуск звука. Новое событие приходит, только если тело изменилось.
    /// </para>
    /// </remarks>
    public sealed record EarlyMedia(int Status, ReadOnlyMemory<byte> Body, string? ContentType) : SipCallEvent;

    /// <summary>
    /// 180 без SDP: на той стороне звонит, а гудков станция не даёт — играть
    /// их надо самим.
    /// </summary>
    ///
    /// <remarks>
    /// Приходит один раз за вызов. Раннее медиа важнее: если поток уже идёт или
    /// поднимется позже, гудки собственного производства замолкают, иначе
    /// оператор слышит два сигнала сразу.
    /// </remarks>
    public sealed record LocalRingback : SipCallEvent;

    public sealed record Failed(int Status, string Reason) : SipCallEvent;

    public sealed record Ended(string Reason) : SipCallEvent;
}

/// <summary>
/// Входящий звонок в том виде, в каком о нём можно судить до ответа.
///
/// Событий у него столько же, сколько у исходящего, и приезжают они тем же
/// потоком: отменённый до ответа вызов — это завершение, и окно надо убрать
/// ровно так же, как при обычном окончании.
/// </summary>
public sealed class SipIncomingCall
{
    public required string CallId { get; init; }

    /// <summary>
    /// Номер звонящего из From. При раздаче лидов это номер очереди, а не
    /// клиента: клиентский в SIP не приходит вовсе.
    /// </summary>
    public required string CallerNumber { get; init; }

    /// <summary>Отображаемое имя из From, если сервер его прислал.</summary>
    public string? CallerName { get; init; }

    /// <summary>
    /// Сервер просит снять трубку самостоятельно (<c>X-Autoanswer: TRUE</c>).
    ///
    /// Заголовок нестандартный: его ставит диалплан заказчика, и ставит ровно на
    /// раздачу лида — снято с боевых вызовов 28 августа 2026, три раздачи подряд
    /// с ним и семнадцать прочих входящих без него.
    ///
    /// Для SipCore это только факт из заголовка, а не команда. <b>Мы ему не
    /// подчиняемся, и это не упущение.</b> Приложение написано затем, чтобы
    /// принятие вызова требовало живого человека; автоответ по просьбе сервера
    /// отдал бы лид пустому месту — ровно то, от чего защищаемся. Наверх факт
    /// уходит потому, что в нём есть другой смысл: он единственный отличает
    /// раздачу от обычного звонка коллеги, у которого такой же внутренний номер
    /// и такое же имя.
    /// </summary>
    public bool RequestsAutoAnswer { get; init; }

    /// <summary>
    /// Просит ли вызов автоответа любым принятым способом: <c>X-Autoanswer</c>,
    /// <c>Call-Info: answer-after</c>, <c>Alert-Info</c> с автоответом,
    /// <c>Answer-Mode: Auto</c>.
    /// </summary>
    ///
    /// <remarks>
    /// Снимать трубку или нет, решает режим автоподъёма в настройках (с 0.1.61,
    /// как на macOS). По умолчанию режим выключен, и тогда верно всё сказанное
    /// у <see cref="RequestsAutoAnswer"/>: вызов принимает человек.
    /// </remarks>
    public bool AsksForAutoAnswer { get; init; }

    /// <summary>
    /// Номер, на который звонили. Отличается от нашего, когда вызов пришёл через
    /// очередь или переадресацию.
    /// </summary>
    public required string CalledNumber { get; init; }

    /// <summary>Предложение SDP. Разбирает его тот, кто владеет медиа.</summary>
    public ReadOnlyMemory<byte> Offer { get; init; }

    public string? OfferContentType { get; init; }

    /// <summary>События этого звонка: отмена до ответа, завершение, ошибки.</summary>
    public required IAsyncEnumerable<SipCallEvent> Events { get; init; }

    /// <summary>Что показать в окне крупным шрифтом.</summary>
    public string DisplayNumber => CallerNumber.Length == 0 ? "неизвестный номер" : CallerNumber;
}

/// <summary>
/// Исходящий звонок: линия и её события.
///
/// Call-ID отдаётся сразу, ещё до первого INVITE, потому что именно им линия
/// адресуется дальше — на удержание, перевод и завершение. Ждать его из потока
/// событий значило бы иметь окно, в котором звонок уже идёт, а сказать про него
/// нечего.
/// </summary>
public sealed record SipOutgoingCall(string CallId, IAsyncEnumerable<SipCallEvent> Events);

/// <summary>
/// Почему звонок не начался.
///
/// В оригинале это перечисление с переведённым описанием; здесь — готовые
/// строки, потому что наружу они и уходят только строкой, в событии
/// <see cref="SipCallEvent.Failed"/>.
/// </summary>
public static class SipCallErrors
{
    public const string NotRegistered = "нет регистрации на сервере";

    public const string AlreadyInCall = "звонок уже идёт";

    public const string EmptyTarget = "не задан номер";

    /// <summary>Звонка, которым просят управлять, уже нет: обычно его отменили, пока оператор тянулся к кнопке.</summary>
    public const string NoIncomingCall = "входящего звонка больше нет";

    /// <summary>Все линии заняты: разговор, консультация и третий участник.</summary>
    public static string TooManyLines(int maximum) =>
        $"заняты все линии ({maximum.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>
    /// Человеческое объяснение кода отказа.
    ///
    /// Существует потому, что «сервер ответил 486» ничего не говорит оператору,
    /// а «занято» говорит всё.
    /// </summary>
    public static string DescribeCallFailure(int status, string reason) => status switch
    {
        486 or 600 => "занято",
        408 => "не отвечает",
        480 => "недоступен",
        404 => "такого номера нет",
        403 => "звонок запрещён",
        487 => "звонок отменён",
        603 => "отклонён",
        _ => $"отказ {status.ToString(CultureInfo.InvariantCulture)} {reason}",
    };
}

public enum SipRenegotiationErrorKind
{
    NoActiveCall,

    /// <summary>Наш повторный INVITE уже в пути.</summary>
    AlreadyRenegotiating,

    /// <summary>491: собеседник прислал встречное предложение раньше нашего.</summary>
    RequestPending,

    Rejected,
    Timeout,
    TransportFailed,
}

/// <summary>
/// Что могло пойти не так при пересогласовании медиа внутри разговора.
///
/// Отдельно от ошибок звонка потому, что смысл у этих ошибок другой: звонок
/// после них продолжается. RFC 3261 §14.1 требует именно этого — отказ на
/// повторный INVITE оставляет разговор на прежних параметрах, а не завершает
/// его. Для оператора это значит «удержание не сработало», а не «связь упала».
/// </summary>
public sealed class SipRenegotiationException : Exception
{
    public SipRenegotiationException()
        : this(SipRenegotiationErrorKind.NoActiveCall)
    {
    }

    public SipRenegotiationException(string message)
        : this(SipRenegotiationErrorKind.TransportFailed, 0, message)
    {
    }

    public SipRenegotiationException(string message, Exception innerException)
        : base(message, innerException) => Kind = SipRenegotiationErrorKind.TransportFailed;

    public SipRenegotiationException(SipRenegotiationErrorKind kind, int status = 0, string reason = "")
        : base(Describe(kind, status, reason))
    {
        Kind = kind;
        Status = status;
    }

    public SipRenegotiationErrorKind Kind { get; }

    public int Status { get; }

    private static string Describe(SipRenegotiationErrorKind kind, int status, string reason) => kind switch
    {
        SipRenegotiationErrorKind.NoActiveCall => "разговора нет",
        SipRenegotiationErrorKind.AlreadyRenegotiating => "пересогласование уже идёт",
        SipRenegotiationErrorKind.RequestPending => "собеседник пересогласовывает первым (491)",
        SipRenegotiationErrorKind.Rejected => $"отказ {status.ToString(CultureInfo.InvariantCulture)} {reason}",
        SipRenegotiationErrorKind.Timeout => "сервер не ответил",
        _ => $"сеть: {reason}",
    };
}

/// <summary>
/// Как отвечать на чужой повторный INVITE.
///
/// На вход — Call-ID линии и предложение SDP байтами, на выход — наш ответ или
/// <see langword="null"/>, если принять предложение нечем (тогда уйдёт 488).
/// Делегат, а не событие в потоке, ровно по одной причине: ответить надо в
/// рамках той же транзакции, и «отправить событие и надеяться, что кто-то
/// ответит» здесь не работает. Разбор SDP при этом остаётся снаружи — SipCore
/// про медиа не знает ничего.
///
/// Call-ID обязателен: у оператора до трёх линий, и пересогласовать сервер может
/// ту, которая стоит на удержании.
/// </summary>
public delegate Task<ReadOnlyMemory<byte>?> SipMediaRenegotiator(string callId, ReadOnlyMemory<byte> offer);

/// <summary>Внутренняя очередь событий одного звонка. Политика вытеснения та же, что в оригинале.</summary>
internal static class SipCallEventChannel
{
    public static Channel<SipCallEvent> Create() =>
        Channel.CreateBounded<SipCallEvent>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
}
