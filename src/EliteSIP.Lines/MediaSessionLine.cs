using EliteSIP.Audio;
using EliteSIP.MediaCore;

namespace EliteSIP.Lines;

/// <summary>
/// Живое медиа линии: <see cref="MediaSession"/> плюс то описание, которое мы
/// объявили собеседнику.
///
/// Описание хранится здесь, а не в сессии, по той же причине, по которой сессия
/// не знает про SIP: повторное предложение — это правка прежнего описания, а не
/// сборка нового. Собрать новое — самый простой способ случайно сменить разом и
/// порт, и ключ SDES, и набор кодеков; всё это уже объявлено и смене посреди
/// диалога не подлежит.
/// </summary>
public sealed class MediaSessionLine(MediaSession session, SessionDescription local) : ILineMedia
{
    private readonly MediaSession _session = session ?? throw new ArgumentNullException(nameof(session));

    private readonly Lock _gate = new();

    private SessionDescription _local = local ?? throw new ArgumentNullException(nameof(local));

    /// <summary>Сессия под линией — для сводок и замеров.</summary>
    public MediaSession Session => _session;

    public bool SupportsTelephoneEvents => _session.SupportsTelephoneEvents;

    public bool OwnsAudio => _session.OwnsAudio;

    public void SuspendAudio() => _session.SuspendAudio();

    public void ResumeAudio() => _session.ResumeAudio();

    public void SetMicrophoneMuted(bool muted) => _session.IsMicrophoneMuted = muted;

    public void SetReceivingAudio(bool receiving) => _session.IsReceivingAudio = receiving;

    public ReadOnlyMemory<byte> MakeReoffer(MediaDirection direction)
    {
        lock (_gate)
        {
            // Предложение запоминается сразу: ответ на него разбирается
            // относительно него же, а не относительно первого предложения
            // разговора — направление там другое.
            _local = SdpNegotiator.MakeReoffer(_local, direction);
            return _local.EncodedData();
        }
    }

    public MediaRenegotiation ApplyAnswer(ReadOnlyMemory<byte> answer)
    {
        SessionDescription offer;
        lock (_gate)
        {
            offer = _local;
        }

        SessionDescription parsed = SdpParser.Parse(answer.Span);
        NegotiatedMedia media = SdpNegotiator.ResolveAnswer(parsed, offer);
        return _session.Renegotiate(media);
    }

    public RemoteReofferAnswer AnswerReoffer(ReadOnlyMemory<byte> offer)
    {
        SessionDescription parsed = SdpParser.Parse(offer.Span);

        lock (_gate)
        {
            NegotiatedMedia? current = _session.Negotiated;
            string address = _local.Audio?.Connection?.Address ?? _local.Connection?.Address ?? _local.Origin.Address;
            int packetTime = current?.PacketTimeMilliseconds ?? AudioCodecInfo.DefaultPacketTimeMilliseconds;
            SrtpMasterKey? key = current?.Security.LocalKey;

            // Кодек разговора — первым, если сервер его ещё предлагает. Выбор по
            // порядку сервера сменил бы кодек на ровном месте: тракт
            // пересобирается, оператор слышит провал.
            (SessionDescription answer, NegotiatedMedia media) = current is not null
                && TryAnswer(parsed, address, packetTime, key, [current.Codec]) is { } kept
                    ? kept
                    : SdpNegotiator.MakeAnswer(
                        parsed,
                        address,
                        _session.LocalPort,
                        packetTimeMilliseconds: packetTime,
                        localKey: key);

            // Та же сессия, выросшая версия: chan_sip, увидев прежнюю версию,
            // описание не перечитывает вовсе.
            answer.Origin = answer.Origin with
            {
                SessionId = _local.Origin.SessionId,
                Username = _local.Origin.Username,
                SessionVersion = unchecked(_local.Origin.SessionVersion + 1),
            };

            bool reformat = current is not null
                && (current.Codec != media.Codec
                    || current.PayloadType != media.PayloadType
                    || current.TelephoneEventPayloadType != media.TelephoneEventPayloadType
                    || !Equals(current.Security.RemoteKey, media.Security.RemoteKey));

            // Другой формат — перенастройка потока на том же сокете; иначе —
            // направление и, может быть, новый адрес. Микрофон и приём после
            // этого выставляет слой линий: он знает кнопку и удержания.
            MediaRenegotiation outcome = reformat ? _session.Adopt(media) : _session.Renegotiate(media);

            _local = answer;
            return new RemoteReofferAnswer(answer.EncodedData(), media, outcome);
        }
    }

    private (SessionDescription, NegotiatedMedia)? TryAnswer(
        SessionDescription offer,
        string address,
        int packetTime,
        SrtpMasterKey? key,
        IReadOnlyList<AudioCodec> codecs)
    {
        try
        {
            return SdpNegotiator.MakeAnswer(
                offer,
                address,
                _session.LocalPort,
                codecs,
                packetTimeMilliseconds: packetTime,
                localKey: key);
        }
        catch (SdpNegotiationException failure) when (failure.Failure is SdpNegotiationFailure.NoCommonCodec)
        {
            return null;
        }
    }

    public Task<bool> SendDtmfAndWaitAsync(DtmfSequence sequence) => _session.SendDtmfAndWaitAsync(sequence);

    public Task StopAsync() => _session.StopAsync();
}
