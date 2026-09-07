namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Разбор и сериализация SDP.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/SDPTests.swift</c>.
/// </summary>
public sealed class SdpTests
{
    /// <summary>SDP ровно в том виде, в каком его присылает chan_sip из Asterisk 13.</summary>
    internal const string AsteriskOffer =
        "v=0\r\n"
        + "o=root 1962650862 1962650862 IN IP4 172.17.0.2\r\n"
        + "s=Asterisk PBX 13.38.3\r\n"
        + "c=IN IP4 172.17.0.2\r\n"
        + "t=0 0\r\n"
        + "m=audio 14028 RTP/AVP 0 8 101\r\n"
        + "a=rtpmap:0 PCMU/8000\r\n"
        + "a=rtpmap:8 PCMA/8000\r\n"
        + "a=rtpmap:101 telephone-event/8000\r\n"
        + "a=fmtp:101 0-16\r\n"
        + "a=maxptime:150\r\n"
        + "a=sendrecv\r\n";

    [Fact]
    public void Разбирает_предложение_от_chan_sip()
    {
        SessionDescription sdp = SdpParser.Parse(AsteriskOffer);

        Assert.Equal("root", sdp.Origin.Username);
        Assert.Equal(1962650862ul, sdp.Origin.SessionId);
        Assert.Equal("172.17.0.2", sdp.Connection?.Address);
        Assert.Equal("Asterisk PBX 13.38.3", sdp.SessionName);

        MediaDescription audio = Assert.IsType<MediaDescription>(sdp.Audio);
        Assert.Equal(14028, audio.Port);
        Assert.Equal("RTP/AVP", audio.ProtocolName);
        Assert.Equal<byte[]>([0, 8, 101], [.. audio.Formats]);
        Assert.Equal(MediaDirection.SendRecv, audio.Direction);

        IReadOnlyDictionary<byte, RtpMap> maps = audio.RtpMaps;
        Assert.Equal("PCMU", maps[0].EncodingName);
        Assert.Equal("PCMA", maps[8].EncodingName);
        Assert.True(maps[101].IsTelephoneEvent);
        Assert.Equal(8000u, maps[101].ClockRate);

        // Незнакомые строки не теряются — иначе при отладке непонятно, что
        // прислал сервер.
        Assert.Equal("150", audio.Attribute("maxptime"));
    }

    [Fact]
    public void Направление_по_умолчанию_sendrecv()
    {
        SessionDescription sdp = SdpParser.Parse(
            "v=0\r\no=- 1 1 IN IP4 10.0.0.1\r\ns=-\r\nc=IN IP4 10.0.0.1\r\nt=0 0\r\n"
            + "m=audio 4000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n");

        // RFC 4566 §6: отсутствие атрибута равносильно sendrecv.
        Assert.Equal(MediaDirection.SendRecv, sdp.Audio?.Direction);
    }

    [Fact]
    public void Читает_удержание_как_sendonly_и_порт_0()
    {
        SessionDescription sdp = SdpParser.Parse(
            "v=0\r\no=- 1 2 IN IP4 10.0.0.1\r\ns=-\r\nc=IN IP4 10.0.0.1\r\nt=0 0\r\n"
            + "m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendonly\r\n");

        Assert.Equal(MediaDirection.SendOnly, sdp.Audio?.Direction);
        Assert.Equal<ushort?>(0, sdp.Audio?.Port);
    }

    [Fact]
    public void Round_trip_сохраняет_смысл()
    {
        SessionDescription original = SdpParser.Parse(AsteriskOffer);
        SessionDescription reparsed = SdpParser.Parse(original.Encoded());

        Assert.Equal(original.Origin, reparsed.Origin);
        Assert.Equal(original.Connection, reparsed.Connection);
        Assert.Equal(original.Audio?.Port, reparsed.Audio?.Port);
        Assert.Equal(original.Audio?.Formats, reparsed.Audio?.Formats);
        Assert.Equal(original.Audio?.RtpMaps, reparsed.Audio?.RtpMaps);
        Assert.Equal(original.Audio?.Direction, reparsed.Audio?.Direction);
    }

