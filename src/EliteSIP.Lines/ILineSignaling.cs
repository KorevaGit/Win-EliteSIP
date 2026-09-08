using EliteSIP.SipCore;

namespace EliteSIP.Lines;

/// <summary>
/// Что слою линий нужно от сигнализации.
///
/// Подмножество <see cref="SipUserAgent"/> и по той же причине, что и
/// <see cref="ILineMedia"/>: порядок запросов проверяется без сервера. Все
/// методы адресуются Call-ID явно — другого ключа у линии нет, а умолчание
/// «единственная линия» при двух и более означало бы положить трубку не тому.
/// </summary>
public interface ILineSignaling
{
    /// <summary>Повторный INVITE внутри диалога. Возвращает SDP собеседника.</summary>
    public Task<ReadOnlyMemory<byte>> ReinviteAsync(string callId, ReadOnlyMemory<byte> offer);

    /// <summary>
    /// REFER по линии <paramref name="callId"/>. При консультационном переводе
    /// заменяемый диалог называется в <paramref name="replacing"/>.
    /// </summary>
    public IAsyncEnumerable<SipTransferEvent> Transfer(string callId, string target, SipDialogIdentifier? replacing);

    /// <summary>Кладёт трубку на одной линии.</summary>
    public Task HangUpAsync(string callId);

    /// <summary>Идентификатор диалога линии — для <c>Replaces</c>.</summary>
    public SipDialogIdentifier? DialogIdentifierOf(string callId);
}
