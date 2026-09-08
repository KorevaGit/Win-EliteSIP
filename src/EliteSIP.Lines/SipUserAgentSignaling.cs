using EliteSIP.SipCore;

namespace EliteSIP.Lines;

/// <summary>
/// Живая сигнализация линий — тонкая обёртка над <see cref="SipUserAgent"/>.
///
/// Обёртка ничего не решает и не хранит: всё, что здесь есть, — приведение
/// вызовов к явной адресации по Call-ID. Умолчание «единственная линия» у агента
/// осталось ради стенда и проверок, которым многолинейность не нужна; слой линий
/// им не пользуется никогда.
/// </summary>
public sealed class SipUserAgentSignaling(SipUserAgent agent) : ILineSignaling
{
    private readonly SipUserAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public Task<ReadOnlyMemory<byte>> ReinviteAsync(string callId, ReadOnlyMemory<byte> offer) =>
        _agent.ReinviteAsync(callId, offer);

    public IAsyncEnumerable<SipTransferEvent> Transfer(
        string callId,
        string target,
        SipDialogIdentifier? replacing) =>
        _agent.Transfer(callId, target, replacing);

    public Task HangUpAsync(string callId) => _agent.HangUpAsync(callId);

    public SipDialogIdentifier? DialogIdentifierOf(string callId) => _agent.DialogIdentifierOf(callId);
}
