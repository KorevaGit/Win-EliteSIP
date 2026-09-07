using System.Globalization;

namespace EliteSIP.MediaCore;

/// <summary>Итог согласования: всё, что нужно медиа-слою, чтобы начать говорить.</summary>
public sealed record NegotiatedMedia(
    AudioCodec Codec,
    byte PayloadType,
    string RemoteAddress,
    ushort RemotePort,
    byte? TelephoneEventPayloadType = null,
    MediaDirection Direction = MediaDirection.SendRecv,
    int PacketTimeMilliseconds = AudioCodecInfo.DefaultPacketTimeMilliseconds,
    MediaSecurity? Security = null)
{
    /// <summary>Защита потока. Отсутствие записи означает открытый RTP.</summary>
    public MediaSecurity Security { get; init; } = Security ?? MediaSecurity.None;

    /// <summary>
    /// Поток отключён целиком: отправлять физически некуда.
    ///
    /// Нулевой порт и адрес <c>0.0.0.0</c> — старая запись удержания из
    /// RFC 2543, и chan_sip до сих пор шлёт именно её, иногда вообще не трогая
    /// направление. Отличать её от нового <c>a=sendonly</c> приходится вот
    /// зачем: на этот адрес нельзя пересобирать сокет, его надо просто
    /// переждать.
    /// </summary>
    public bool IsStreamDisabled => RemotePort == 0 || RemoteAddress == "0.0.0.0";

    /// <summary>
    /// Собеседник поставил нас на удержание: звука от нас больше не ждут.
    ///
    /// Заметить это важнее, чем кажется: если пропустить, оператор продолжит
    /// говорить в линию, где его никто не слышит, и узнает об этом только по
    /// недоумению собеседника после возврата.
    /// </summary>
    public bool IsHeld => IsStreamDisabled || !Direction.SendsAudio();
}

/// <summary>Почему согласование не состоялось.</summary>
public enum SdpNegotiationFailure
{
    NoAudioSection,
    NoCommonCodec,
    NoRemoteAddress,
    SecureMediaRequired,
    MissingCryptoAttribute,
    CryptoTagMismatch,
    UnsupportedMediaProtocol,
}

/// <summary>Согласование SDP не состоялось.</summary>
public sealed class SdpNegotiationException : Exception
{
    public SdpNegotiationException(SdpNegotiationFailure failure, string detail = "")
        : base(Describe(failure, detail))
    {
        Failure = failure;
        Detail = detail;
    }

    public SdpNegotiationException()
        : this(SdpNegotiationFailure.NoAudioSection)
    {
    }

    public SdpNegotiationException(string message)
        : base(message) => Detail = message;

    public SdpNegotiationException(string message, Exception innerException)
        : base(message, innerException) => Detail = message;

    public SdpNegotiationFailure Failure { get; }

    public string Detail { get; } = string.Empty;

    private static string Describe(SdpNegotiationFailure failure, string detail) => failure switch
    {
        SdpNegotiationFailure.NoAudioSection => "в SDP нет аудио-секции",
        SdpNegotiationFailure.NoCommonCodec => $"нет общего кодека, предложены payload type: {detail}",
        SdpNegotiationFailure.NoRemoteAddress => "в SDP не указан адрес для медиа",
        SdpNegotiationFailure.SecureMediaRequired => "сервер не подтвердил обязательный SRTP",
        SdpNegotiationFailure.MissingCryptoAttribute => "в защищённом SDP нет поддерживаемого атрибута a=crypto",
        SdpNegotiationFailure.CryptoTagMismatch => "сервер выбрал неизвестное предложение a=crypto",
        SdpNegotiationFailure.UnsupportedMediaProtocol => $"неподдерживаемый протокол медиа {detail}",
        _ => "согласование SDP не удалось",
    };
}

public enum MediaSecurityPolicy
{
    None,
    SdesRequired,
}

