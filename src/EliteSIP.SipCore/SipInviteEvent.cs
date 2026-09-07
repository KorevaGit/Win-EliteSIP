namespace EliteSIP.SipCore;

/// <summary>Что происходит с исходящим INVITE.</summary>
public abstract record SipInviteEvent
{
    private SipInviteEvent()
    {
    }

    /// <summary>1xx: пошли гудки. Может прийти несколько раз.</summary>
    public sealed record Provisional(SipResponse Response) : SipInviteEvent;

    /// <summary>
    /// 2xx: собеседник ответил.
    ///
    /// ACK на 2xx отправляет вызывающая сторона, а не слой транзакций, и это
    /// требование RFC 3261 §17.1.1.3: такой ACK идёт ВНЕ транзакции, по маршруту
    /// диалога и на Contact из ответа. Слой этого маршрута не знает.
    /// </summary>
    public sealed record Success(SipResponse Response) : SipInviteEvent;

    /// <summary>3xx–6xx. ACK на такой ответ — часть транзакции, слой уже его отправил.</summary>
    public sealed record Failure(SipResponse Response) : SipInviteEvent;

    /// <summary>Истёк таймер B: 64*T1, по умолчанию 32 секунды без всякого ответа.</summary>
    public sealed record Timeout : SipInviteEvent;

    public sealed record TransportFailed(string Reason) : SipInviteEvent;
}

/// <summary>
/// Что происходит с входящим INVITE после того, как мы на него ответили.
///
/// Слой транзакций сам по себе звонок не ведёт, но две вещи знает только он:
/// дошёл ли до собеседника наш финальный ответ и не пора ли перестать его
/// повторять. Обе важны для звонка целиком, поэтому уезжают наверх событием.
/// </summary>
public abstract record SipServerInviteEvent
{
    private SipServerInviteEvent()
    {
    }

    /// <summary>Пришёл ACK: собеседник принял наш ответ, повторять его больше не нужно.</summary>
    public sealed record Acknowledged(string CallId) : SipServerInviteEvent;

    /// <summary>
    /// ACK не пришёл за 64*T1.
    ///
    /// По RFC 3261 §13.3.1.4 это конец: диалог формально установлен нашим 200,
    /// но подтверждения нет, и звонок надо закрывать через BYE. Иначе получится
    /// разговор, о котором знаем только мы.
    /// </summary>
    public sealed record NotAcknowledged(string CallId) : SipServerInviteEvent;
}
