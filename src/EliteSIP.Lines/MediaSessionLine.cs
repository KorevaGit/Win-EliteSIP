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

    public Task<bool> SendDtmfAndWaitAsync(DtmfSequence sequence) => _session.SendDtmfAndWaitAsync(sequence);

    public Task StopAsync() => _session.StopAsync();
}
