using EliteSIP.Audio;
using EliteSIP.Lines;
using EliteSIP.MediaCore;

namespace EliteSIP.Lines.Tests;

/// <summary>
/// Медиа линии на настоящей сессии — без звуковой карты: тракт не
/// запускается, а сокет и согласование настоящие.
/// </summary>
public sealed class MediaSessionLineTests
{
    /// <summary>
    /// Свой диапазон портов: сборки тестов идут параллельно, а учёт занятых
    /// портов у каждой свой (см. MediaSessionTests).
    /// </summary>
    private const ushort PortRangeLower = 16900;
    private const ushort PortRangeUpper = 16960;

    [Fact]
    public void Ответ_на_повторное_предложение_станции_правит_прежнее_описание()
    {
        RtpPortReservation reservation = RtpPortReservation.Reserve(PortRangeLower, PortRangeUpper);
        SessionDescription local = SdpNegotiator.MakeOffer(
            "10.0.0.5",
            reservation.RtpPort,
            sessionId: 777,
            sessionVersion: 3);
        NegotiatedMedia negotiated = new(
            AudioCodec.Pcmu,
            0,
            "10.0.0.1",
            20000,
            TelephoneEvent.DefaultPayloadType,
            MediaDirection.SendRecv,
            20,
            MediaSecurity.None);

        using MediaSession session = new(negotiated, reservation);
        MediaSessionLine line = new(session, local);

        // Станция переносит поток на другое плечо и ставит на удержание. A-law
        // у неё первым, но µ-law тоже есть — кодек разговора менять незачем.
        SessionDescription reoffer = SdpNegotiator.MakeOffer(
            "10.0.0.2",
            30000,
            codecs: [AudioCodec.Pcma, AudioCodec.Pcmu],
            direction: MediaDirection.SendOnly);

        RemoteReofferAnswer answer = line.AnswerReoffer(reoffer.EncodedData());
        SessionDescription sent = SdpParser.Parse(answer.Answer.Span);

        // Та же сессия, выросшая версия, тот же порт: иначе chan_sip описание
        // не перечитает, а порт посреди диалога менять нельзя.
        Assert.Equal(777UL, sent.Origin.SessionId);
        Assert.Equal(4UL, sent.Origin.SessionVersion);
        Assert.Equal(reservation.RtpPort, sent.Audio?.Port);
        Assert.Equal("10.0.0.5", sent.Connection?.Address);

        Assert.Equal(AudioCodec.Pcmu, answer.Media.Codec);
        Assert.True(answer.Media.IsHeld);
        Assert.Equal(MediaRenegotiation.StreamRebuilt, answer.Outcome);

        // Главное — звук уходит на новое плечо, а не на прежний адрес.
        Assert.Equal("10.0.0.2", session.Negotiated?.RemoteAddress);
        Assert.Equal((ushort)30000, session.Negotiated?.RemotePort);
    }
}
