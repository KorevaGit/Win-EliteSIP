using System.Globalization;

namespace EliteSIP.SipCore;

/// <summary>
/// Диалог SIP — то, что связывает запросы одного звонка между собой.
///
/// Существует потому, что ACK, BYE, re-INVITE и REFER обязаны нести те же
/// Call-ID и теги, что и установивший диалог INVITE, идти по тому же набору
/// маршрутов и на тот же Contact собеседника. Собрать это по месту из ответа
/// каждый раз — верный способ получить 481 «диалог не существует».
///
/// Тип неизменяемый по номеру CSeq: <see cref="NextSequence"/> возвращает новый
/// диалог, чтобы номер нельзя было случайно использовать дважды — сервер счёл бы
/// это ретрансмиссией. В оригинале то же самое давала значимая семантика
/// структуры.
/// </summary>
public sealed class SipDialog
{
    public SipDialog(
        string callId,
        string localTag,
        string remoteTag,
        NameAddress localAddress,
        NameAddress remoteAddress,
        SipUri remoteTarget,
        int localSequence,
        bool isInitiator,
        IEnumerable<SipUri>? routeSet = null)
    {
        CallId = callId;
        LocalTag = localTag;
        RemoteTag = remoteTag;
        LocalAddress = localAddress;
        RemoteAddress = remoteAddress;
        RemoteTarget = remoteTarget;
        LocalSequence = localSequence;
        IsInitiator = isInitiator;
        RouteSet = routeSet is null ? [] : [.. routeSet];
    }

    public string CallId { get; }

    public string LocalTag { get; }

    public string RemoteTag { get; }

    public NameAddress LocalAddress { get; }

    public NameAddress RemoteAddress { get; }

    /// <summary>
    /// Contact собеседника: куда отправлять запросы внутри диалога. Это НЕ то
    /// же, что адрес в To — за NAT они почти всегда разные.
    /// </summary>
    public SipUri RemoteTarget { get; }

    /// <summary>
    /// Набор маршрутов из Record-Route. Для входящего звонка порядок обратный
    /// тому, в каком заголовки пришли.
    /// </summary>
    public IReadOnlyList<SipUri> RouteSet { get; }

    /// <summary>Номер CSeq для следующего запроса от нас.</summary>
    public int LocalSequence { get; }

    /// <summary>Инициировали ли диалог мы. От этого зависит, чей tag куда идёт.</summary>
    public bool IsInitiator { get; }

    /// <summary>Собирает диалог из успешного ответа на наш INVITE.</summary>
    public static SipDialog? FromInitiator(SipRequest request, SipResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if (!response.IsSuccess
            || response.CallId is not string callId
            || (response.From ?? request.From) is not NameAddress from
            || response.To is not NameAddress to
            || from.Tag is not string localTag
            || to.Tag is not string remoteTag
            || request.CSeq is not (int Number, SipMethod) cseq)
        {
            return null;
        }

        // Contact ответа — обязательный элемент: без него неизвестно, куда
        // отправлять ACK и BYE. Падать назад на адрес из To нельзя, за NAT это
        // приведёт в никуда.
        if (response.Contacts.Count == 0)
        {
            return null;
        }

        // Record-Route в ответе идёт в порядке от нас к собеседнику, а
        // маршрутизировать надо в том же направлении — значит порядок
        // сохраняется как есть (RFC 3261 §12.1.2).
        return new SipDialog(
            callId,
            localTag,
            remoteTag,
            from,
            to,
            response.Contacts[0].Uri,
            cseq.Number,
            isInitiator: true,
            RoutesFrom(response.Headers, reversed: false));
    }

