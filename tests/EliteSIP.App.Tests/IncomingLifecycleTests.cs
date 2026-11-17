using System.IO;
using System.Reflection;
using System.Threading.Channels;
using System.Windows.Threading;
using EliteSIP.App.Incoming;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.App.Shell;
using EliteSIP.Audio;
using EliteSIP.CallHistory;
using EliteSIP.Lines;
using EliteSIP.MediaCore;
using EliteSIP.SipCore;
using EliteSIP.SipCore.Udp;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class IncomingLifecycleTests
{
    [Fact]
    public async Task Accepted_call_remains_visible_and_can_end_while_media_line_is_blocked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pending-media-{Guid.NewGuid():N}.db");
        using var history = new CallHistoryStore(new(path));
        using var transport = new SocketSipTransport(new("127.0.0.1", 5060), SipCore.SipTransport.Udp);
        using var agent = new SipUserAgent(new() { Username = "707", Domain = "localhost" }, new("707", "test"), transport);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var lines = new LineController(new SipUserAgentSignaling(agent));
        var record = new CallRecord { CallId = "pending-media", Direction = CallDirection.Incoming, Number = "2929", AnsweredAt = DateTimeOffset.Now.AddSeconds(-5) };
        PhoneService phone = null!;
        PanelViewModel panel = null!;
        Task<bool>? attaching = null;
        CancellationToken lifetime = default;
        try
        {
            WpfHost.Run(() =>
            {
                panel = new PanelViewModel();
                phone = new PhoneService(new AppSettings(), panel, history,
                    new IncomingCallPresenter(_ => { }, () => 0), Dispatcher.CurrentDispatcher, _ => { });
                SetField(phone, "_agent", agent);
                SetField(phone, "_lines", lines);
                var records = (Dictionary<string, CallRecord>)typeof(PhoneService).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(phone)!;
                records.Add(record.CallId, record);
                history.Begin(record);
                lifetime = (CancellationToken)typeof(PhoneService).GetMethod("MediaLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(phone, [record.CallId])!;
            });
            attaching = Task.Run(() => lines.AttachAsync(record.CallId, new TestMedia
            {
                BeforeResume = () => { entered.Set(); release.Wait(); },
            }, record.Number));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            await Task.Run(() => WpfHost.Run(() =>
            {
                Invoke(phone, "SyncLines");
                Assert.True(panel.IsInCall);
                Assert.True(panel.IsCallButtonEnabled);
                Assert.Equal(record.CallId, panel.ActiveLine?.CallId);
                Assert.Equal(record.AnsweredAt, panel.ActiveLine?.ConnectedAt);
                Assert.False(panel.CanHold);
                Assert.False(panel.CanTransfer);
            })).WaitAsync(TimeSpan.FromSeconds(2));
            Task hangup = null!;
            WpfHost.Run(() => hangup = phone.HangUpAsync());
            await hangup.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(lifetime.IsCancellationRequested);
            Task ended = null!;
            WpfHost.Run(() => ended = (Task)typeof(PhoneService).GetMethod("CloseIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(phone, [record.CallId, "ended", null])!);
            await ended.WaitAsync(TimeSpan.FromSeconds(2));
            WpfHost.Run(() => Assert.False(panel.IsInCall));
        }
        finally
        {
            release.Set();
            if (attaching is not null) { await attaching.WaitAsync(TimeSpan.FromSeconds(3)); }
            await lines.DetachAsync(record.CallId);
            if (phone is not null) { WpfHost.Run(phone.Dispose); }
            history.Dispose();
            File.Delete(path);
        }
    }
    [Theory]
    [InlineData("always", false)]
    [InlineData("header", false)]
    [InlineData("list", false)]
    [InlineData("always", true)]
    [InlineData("header", true)]
    [InlineData("list", true)]
    public async Task Автоподъём_закрывает_линию_и_историю_при_завершении(string mode, bool answered)
    {
        var path = Path.Combine(Path.GetTempPath(), $"incoming-{Guid.NewGuid():N}.db");
        using var history = new CallHistoryStore(new(path));
        using var transport = new SocketSipTransport(new("127.0.0.1", 5060), SipCore.SipTransport.Udp);
        using var agent = new SipUserAgent(
            new() { Username = "707", Domain = "localhost" }, new("707", "test"), transport);
        var events = Channel.CreateUnbounded<SipCallEvent>();
        var call = new SipIncomingCall
        {
            CallId = Guid.NewGuid().ToString(),
            CallerNumber = "88003",
            CalledNumber = "707",
            RequestsAutoAnswer = true,
            AsksForAutoAnswer = true,
            Events = events.Reader.ReadAllAsync(),
        };
        var settings = new AppSettings();
        settings.IncomingCall.AutoAnswer = mode;
        settings.IncomingCall.AutoAnswerNumbers = [call.CallerNumber];
        var lines = new LineController(new SipUserAgentSignaling(agent));
        var media = new TestMedia();
        PhoneService phone = null!;
        PanelViewModel panel = null!;
        try
        {
            WpfHost.Run(() =>
            {
                panel = new PanelViewModel();
                phone = new PhoneService(settings, panel, history,
                    new IncomingCallPresenter(_ => { }, () => 0), Dispatcher.CurrentDispatcher, _ => { });
                SetField(phone, "_agent", agent);
                SetField(phone, "_lines", lines);
                SetField(phone, "_bus", new VoiceAudioBus(
                    new WasapiVoiceAudioEngine(new()), configuration => new WasapiVoiceAudioEngine(configuration)));
                Invoke(phone, "Offer", call);
            });

            // No PBX or audio device is opened. Supply the accepted line separately
            // so this checks the application's cleanup after either CANCEL or BYE.
            if (answered)
            {
                Assert.True(await lines.AttachAsync(call.CallId, media, call.DisplayNumber));
                WpfHost.Run(() => Invoke(phone, "MarkAnswered", call.CallId));
            }
            WpfHost.Run(() => Invoke(phone, "SyncLines"));
            var reason = answered ? "собеседник завершил звонок" : "отменён вызывающим";
            await events.Writer.WriteAsync(new SipCallEvent.Ended(reason));
            events.Writer.Complete();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            CallRecord record;
            do
            {
                record = Assert.Single(history.Records(new(settings.Account.ProfileId)));
                var idle = false;
                WpfHost.Run(() => idle = !phone.IsCallActive && !panel.IsInCall);
                if (record.EndedAt is not null && lines.Lines.Count == 0 && idle)
                {
                    break;
                }
                await Task.Delay(10);
            }
            while (DateTime.UtcNow < deadline);

            Assert.Equal(reason, record.EndReason);
            Assert.NotNull(record.EndedAt);
            Assert.Equal(answered, record.AnsweredAt is not null);
            Assert.Empty(lines.Lines);
            WpfHost.Run(() =>
            {
                Assert.False(phone.IsCallActive);
                Assert.False(panel.IsInCall);
            });
            Assert.False(media.OwnsAudio);
        }
        finally
        {
            events.Writer.TryComplete();
            if (phone is not null)
            {
                WpfHost.Run(phone.Dispose);
            }
            history.Dispose();
            File.Delete(path);
        }
    }

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Invoke(object target, string name, params object[] arguments)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private sealed class TestMedia : ILineMedia
    {
        public Action? BeforeResume { get; init; }
        public bool SupportsTelephoneEvents => false;
        public bool OwnsAudio { get; private set; }
        public void SuspendAudio() => OwnsAudio = false;
        public void ResumeAudio() { BeforeResume?.Invoke(); OwnsAudio = true; }
        public void SetMicrophoneMuted(bool muted) { }
        public void SetReceivingAudio(bool receiving) { }
        public ReadOnlyMemory<byte> MakeReoffer(MediaDirection direction) => throw new NotSupportedException();
        public MediaRenegotiation ApplyAnswer(ReadOnlyMemory<byte> answer) => throw new NotSupportedException();
        public RemoteReofferAnswer AnswerReoffer(ReadOnlyMemory<byte> offer) => throw new NotSupportedException();
        public Task<bool> SendDtmfAndWaitAsync(DtmfSequence sequence) => Task.FromResult(false);
        public Task StopAsync() => Task.CompletedTask;
    }
}