/// <summary>Составление предложения и разбор ответа по RFC 3264.</summary>
public static class SdpNegotiator
{
    private const string PlainProtocol = "RTP/AVP";
    private const string SecureProtocol = "RTP/SAVP";

    /// <summary>
    /// Кодеки, которые предлагаем по умолчанию, в порядке предпочтения.
    ///
    /// G.711 первым, PCMU перед PCMA — так настроен боевой пир: боевой
    /// <c>allow</c> идёт в порядке <c>(ulaw|alaw|gsm|g726|g722)</c>, Asterisk
    /// как отвечающая сторона выбирает кодек по своей очереди, и разговор идёт
    /// на G.711 независимо от того, что стояло первым у нас. Широкую полосу на
    /// сервере решено не поднимать: в город всё равно уходит G.711 через транк,
    /// а лишний транскодинг платится процессорным временем сервера на каждом
    /// плече.
    ///
    /// G.722 остаётся в предложении последним, и это не рудимент. Он ничего не
    /// стоит против боя (его просто не выберут), но оставляет разговор
    /// широкополосным там, где стороны договариваются между собой.
    /// </summary>
    public static IReadOnlyList<AudioCodec> DefaultCodecs { get; } =
        [AudioCodec.Pcmu, AudioCodec.Pcma, AudioCodec.G722];

    /// <summary>
    /// Кодеки без широкой полосы. Пригодится, когда понадобится заставить
    /// разговор идти узкой полосой, не трогая остальную настройку.
    /// </summary>
    public static IReadOnlyList<AudioCodec> NarrowbandCodecs { get; } = [AudioCodec.Pcmu, AudioCodec.Pcma];

    /// <summary>Идентификатор сессии по умолчанию — секунды эпохи, как в оригинале.</summary>
    public static ulong DefaultSessionId() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Наше предложение.
    ///
    /// Порядок кодеков — это порядок предпочтения, и отвечающая сторона обычно
    /// его уважает.
    /// </summary>
    public static SessionDescription MakeOffer(
        string address,
        ushort port,
        IReadOnlyList<AudioCodec>? codecs = null,
        byte? telephoneEventPayloadType = TelephoneEvent.DefaultPayloadType,
        MediaDirection direction = MediaDirection.SendRecv,
        ulong? sessionId = null,
        ulong sessionVersion = 1,
        int packetTimeMilliseconds = AudioCodecInfo.DefaultPacketTimeMilliseconds,
        MediaSecurityPolicy security = MediaSecurityPolicy.None)
    {
        codecs ??= DefaultCodecs;

        List<byte> formats = [.. codecs.Select(codec => codec.PayloadType())];
        List<SdpAttributeLine> attributes =
        [
            .. codecs.Select(codec => new SdpAttributeLine(
                "rtpmap",
                new RtpMap(codec.PayloadType(), codec.SdpName(), codec.RtpClockRate()).Value)),
        ];

        if (telephoneEventPayloadType is byte eventType)
        {
            formats.Add(eventType);
            attributes.Add(new SdpAttributeLine(
                "rtpmap",
                new RtpMap(eventType, TelephoneEvent.SdpName, TelephoneEvent.ClockRate).Value));
            // fmtp обязателен: без него часть серверов не понимает, какие
            // события мы умеем принимать, и отбрасывает DTMF.
            attributes.Add(new SdpAttributeLine("fmtp", $"{eventType} {TelephoneEvent.SupportedEventRange}"));
        }

        attributes.Add(new SdpAttributeLine("ptime", packetTimeMilliseconds.ToString(CultureInfo.InvariantCulture)));
        attributes.Add(new SdpAttributeLine(direction.AttributeName()));

        string protocolName = PlainProtocol;
        if (security is MediaSecurityPolicy.SdesRequired)
        {
            protocolName = SecureProtocol;
            attributes.Add(new SdpAttributeLine("crypto", new SdesCryptoLine(SrtpMasterKey.Random()).Value));
        }

        return new SessionDescription(
            new SdpOrigin(sessionId ?? DefaultSessionId(), address, SessionVersion: sessionVersion),
            connection: new SdpConnection(address),
            media: [new MediaDescription(port, protocolName: protocolName, formats: formats, attributes: attributes)]);
    }

