using EliteSIP.Lines;
using EliteSIP.MediaCore;
using EliteSIP.SipCore;

namespace EliteSIP.Lines.Tests;

/// <summary>
/// Проверки слоя линий. Здесь нет ни звуковой карты, ни сервера: всё, что этот
/// слой решает, — это порядок и адресация, и проверяется он таблицей вызовов.
/// </summary>
public sealed class LineControllerTests
{
    private readonly List<string> _journal = [];

    private readonly FakeSignaling _signaling;

    private readonly LineController _controller;

    public LineControllerTests()
    {
        _signaling = new FakeSignaling(_journal);
        _controller = new LineController(_signaling, message => _journal.Add($"журнал: {message}"));
    }

    [Fact]
    public async Task ПерваяЛинияЗабираетТрактИСлышна()
    {
        FakeLineMedia media = await AttachAsync("a");

        Assert.True(media.OwnsAudio);
        Assert.False(media.MicrophoneMuted);
        Assert.True(media.Receiving);
        Assert.Equal("a", _controller.ActiveCallId);

        // Повторного INVITE по единственной линии быть не должно: разговор
        // только начался, и сообщать серверу нечего.
        Assert.DoesNotContain(_journal, entry => entry.StartsWith("сеть:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ВтораяЛинияПолучаетТрактПослеТогоКакПерваяЕгоОтпустила()
    {
        FakeLineMedia first = await AttachAsync("a");
        FakeLineMedia second = await AttachAsync("b");

        int released = _journal.IndexOf("a: отпустил тракт");
        int claimed = _journal.IndexOf("b: взял тракт");

        Assert.True(released >= 0 && claimed >= 0);
        Assert.True(released < claimed, "фоновая линия обязана отпустить устройство до того, как активная его возьмёт");

        // И только потом — сеть. Ждать ответа сервера, чтобы отдать звук, значит
        // оставить оператора без обеих линий на время обмена.
        Assert.True(claimed < _journal.IndexOf("сеть: повторный INVITE по a"));

        Assert.False(first.OwnsAudio);
        Assert.True(second.OwnsAudio);
        Assert.True(first.MicrophoneMuted);
        Assert.False(first.Receiving);
    }

    [Fact]
    public async Task ФоноваяЛинияПерестаётПринимать()
    {
        FakeLineMedia first = await AttachAsync("a");
        await AttachAsync("b");

        // Asterisk на удержании не замолкает и продолжает слать поток на полной
        // скорости: глушить приём обязан клиент, иначе оператор слышит двоих.
        Assert.False(first.Receiving);
        Assert.Equal(MediaDirection.SendOnly, first.LastReoffer);
    }

    [Fact]
    public async Task ЧетвёртаяЛинияНеЗаводится()
    {
        await AttachAsync("a");
        await AttachAsync("b");
        await AttachAsync("c");

        Assert.False(_controller.HasFreeLine);
        Assert.False(await _controller.AttachAsync("d", new FakeLineMedia("d", _journal)));
        Assert.Equal(3, _controller.Lines.Count);
    }

    [Fact]
    public async Task ОтказНаУдержаниеОткатываетСостояниеЛинии()
    {
        FakeLineMedia media = await AttachAsync("a");
        _signaling.ReinviteFails.Add("a");

        Assert.False(await _controller.HoldAsync("a", true));

        // Отказ на повторный INVITE разговор не рвёт: он остаётся на прежних
        // параметрах, и показывать удержание, которого нет, нельзя.
        LineView line = Assert.Single(_controller.Lines);
        Assert.False(line.IsHeldByOperator);
        Assert.True(line.IsAudible);
        Assert.True(media.Receiving);
        Assert.False(media.MicrophoneMuted);
    }

    [Fact]
    public async Task СерверноеУдержаниеГлушитЛиниюДажеБезКнопки()
    {
        FakeLineMedia media = await AttachAsync("a");

        // Старая запись chan_sip: направление нетронуто, а поток выключен
        // нулевым адресом. Смотреть на одно направление — значит продолжать
        // отправлять голос оператора в никуда.
        _controller.ApplyRemoteMedia("a", Negotiated("0.0.0.0", MediaDirection.SendRecv));

        Assert.True(media.MicrophoneMuted);
        Assert.False(media.Receiving);
        Assert.True(Assert.Single(_controller.Lines).IsHeldByServer);

        _controller.ApplyRemoteMedia("a", Negotiated("10.0.0.1", MediaDirection.SendRecv));
        Assert.False(media.MicrophoneMuted);
    }

    [Fact]
    public async Task КнопкаМикрофонаНеВозвращаетВРазговорУдержаннуюЛинию()
    {
        FakeLineMedia media = await AttachAsync("a");
        await _controller.HoldAsync("a", true);

        _controller.IsMicrophoneMuted = true;
        _controller.IsMicrophoneMuted = false;

        Assert.True(media.MicrophoneMuted);
        Assert.False(media.Receiving);
    }

    [Fact]
    public async Task ЗавершениеАктивнойЛинииВозвращаетОставшуюсяВРазговор()
    {
        FakeLineMedia first = await AttachAsync("a");
        FakeLineMedia second = await AttachAsync("b");
        _journal.Clear();

        await _controller.DetachAsync("b");

        Assert.Equal("a", _controller.ActiveCallId);
        Assert.True(first.OwnsAudio);
        Assert.True(first.Receiving);
        Assert.Equal(MediaDirection.SendRecv, first.LastReoffer);
        Assert.Contains("сеть: повторный INVITE по a", _journal);
        Assert.False(second.OwnsAudio);
    }

    [Fact]
    public async Task КонференцияУходитПоАктивнойЛинииИПовторБлокируется()
    {
        await AttachAsync("a");
        FakeLineMedia second = await AttachAsync("b");

        Assert.Equal(DtmfOutcome.Sent, await _controller.SendConferenceCodeAsync("*3"));
        Assert.Equal("*3", Assert.Single(second.SentTones).DisplayText);

        // После успешной передачи повтор заблокирован до конца звонка:
        // подтверждать нечем, а вторая отправка кода — это второй набор в линию.
        Assert.Equal(DtmfOutcome.AlreadySent, await _controller.SendConferenceCodeAsync("*3"));
        Assert.Equal(ConferenceState.Sent, _controller.Lines[1].Conference);
    }

    [Fact]
    public async Task НеудачнаяКонференцияСбрасываетСостояние()
    {
        FakeLineMedia media = await AttachAsync("a");
        media.ToneSendingFails = true;

        Assert.Equal(DtmfOutcome.NotSupported, await _controller.SendConferenceCodeAsync("*3"));
        Assert.Equal(ConferenceState.Idle, Assert.Single(_controller.Lines).Conference);
    }

    [Fact]
    public async Task БезTelephoneEventТоныНеУходятМолча()
    {
        FakeLineMedia media = await AttachAsync("a");
        media.SupportsTelephoneEvents = false;

        Assert.Equal(DtmfOutcome.NotSupported, await _controller.SendDtmfAsync(new DtmfSequence("123")));
        Assert.Empty(media.SentTones);
    }

    [Fact]
    public async Task КонсультационныйПереводКладётТрубкуНаОбеихНогах()
    {
        await AttachAsync("a");
        await AttachAsync("consult");

        LineTransferResult result = await _controller.TransferAsync("a", "701", "consult");

        Assert.True(result.Succeeded);
        Assert.Equal("a -> 701 вместо consult", Assert.Single(_signaling.TransferTargets));
        Assert.Equal(["consult", "a"], _signaling.HungUp);

        // Ни одна линия не возвращается в разговор: кончились обе. На стенде
        // возврат выглядел как «удержание не сработало (разговора нет)» и
        // лишний подъём тракта поверх уже завершённого звонка.
        Assert.Empty(_controller.Lines);
        Assert.DoesNotContain(_journal, entry => entry.Contains("возвращаем", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ОтказПереводаОставляетИсходныйРазговор()
    {
        await AttachAsync("a");
        await AttachAsync("consult");
        _signaling.TransferEvents.Clear();
        _signaling.TransferEvents.Add(new SipTransferEvent.Accepted());
        _signaling.TransferEvents.Add(new SipTransferEvent.Failed(486, "Busy Here"));

        LineTransferResult result = await _controller.TransferAsync("a", "701", "consult");

        Assert.False(result.Succeeded);
        Assert.Contains("486", result.Reason, StringComparison.Ordinal);
        Assert.Empty(_signaling.HungUp);
        Assert.Equal(2, _controller.Lines.Count);
    }

    [Fact]
    public async Task ПереводБезКонсультацииУходитБезReplaces()
    {
        await AttachAsync("a");

        Assert.True((await _controller.TransferAsync("a", "700")).Succeeded);
        Assert.Equal("a -> 700", Assert.Single(_signaling.TransferTargets));
        Assert.Equal(["a"], _signaling.HungUp);
    }

    [Fact]
    public async Task ПереводСНеизвестнойКонсультациейНеУходитВовсе()
    {
        await AttachAsync("a");

        LineTransferResult result = await _controller.TransferAsync("a", "701", "неизвестная");

        Assert.False(result.Succeeded);
        Assert.Empty(_signaling.TransferTargets);
    }

    [Fact]
    public async Task ВстречноеПереключениеОтклоняется()
    {
        await AttachAsync("a");
        await AttachAsync("b");

        // Пока первое переключение ждёт ответа сервера, второе обязано уйти ни
        // с чем: два встречных повторных INVITE по одной линии — это 491.
        var gate = new TaskCompletionSource();
        var slow = new SlowSignaling(_signaling, gate.Task);
        var controller = new LineController(slow);
        await controller.AttachAsync("a", new FakeLineMedia("a", _journal));
        await controller.AttachAsync("b", new FakeLineMedia("b", _journal), makeActive: false);

        // Первое переключение встало на удержании линии «a» — ответа сервера
        // нет и не будет, пока проверка его не отпустит.
        Task<bool> first = controller.SwitchToAsync("b");
        bool second = await controller.SwitchToAsync("a");

        Assert.False(second);
        gate.SetResult();
        Assert.True(await first);
        Assert.Equal("b", controller.ActiveCallId);
    }

    private async Task<FakeLineMedia> AttachAsync(string callId)
    {
        var media = new FakeLineMedia(callId, _journal);
        Assert.True(await _controller.AttachAsync(callId, media, callId));
        return media;
    }

    private static NegotiatedMedia Negotiated(string address, MediaDirection direction) =>
        new(AudioCodec.G722, 9, address, 10000, 101, direction, 20, null);

    /// <summary>Сигнализация, у которой повторный INVITE висит, пока его не отпустят.</summary>
    private sealed class SlowSignaling(ILineSignaling inner, Task gate) : ILineSignaling
    {
        public async Task<ReadOnlyMemory<byte>> ReinviteAsync(string callId, ReadOnlyMemory<byte> offer)
        {
            await gate.ConfigureAwait(false);
            return await inner.ReinviteAsync(callId, offer).ConfigureAwait(false);
        }

        public IAsyncEnumerable<SipTransferEvent> Transfer(
            string callId,
            string target,
            SipDialogIdentifier? replacing) => inner.Transfer(callId, target, replacing);

        public Task HangUpAsync(string callId) => inner.HangUpAsync(callId);

        public SipDialogIdentifier? DialogIdentifierOf(string callId) => inner.DialogIdentifierOf(callId);
    }
}
