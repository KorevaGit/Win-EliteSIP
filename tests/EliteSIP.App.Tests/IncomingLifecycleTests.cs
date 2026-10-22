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

public sealed class IncomingLifecycleTests
{
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
        public bool SupportsTelephoneEvents => false;
        public bool OwnsAudio { get; private set; }
        public void SuspendAudio() => OwnsAudio = false;
        public void ResumeAudio() => OwnsAudio = true;
        public void SetMicrophoneMuted(bool muted) { }
        public void SetReceivingAudio(bool receiving) { }
        public ReadOnlyMemory<byte> MakeReoffer(MediaDirection direction) => throw new NotSupportedException();
        public MediaRenegotiation ApplyAnswer(ReadOnlyMemory<byte> answer) => throw new NotSupportedException();
        public RemoteReofferAnswer AnswerReoffer(ReadOnlyMemory<byte> offer) => throw new NotSupportedException();
        public Task<bool> SendDtmfAndWaitAsync(DtmfSequence sequence) => Task.FromResult(false);
        public Task StopAsync() => Task.CompletedTask;
    }
}