    /// <summary>
    /// Повторное предложение внутри уже идущего разговора.
    ///
    /// Строится правкой прежнего описания, а не сборкой нового, и это
    /// принципиально. Порт менять нельзя — он объявлен и занят. Ключ SDES
    /// перевыпускать нельзя — по нему собеседник расшифровывает наш поток, и
    /// новый ключ означает пересборку контекстов на обеих сторонах ради смены
    /// одной строчки направления. Набор кодеков менять тоже незачем:
    /// договорились один раз.
    ///
    /// Расти обязана только версия сессии в <c>o=</c>: по ней принимающая
    /// сторона понимает, что описание новое. Неизменная версия — законный повод
    /// проигнорировать предложение целиком (RFC 3264 §8), и Asterisk этим
    /// правом пользуется.
    /// </summary>
    public static SessionDescription MakeReoffer(SessionDescription previous, MediaDirection direction)
    {
        ArgumentNullException.ThrowIfNull(previous);

        // Копия обязательна: прежнее описание уже уехало собеседнику, и править
        // его на месте значило бы задним числом менять то, о чём договорились.
        SessionDescription reoffer = previous.Clone();
        reoffer.Origin = reoffer.Origin with { SessionVersion = unchecked(reoffer.Origin.SessionVersion + 1) };

        foreach (MediaDescription section in reoffer.Media.Where(section => section.Type == "audio"))
        {
            section.Attributes.RemoveAll(attribute => MediaDirectionInfo.All
                .Any(candidate => string.Equals(
                    candidate.AttributeName(),
                    attribute.Name,
                    StringComparison.OrdinalIgnoreCase)));
            section.Attributes.Add(new SdpAttributeLine(direction.AttributeName()));
        }

        return reoffer;
    }

    /// <summary>Разбирает ответ на наше предложение.</summary>
    public static NegotiatedMedia ResolveAnswer(
        SessionDescription answer,
        SessionDescription offer,
        IReadOnlyList<AudioCodec>? supported = null)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(offer);
        supported ??= DefaultCodecs;

        MediaDescription audio = answer.Audio
            ?? throw new SdpNegotiationException(SdpNegotiationFailure.NoAudioSection);

        string address = audio.Connection?.Address ?? answer.Connection?.Address
            ?? throw new SdpNegotiationException(SdpNegotiationFailure.NoRemoteAddress);

        // Кодек выбираем по порядку ОТВЕТА: отвечающая сторона говорит, чем она
        // будет пользоваться, и навязывать ей свой порядок нельзя.
        (AudioCodec Codec, byte PayloadType) chosen = ChooseCodec(audio, supported);
        byte? eventType = TelephoneEventPayloadType(audio);

        // Наше направление — это встречное к тому, что объявил собеседник,
        // пересечённое с тем, что мы просили в предложении.
        MediaDirection offeredDirection = offer.Audio?.Direction ?? MediaDirection.SendRecv;