    /// <summary>
    /// Собирает диалог из принятого нами INVITE и нашего же 200 OK.
    ///
    /// Зеркало инициаторского случая, и зеркалить приходится всё: наш тег теперь
    /// в To, чужой — во From, маршрут из Record-Route берётся в обратном порядке
    /// (RFC 3261 §12.1.1), а счётчик CSeq для наших запросов начинается с нуля —
    /// номер из INVITE принадлежит другой стороне.
    /// </summary>
    public static SipDialog? FromResponder(SipRequest request, string localTag)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CallId is not string callId
            || request.From is not NameAddress from
            || request.To is not NameAddress to
            || from.Tag is not string remoteTag)
        {
            return null;
        }

        // Contact запроса — единственный адрес, куда можно слать BYE. За NAT он
        // отличается от того, что стоит во From, и падать назад на From нельзя.
        if (request.Contacts.Count == 0)
        {
            return null;
        }

        return new SipDialog(
            callId,
            localTag,
            remoteTag,
            to,
            from,
            request.Contacts[0].Uri,
            localSequence: 0,
            isInitiator: false,
            RoutesFrom(request.Headers, reversed: true));
    }

    private static List<SipUri> RoutesFrom(SipHeaders headers, bool reversed)
    {
        List<SipUri> routes = [];
        foreach (string value in headers.Values(SipHeaderName.RecordRoute))
        {
            if (NameAddress.Parse(value) is NameAddress address)
            {
                routes.Add(address.Uri);
            }
        }
        if (reversed)
        {
            routes.Reverse();
        }
        return routes;
    }

    // Построение запросов внутри диалога

    /// <summary>
    /// Куда физически отправлять запрос: первый lr-маршрут, если он есть, иначе
    /// Contact собеседника.
    /// </summary>
    public SipUri RequestDestination =>
        RouteSet.Count > 0 && RouteSet[0].HasParameter("lr") ? RouteSet[0] : RemoteTarget;

    /// <summary>
    /// Готовит запрос внутри диалога. CSeq наращивается вызывающим через
    /// <see cref="NextSequence"/> — кроме ACK, который обязан повторить номер INVITE.
    /// </summary>
    public SipRequest MakeRequest(
        SipMethod method,
        int sequence,
        SipVia via,
        NameAddress? contact = null,
        string? userAgent = null,
        int maxForwards = 70)
    {
        ArgumentNullException.ThrowIfNull(via);

        var request = new SipRequest(method, RemoteTarget);
        request.Headers.Append(SipHeaderName.Via, via.ToString());
        request.Headers.Append(SipHeaderName.MaxForwards, maxForwards.ToString(CultureInfo.InvariantCulture));

        NameAddress from = LocalAddress.Clone();
        from.Tag = LocalTag;
        request.Headers.Append(SipHeaderName.From, from.ToString());

        NameAddress to = RemoteAddress.Clone();
        to.Tag = RemoteTag;
        request.Headers.Append(SipHeaderName.To, to.ToString());

        request.Headers.Append(SipHeaderName.CallId, CallId);
        request.Headers.Append(
            SipHeaderName.CSeq,
            $"{sequence.ToString(CultureInfo.InvariantCulture)} {method.Name()}");

        // Route повторяет набор маршрутов; без него запрос внутри диалога может
        // не дойти через прокси.
        foreach (SipUri route in RouteSet)
        {
            request.Headers.Append(SipHeaderName.Route, new NameAddress(route).ToString());
        }

        if (contact is not null)
        {
            request.Headers.Append(SipHeaderName.Contact, contact.ToString());
        }
        if (userAgent is not null)
        {
            request.Headers.Append(SipHeaderName.UserAgent, userAgent);
        }

        return request;
    }

    /// <summary>
    /// Следующий номер CSeq. Возвращает обновлённый диалог, чтобы номер нельзя
    /// было случайно использовать дважды.
    /// </summary>
    public (SipDialog Dialog, int Sequence) NextSequence()
    {
        int sequence = LocalSequence + 1;
        var next = new SipDialog(
            CallId,
            LocalTag,
            RemoteTag,
            LocalAddress,
            RemoteAddress,
            RemoteTarget,
            sequence,
            IsInitiator,
            RouteSet);
        return (next, sequence);
    }

    /// <summary>
    /// Совпадает ли ответ или запрос с этим диалогом.
    ///
    /// Сравниваются все три составляющих идентификатора диалога: Call-ID и оба
    /// тега. Только Call-ID недостаточно — при перезвоне он может повториться.
    /// </summary>
    public bool Matches(string callId, string? localTag, string? remoteTag)
    {
        if (!string.Equals(callId, CallId, StringComparison.Ordinal))
        {
            return false;
        }
        if (localTag is null || remoteTag is null)
        {
            return false;
        }
        return string.Equals(localTag, LocalTag, StringComparison.Ordinal)
            && string.Equals(remoteTag, RemoteTag, StringComparison.Ordinal);
    }
}
