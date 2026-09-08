using System.Runtime.CompilerServices;
using EliteSIP.Audio;
using EliteSIP.Lines;
using EliteSIP.MediaCore;
using EliteSIP.SipCore;

namespace EliteSIP.Lines.Tests;

/// <summary>
/// Медиа линии без звуковой карты и без сокета.
///
/// Записывает не состояние, а порядок вызовов: всё, ради чего слой линий
/// существует, — это кто отпускает устройство раньше и что уходит в сеть после
/// чего. Состояние такую ошибку не поймает: к концу переключения оно верное в
/// обоих случаях.
/// </summary>
internal sealed class FakeLineMedia(string name, List<string> journal) : ILineMedia
{
    public string Name { get; } = name;

    public bool SupportsTelephoneEvents { get; set; } = true;

    public bool OwnsAudio { get; private set; }

    public bool MicrophoneMuted { get; private set; } = true;

    public bool Receiving { get; private set; }

    public MediaDirection? LastReoffer { get; private set; }

    public List<DtmfSequence> SentTones { get; } = [];

    /// <summary>Отправка тонов не удалась — собеседник ничего не подтвердил.</summary>
    public bool ToneSendingFails { get; set; }

    public void SuspendAudio()
    {
        OwnsAudio = false;
        journal.Add($"{Name}: отпустил тракт");
    }

    public void ResumeAudio()
    {
        OwnsAudio = true;
        journal.Add($"{Name}: взял тракт");
    }

    public void SetMicrophoneMuted(bool muted) => MicrophoneMuted = muted;

    public void SetReceivingAudio(bool receiving) => Receiving = receiving;

    public ReadOnlyMemory<byte> MakeReoffer(MediaDirection direction)
    {
        LastReoffer = direction;
        journal.Add($"{Name}: предложение {direction.AttributeName()}");
        return new byte[] { 1 };
    }

    public MediaRenegotiation ApplyAnswer(ReadOnlyMemory<byte> answer)
    {
        journal.Add($"{Name}: ответ применён");
        return MediaRenegotiation.DirectionOnly;
    }

    public Task<bool> SendDtmfAndWaitAsync(DtmfSequence sequence)
    {
        SentTones.Add(sequence);
        journal.Add($"{Name}: тоны {sequence.DisplayText}");
        return Task.FromResult(!ToneSendingFails);
    }

    public Task StopAsync()
    {
        journal.Add($"{Name}: медиа остановлено");
        return Task.CompletedTask;
    }
}

/// <summary>Сигнализация без сервера: запоминает запросы и отвечает так, как велено.</summary>
internal sealed class FakeSignaling(List<string> journal) : ILineSignaling
{
    /// <summary>Линии, по которым повторный INVITE обязан провалиться.</summary>
    public HashSet<string> ReinviteFails { get; } = new(StringComparer.Ordinal);

    /// <summary>Что присылать в ответ на REFER.</summary>
    public List<SipTransferEvent> TransferEvents { get; } =
        [new SipTransferEvent.Accepted(), new SipTransferEvent.Succeeded()];

    public List<string> HungUp { get; } = [];

    public SipDialogIdentifier? Consultation { get; set; } = new("consult", "local", "remote");

    /// <summary>Что произошло до того, как REFER ушёл, — для проверки порядка.</summary>
    public List<string> TransferTargets { get; } = [];

    public Task<ReadOnlyMemory<byte>> ReinviteAsync(string callId, ReadOnlyMemory<byte> offer)
    {
        journal.Add($"сеть: повторный INVITE по {callId}");

        if (ReinviteFails.Contains(callId))
        {
            throw new SipRenegotiationException(SipRenegotiationErrorKind.RequestPending, 491);
        }

        return Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 2 });
    }

    public async IAsyncEnumerable<SipTransferEvent> Transfer(
        string callId,
        string target,
        SipDialogIdentifier? replacing,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TransferTargets.Add($"{callId} -> {target}"
            + (replacing is SipDialogIdentifier dialog ? $" вместо {dialog.CallId}" : string.Empty));
        journal.Add($"сеть: REFER по {callId}");

        foreach (SipTransferEvent value in TransferEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    IAsyncEnumerable<SipTransferEvent> ILineSignaling.Transfer(
        string callId,
        string target,
        SipDialogIdentifier? replacing) => Transfer(callId, target, replacing);

    public Task HangUpAsync(string callId)
    {
        HungUp.Add(callId);
        journal.Add($"сеть: отбой по {callId}");
        return Task.CompletedTask;
    }

    public SipDialogIdentifier? DialogIdentifierOf(string callId) =>
        string.Equals(callId, "consult", StringComparison.Ordinal) ? Consultation : null;
}
