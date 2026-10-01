using EliteSIP.Audio;
using EliteSIP.MediaCore;

namespace EliteSIP.Lines;

/// <summary>
/// Что слою линий нужно от медиа одной линии.
///
/// Интерфейс здесь не ради подмены реализации — она одна,
/// <see cref="MediaSessionLine"/>, — а ради проверки. Всё интересное в этом
/// слое это порядок: кто отпускает звуковую карту раньше, кто позже, что уходит
/// в сеть до чего. Порядок проверяется таблицей вызовов, а живой тракт для этого
/// требует звуковой карты, сокета и собеседника — то есть трёх поводов, чтобы
/// проверка не запускалась. Граница ровно та же, что у CallGuard и DTMF: время и
/// устройства остаются снаружи.
/// </summary>
public interface ILineMedia
{
    /// <summary>Согласован ли telephone-event: без него отправить тон нечем.</summary>
    public bool SupportsTelephoneEvents { get; }

    /// <summary>Владеет ли линия трактом прямо сейчас.</summary>
    public bool OwnsAudio { get; }

    /// <summary>
    /// Отпускает звуковую карту, оставаясь в диалоге: порты, сокет и SSRC
    /// остаются за линией, вернуть её в разговор можно без пересогласования.
    /// </summary>
    public void SuspendAudio();

    /// <summary>Забирает тракт себе и снимает собственное молчание.</summary>
    public void ResumeAudio();

    /// <summary>Микрофон нем: в линию уходит корректно закодированная тишина.</summary>
    public void SetMicrophoneMuted(bool muted);

    /// <summary>Принятое идёт в звук или отбрасывается.</summary>
    public void SetReceivingAudio(bool receiving);

    /// <summary>
    /// Повторное предложение с новым направлением: тот же порт, тот же ключ,
    /// выросшая версия сессии.
    /// </summary>
    public ReadOnlyMemory<byte> MakeReoffer(MediaDirection direction);

    /// <summary>
    /// Принимает ответ собеседника на наше повторное предложение и применяет его
    /// к идущему разговору.
    /// </summary>
    public MediaRenegotiation ApplyAnswer(ReadOnlyMemory<byte> answer);

    /// <summary>
    /// Отвечает на повторное предложение сервера и применяет его к идущему
    /// разговору: новый адрес, направление, кодек.
    ///
    /// Ответ — правка прежнего описания, как и наше повторное предложение: тот
    /// же порт, тот же ключ, та же сессия, выросшая версия.
    /// </summary>
    /// <exception cref="SdpParseException">Предложение не разобралось.</exception>
    /// <exception cref="SdpNegotiationException">Общего кодека или профиля нет.</exception>
    public RemoteReofferAnswer AnswerReoffer(ReadOnlyMemory<byte> offer);

    /// <summary>
    /// Отправляет тоны и дожидается, пока они выйдут в поток.
    ///
    /// Именно «вышли», а не «поставлены в очередь»: конференция и коды перевода
    /// подтверждаются оператору по этому событию, и подтверждать очередь значило
    /// бы показывать успех до того, как что-то произошло.
    /// </summary>
    public Task<bool> SendDtmfAndWaitAsync(DtmfSequence sequence);

    /// <summary>Останавливает медиа линии.</summary>
    public Task StopAsync();
}

/// <summary>Наш ответ на повторное предложение сервера и то, о чём договорились.</summary>
/// <param name="Answer">Описание SDP для 200 OK.</param>
/// <param name="Media">Договорённость — по ней слой линий узнаёт серверное удержание.</param>
/// <param name="Outcome">Что пришлось сделать с потоком.</param>
public sealed record RemoteReofferAnswer(
    ReadOnlyMemory<byte> Answer,
    NegotiatedMedia Media,
    MediaRenegotiation Outcome);