    [Fact]
    public void Сериализация_соблюдает_порядок_строк_по_RFC_4566()
    {
        SessionDescription sdp = SdpNegotiator.MakeOffer("10.0.0.5", 10000, sessionId: 42);
        string encoded = sdp.Encoded();
        string[] lines = encoded.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Часть реализаций разбирает SDP последовательно и на перестановке
        // строк ломается, поэтому порядок проверяется явно.
        Assert.Equal("v=0", lines[0]);
        Assert.StartsWith("o=", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("s=", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("c=", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("t=", lines[4], StringComparison.Ordinal);
        Assert.StartsWith("m=", lines[5], StringComparison.Ordinal);

        // Все a= после m= относятся к секции.
        Assert.All(lines.Skip(6), line => Assert.StartsWith("a=", line, StringComparison.Ordinal));
        Assert.EndsWith("\r\n", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Ошибки_разбора_называются_своими_именами()
    {
        Assert.Equal(SdpParseFailure.Empty, Failure(string.Empty));
        Assert.Equal(SdpParseFailure.MissingOrigin, Failure("s=нет origin\r\n"));
        Assert.Equal(SdpParseFailure.UnsupportedVersion, Failure("v=1\r\n"));
        Assert.Equal(SdpParseFailure.MalformedOrigin, Failure("v=0\r\no=мало полей\r\n"));
        Assert.Equal(
            SdpParseFailure.MalformedMedia,
            Failure("v=0\r\no=- 1 1 IN IP4 h\r\nm=audio плохо RTP/AVP 0\r\n"));

        static SdpParseFailure Failure(string text) =>
            Assert.Throws<SdpParseException>(() => SdpParser.Parse(text)).Failure;
    }

    [Fact]
    public void Адрес_с_TTL_и_порт_с_числом_каналов_не_ломают_разбор()
    {
        SessionDescription sdp = SdpParser.Parse(
            "v=0\r\no=- 1 1 IN IP4 10.0.0.1\r\ns=-\r\nc=IN IP4 224.0.0.1/127\r\nt=0 0\r\n"
            + "m=audio 5004/2 RTP/AVP 0\r\n");

        Assert.Equal("224.0.0.1", sdp.Connection?.Address);
        Assert.Equal<ushort?>(5004, sdp.Audio?.Port);
    }

    [Fact]
    public void Строка_c_внутри_секции_важнее_сессионной()
    {
        SessionDescription sdp = SdpParser.Parse(
            "v=0\r\no=- 1 1 IN IP4 10.0.0.1\r\ns=-\r\nc=IN IP4 10.0.0.1\r\nt=0 0\r\n"
            + "m=audio 4000 RTP/AVP 0\r\nc=IN IP4 192.168.1.77\r\na=rtpmap:0 PCMU/8000\r\n");

        Assert.Equal("10.0.0.1", sdp.Connection?.Address);
        Assert.Equal("192.168.1.77", sdp.Audio?.Connection?.Address);

        NegotiatedMedia media = SdpNegotiator.ResolveAnswer(sdp, SdpNegotiator.MakeOffer("10.0.0.5", 10000));
        Assert.Equal("192.168.1.77", media.RemoteAddress);
    }
}

/// <summary>Согласование SDP по RFC 3264.</summary>
public sealed class SdpNegotiationTests
{
    [Fact]
    public void Защищённое_предложение_использует_RTP_SAVP_и_валидный_SDES()
    {
        SessionDescription offer = SdpNegotiator.MakeOffer(
            "10.0.0.5",
            10000,
            security: MediaSecurityPolicy.SdesRequired);

        MediaDescription audio = Assert.IsType<MediaDescription>(offer.Audio);
        SdesCryptoLine crypto = Assert.Single(audio.SdesCryptoLines);

        Assert.Equal("RTP/SAVP", audio.ProtocolName);
        Assert.Equal(1u, crypto.Tag);
        Assert.Equal(SrtpCryptoSuite.AesCm128HmacSha1Tag80, crypto.Suite);
        Assert.Equal(30, crypto.Key.Bytes.Length);
        Assert.Contains("a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:", offer.Encoded(), StringComparison.Ordinal);
    }

    [Fact]
    public void SDES_согласует_разные_ключи_направлений()
    {
        SessionDescription offer = SdpNegotiator.MakeOffer(
            "10.0.0.5",
            10000,
            security: MediaSecurityPolicy.SdesRequired);
        SdesCryptoLine local = Assert.Single(Assert.IsType<MediaDescription>(offer.Audio).SdesCryptoLines);

        SrtpMasterKey remoteKey = SrtpMasterKey.Random();
        SessionDescription answer = SdpParser.Parse(
            "v=0\r\no=root 1 1 IN IP4 172.17.0.2\r\ns=Asterisk\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
            + "m=audio 14028 RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + $"a=crypto:{new SdesCryptoLine(local.Tag, local.Suite, remoteKey).Value}\r\n");

        NegotiatedMedia media = SdpNegotiator.ResolveAnswer(answer, offer);
        Assert.Equal(MediaSecurity.Sdes(local.Key, remoteKey), media.Security);
    }

    [Fact]
    public void SRTP_не_откатывается_на_открытый_RTP()
    {
        SessionDescription offer = SdpNegotiator.MakeOffer(
            "10.0.0.5",
            10000,
            security: MediaSecurityPolicy.SdesRequired);
        SessionDescription answer = SdpParser.Parse(
            "v=0\r\no=root 1 1 IN IP4 172.17.0.2\r\ns=Asterisk\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
            + "m=audio 14028 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n");

        Assert.Equal(
            SdpNegotiationFailure.SecureMediaRequired,
            Assert.Throws<SdpNegotiationException>(() => SdpNegotiator.ResolveAnswer(answer, offer)).Failure);
    }

    [Fact]
    public void Предложение_содержит_всё_чего_ждёт_Asterisk()
    {
        SessionDescription offer = SdpNegotiator.MakeOffer("10.0.0.5", 10000, sessionId: 7);
        MediaDescription audio = Assert.IsType<MediaDescription>(offer.Audio);

        // PCMU первым — на нём идёт боевой разговор; G.722 остаётся, но последним.
        Assert.Equal<byte[]>([0, 8, 9, 101], [.. audio.Formats]);
        Assert.Equal("PCMU", audio.RtpMaps[0].EncodingName);
        Assert.Equal("G722", audio.RtpMaps[9].EncodingName);

        // В SDP у G.722 объявляется частота 8000, хотя оцифровывает он на
        // 16 000. Это ошибка RFC 1890, оставленная в RFC 3551 §4.5.2 ради
        // совместимости: серверы ждут именно 8000, и «исправление» здесь ломает
        // согласование.
        Assert.Equal(8000u, audio.RtpMaps[9].ClockRate);
        Assert.True(audio.RtpMaps[101].IsTelephoneEvent);

        // Без fmtp часть серверов не понимает, какие события мы принимаем, и
        // отбрасывает DTMF.
        Assert.Equal("101 0-16", audio.Attribute("fmtp"));
        Assert.Equal("20", audio.Attribute("ptime"));
        Assert.Equal(MediaDirection.SendRecv, audio.Direction);
        Assert.Equal("10.0.0.5", offer.Connection?.Address);
    }

    [Fact]
    public void Разбирает_ответ_Asterisk_и_выбирает_кодек_по_его_порядку()
    {
        SessionDescription offer = SdpNegotiator.MakeOffer("10.0.0.5", 10000);
        SessionDescription answer = SdpParser.Parse(
            "v=0\r\no=root 1 1 IN IP4 172.17.0.2\r\ns=Asterisk PBX 13.38.3\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
            + "m=audio 14028 RTP/AVP 8 101\r\na=rtpmap:8 PCMA/8000\r\n"
            + "a=rtpmap:101 telephone-event/8000\r\na=ptime:20\r\na=sendrecv\r\n");

        NegotiatedMedia media = SdpNegotiator.ResolveAnswer(answer, offer);

        // Мы предлагали PCMU первым, сервер выбрал PCMA — слушаемся сервера.
        Assert.Equal(AudioCodec.Pcma, media.Codec);
        Assert.Equal(8, media.PayloadType);
        Assert.Equal<byte?>(101, media.TelephoneEventPayloadType);
        Assert.Equal("172.17.0.2", media.RemoteAddress);
        Assert.Equal(14028, media.RemotePort);
        Assert.Equal(MediaDirection.SendRecv, media.Direction);
        Assert.False(media.IsHeld);
    }

    [Fact]
    public void Опознаёт_статический_payload_type_без_rtpmap()
    {
        // chan_sip иногда не присылает rtpmap для 0 и 8: RFC 3551 закрепляет их
        // жёстко. Требовать rtpmap в этом случае — значит рвать совместимость.
        SessionDescription answer = SdpParser.Parse(
            "v=0\r\no=root 1 1 IN IP4 172.17.0.2\r\ns=-\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
            + "m=audio 14028 RTP/AVP 0\r\n");

        NegotiatedMedia media = SdpNegotiator.ResolveAnswer(answer, SdpNegotiator.MakeOffer("10.0.0.5", 10000));
        Assert.Equal(AudioCodec.Pcmu, media.Codec);
        Assert.Equal(0, media.PayloadType);
        Assert.Null(media.TelephoneEventPayloadType);
    }

    [Fact]
    public void Отсутствие_общего_кодека_явная_ошибка()
    {
        SessionDescription answer = SdpParser.Parse(
            "v=0\r\no=root 1 1 IN IP4 172.17.0.2\r\ns=-\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
            + "m=audio 14028 RTP/AVP 18\r\na=rtpmap:18 G729/8000\r\n");

        // G.729 требует лицензии и отдельного модуля — мы его не предлагаем и
        // принять не можем.
        SdpNegotiationException error = Assert.Throws<SdpNegotiationException>(
            () => SdpNegotiator.ResolveAnswer(answer, SdpNegotiator.MakeOffer("10.0.0.5", 10000)));
        Assert.Equal(SdpNegotiationFailure.NoCommonCodec, error.Failure);
        Assert.Equal("18", error.Detail);
    }

    [Fact]
    public void Порт_0_в_ответе_означает_удержание()
    {
        SessionDescription answer = SdpParser.Parse(
            "v=0\r\no=root 1 2 IN IP4 172.17.0.2\r\ns=-\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
            + "m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendonly\r\n");

        NegotiatedMedia media = SdpNegotiator.ResolveAnswer(answer, SdpNegotiator.MakeOffer("10.0.0.5", 10000));
        Assert.True(media.IsHeld);

        // Собеседник только отправляет — значит мы только слушаем.
        Assert.Equal(MediaDirection.RecvOnly, media.Direction);
    }

    [Fact]
    public void Составляет_ответ_на_чужое_предложение()
    {
        SessionDescription offer = SdpParser.Parse(SdpTests.AsteriskOffer);
        (SessionDescription answer, NegotiatedMedia media) =
            SdpNegotiator.MakeAnswer(offer, "10.0.0.5", 10500);

        MediaDescription audio = Assert.IsType<MediaDescription>(answer.Audio);
        Assert.Equal(10500, audio.Port);
        Assert.Equal(0, audio.Formats[0]);
        Assert.Contains<byte>(101, audio.Formats);
        Assert.Equal("10.0.0.5", answer.Connection?.Address);
        Assert.Equal(MediaDirection.SendRecv, audio.Direction);

        Assert.Equal(AudioCodec.Pcmu, media.Codec);
        Assert.Equal("172.17.0.2", media.RemoteAddress);
        Assert.Equal(14028, media.RemotePort);
    }

    [Fact]
    public void Ответ_на_удержание_разворачивает_направление()
    {
        SessionDescription holdOffer = SdpParser.Parse(
            "v=0\r\no=root 1 2 IN IP4 172.17.0.2\r\ns=-\r\nc=IN IP4 172.17.0.2\r\nt=0 0\r\n"
            + "m=audio 14028 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendonly\r\n");

        (SessionDescription answer, NegotiatedMedia media) =
            SdpNegotiator.MakeAnswer(holdOffer, "10.0.0.5", 10500);

        // Собеседник только отправляет — значит мы обязаны объявить recvonly.
        Assert.Equal(MediaDirection.RecvOnly, answer.Audio?.Direction);
        Assert.Equal(MediaDirection.RecvOnly, media.Direction);
    }

    [Fact]
    public void Повторное_предложение_меняет_направление_и_версию_не_трогая_прежнее()
    {
        SessionDescription offer = SdpNegotiator.MakeOffer("10.0.0.5", 10000, sessionId: 7);
        SessionDescription reoffer = SdpNegotiator.MakeReoffer(offer, MediaDirection.SendOnly);

        Assert.Equal(2ul, reoffer.Origin.SessionVersion);
        Assert.Equal(MediaDirection.SendOnly, reoffer.Audio?.Direction);
        Assert.Equal<ushort?>(10000, reoffer.Audio?.Port);

        // Прежнее описание уже уехало собеседнику — править его задним числом
        // нельзя.
        Assert.Equal(1ul, offer.Origin.SessionVersion);
        Assert.Equal(MediaDirection.SendRecv, offer.Audio?.Direction);
    }

    [Fact]
    public void Таблица_пересечения_направлений()
    {
        Assert.Equal(
            MediaDirection.SendRecv,
            SdpNegotiator.Intersect(MediaDirection.SendRecv, MediaDirection.SendRecv));
        Assert.Equal(
            MediaDirection.RecvOnly,
            SdpNegotiator.Intersect(MediaDirection.SendRecv, MediaDirection.SendOnly));
        Assert.Equal(
            MediaDirection.SendOnly,
            SdpNegotiator.Intersect(MediaDirection.SendRecv, MediaDirection.RecvOnly));
        Assert.Equal(
            MediaDirection.Inactive,
            SdpNegotiator.Intersect(MediaDirection.SendRecv, MediaDirection.Inactive));
        Assert.Equal(
            MediaDirection.Inactive,
            SdpNegotiator.Intersect(MediaDirection.SendOnly, MediaDirection.SendOnly));
        Assert.Equal(
            MediaDirection.RecvOnly,
            SdpNegotiator.Intersect(MediaDirection.RecvOnly, MediaDirection.SendRecv));
        Assert.Equal(
            MediaDirection.Inactive,
            SdpNegotiator.Intersect(MediaDirection.Inactive, MediaDirection.SendRecv));
    }
}