        return new NegotiatedMedia(
            chosen.Codec,
            chosen.PayloadType,
            address,
            audio.Port,
            eventType,
            Intersect(offeredDirection, audio.Direction),
            audio.PacketTimeMilliseconds ?? AudioCodecInfo.DefaultPacketTimeMilliseconds,
            ResolveSecurity(audio, offer.Audio));
    }

    /// <summary>
    /// Составляет ответ на чужое предложение и заодно возвращает итог.
    /// </summary>
    /// <param name="localKey">
    /// Наш ключ SRTP. Задаётся при пересогласовании: в ответе на повторный
    /// INVITE ключ обязан остаться прежним, иначе поток придётся пересобирать
    /// на каждое удержание.
    /// </param>
    public static (SessionDescription Answer, NegotiatedMedia Media) MakeAnswer(
        SessionDescription offer,
        string address,
        ushort port,
        IReadOnlyList<AudioCodec>? supported = null,
        ulong? sessionId = null,
        int packetTimeMilliseconds = AudioCodecInfo.DefaultPacketTimeMilliseconds,
        SrtpMasterKey? localKey = null)
    {
        ArgumentNullException.ThrowIfNull(offer);
        supported ??= DefaultCodecs;

        MediaDescription audio = offer.Audio
            ?? throw new SdpNegotiationException(SdpNegotiationFailure.NoAudioSection);

        string remoteAddress = audio.Connection?.Address ?? offer.Connection?.Address
            ?? throw new SdpNegotiationException(SdpNegotiationFailure.NoRemoteAddress);

        (AudioCodec codec, byte payloadType) = ChooseCodec(audio, supported);
        byte? eventType = TelephoneEventPayloadType(audio);
        MediaDirection direction = audio.Direction.Reversed();

        List<byte> formats = [payloadType];
        List<SdpAttributeLine> attributes =
        [
            new SdpAttributeLine("rtpmap", new RtpMap(payloadType, codec.SdpName(), codec.RtpClockRate()).Value),
        ];

        if (eventType is byte confirmed)
        {
            formats.Add(confirmed);
            attributes.Add(new SdpAttributeLine(
                "rtpmap",
                new RtpMap(confirmed, TelephoneEvent.SdpName, TelephoneEvent.ClockRate).Value));
            attributes.Add(new SdpAttributeLine("fmtp", $"{confirmed} {TelephoneEvent.SupportedEventRange}"));
        }

        attributes.Add(new SdpAttributeLine("ptime", packetTimeMilliseconds.ToString(CultureInfo.InvariantCulture)));
        attributes.Add(new SdpAttributeLine(direction.AttributeName()));

        string protocolName;
        MediaSecurity security;
        switch (audio.ProtocolName.ToUpperInvariant())
        {
            case PlainProtocol:
                protocolName = PlainProtocol;
                security = MediaSecurity.None;
                break;

            case SecureProtocol:
            {
                SdesCryptoLine remoteCrypto = FirstCrypto(audio)
                    ?? throw new SdpNegotiationException(SdpNegotiationFailure.MissingCryptoAttribute);
                SrtpMasterKey key = localKey ?? SrtpMasterKey.Random();
                attributes.Add(new SdpAttributeLine(
                    "crypto",
                    new SdesCryptoLine(remoteCrypto.Tag, remoteCrypto.Suite, key).Value));
                protocolName = SecureProtocol;
                security = MediaSecurity.Sdes(key, remoteCrypto.Key);
                break;
            }

            default:
                throw new SdpNegotiationException(
                    SdpNegotiationFailure.UnsupportedMediaProtocol,
                    audio.ProtocolName);
        }

        SessionDescription answer = new(
            new SdpOrigin(sessionId ?? DefaultSessionId(), address),
            connection: new SdpConnection(address),
            media: [new MediaDescription(port, protocolName: protocolName, formats: formats, attributes: attributes)]);

        NegotiatedMedia media = new(
            codec,
            payloadType,
            remoteAddress,
            audio.Port,
            eventType,
            direction,
            audio.PacketTimeMilliseconds ?? packetTimeMilliseconds,
            security);

        return (answer, media);
    }

    /// <summary>
    /// Пересечение направлений двух сторон.
    ///
    /// Если мы просили только слушать, а собеседник только слушает, говорить
    /// некому — получается inactive. Эта таблица и есть логика удержания.
    /// </summary>
    internal static MediaDirection Intersect(MediaDirection ours, MediaDirection theirs)
    {
        bool weSend = ours.SendsAudio() && theirs.ReceivesAudio();
        bool weReceive = ours.ReceivesAudio() && theirs.SendsAudio();

        return (weSend, weReceive) switch
        {
            (true, true) => MediaDirection.SendRecv,
            (true, false) => MediaDirection.SendOnly,
            (false, true) => MediaDirection.RecvOnly,
            _ => MediaDirection.Inactive,
        };
    }

    /// <summary>
    /// Выбор кодека из чужого списка форматов.
    ///
    /// Статические payload type (0 и 8) можно опознать без rtpmap — RFC 3551
    /// закрепляет их жёстко, и chan_sip иногда rtpmap не шлёт.
    /// </summary>
    private static (AudioCodec Codec, byte PayloadType) ChooseCodec(
        MediaDescription audio,
        IReadOnlyList<AudioCodec> supported)
    {
        IReadOnlyDictionary<byte, RtpMap> maps = audio.RtpMaps;
        foreach (byte format in audio.Formats)
        {
            if (maps.TryGetValue(format, out RtpMap? map))
            {
                foreach (AudioCodec codec in supported)
                {
                    if (map.Matches(codec))
                    {
                        return (codec, format);
                    }
                }
            }
            else if (AudioCodecInfo.FromStaticPayloadType(format) is AudioCodec staticCodec
                && supported.Contains(staticCodec))
            {
                return (staticCodec, format);
            }
        }

        throw new SdpNegotiationException(
            SdpNegotiationFailure.NoCommonCodec,
            string.Join(", ", audio.Formats));
    }

    /// <summary>Первое корректное предложение SDES в секции, если оно есть.</summary>
    private static SdesCryptoLine? FirstCrypto(MediaDescription section)
    {
        IReadOnlyList<SdesCryptoLine> lines = section.SdesCryptoLines;
        return lines.Count > 0 ? lines[0] : null;
    }

    private static byte? TelephoneEventPayloadType(MediaDescription audio)
    {
        IReadOnlyDictionary<byte, RtpMap> maps = audio.RtpMaps;
        foreach (byte format in audio.Formats)
        {
            if (maps.TryGetValue(format, out RtpMap? map) && map.IsTelephoneEvent)
            {
                return format;
            }
        }

        return null;
    }

    private static MediaSecurity ResolveSecurity(MediaDescription answer, MediaDescription? offer)
    {
        if (offer is null)
        {
            throw new SdpNegotiationException(SdpNegotiationFailure.NoAudioSection);
        }

        switch (offer.ProtocolName.ToUpperInvariant())
        {
            case PlainProtocol:
                if (!string.Equals(answer.ProtocolName, PlainProtocol, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SdpNegotiationException(
                        SdpNegotiationFailure.UnsupportedMediaProtocol,
                        answer.ProtocolName);
                }

                return MediaSecurity.None;

            case SecureProtocol:
            {
                if (!string.Equals(answer.ProtocolName, SecureProtocol, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SdpNegotiationException(SdpNegotiationFailure.SecureMediaRequired);
                }

                SdesCryptoLine? localCrypto = FirstCrypto(offer);
                SdesCryptoLine? remoteCrypto = FirstCrypto(answer);
                if (localCrypto is null || remoteCrypto is null)
                {
                    throw new SdpNegotiationException(SdpNegotiationFailure.MissingCryptoAttribute);
                }

                if (localCrypto.Tag != remoteCrypto.Tag || localCrypto.Suite != remoteCrypto.Suite)
                {
                    throw new SdpNegotiationException(SdpNegotiationFailure.CryptoTagMismatch);
                }

                return MediaSecurity.Sdes(localCrypto.Key, remoteCrypto.Key);
            }

            default:
                throw new SdpNegotiationException(
                    SdpNegotiationFailure.UnsupportedMediaProtocol,
                    offer.ProtocolName);
        }
    }
}
