using System.IO;
using System.Reflection;
using System.Windows.Shell;
using EliteSIP.App.Incoming;
using EliteSIP.App.Settings;
using EliteSIP.CallGuard;

namespace EliteSIP.App.Tests;

public sealed class IncomingPlacementTests
{
    private static readonly IncomingCallSubject Subject = IncomingCallSubject.Classify(
        "88003", "Очередь", requestsAutoAnswer: true, ownNumber: "707", masksMobileNumbers: false);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Фиксированная_позиция_переживает_новый_вызов_и_перезапуск(bool guarded, bool randomized)
        => WpfHost.Run(() =>
        {
            var file = Path.Combine(Path.GetTempPath(), $"incoming-position-{Guid.NewGuid():N}.json");
            try
            {
                var settings = new AppSettings();
                var policy = Policy(guarded, randomized);
                using (var presenter = Presenter(settings))
                {
                    presenter.Show(Subject, policy, () => Assert.Fail("Перенос не принимает вызов"), () => Assert.Fail("Перенос не отклоняет вызов"));
                    var window = WindowOf(presenter);
                    Assert.Equal(28, WindowChrome.GetWindowChrome(window).CaptionHeight);
                    var placement = PlacementOf(presenter);
                    var target = new ScreenPoint(placement.Bounds.MinX + 20, placement.Bounds.MinY + 20);
                    Move(window, target);
                    var moved = window.PhysicalFrame.Origin;
                    Assert.Equal(moved, settings.IncomingPlacement.Origin);

                    presenter.Hide();
                    presenter.Show(Subject, policy, () => { }, () => { });
                    Assert.Equal(moved, WindowOf(presenter).PhysicalFrame.Origin);
                    settings.Save(file);
                }

                var restored = AppSettings.Load(file);
                Assert.Equal(settings.IncomingPlacement.Origin, restored.IncomingPlacement.Origin);
                using var restarted = Presenter(restored);
                restarted.Show(Subject, policy, () => { }, () => { });
                Assert.Equal(settings.IncomingPlacement.Origin, WindowOf(restarted).PhysicalFrame.Origin);
            }
            finally
            {
                File.Delete(file);
            }
        });

    [Fact]
    public void Случайный_показ_и_его_перенос_не_переписывают_фиксированное_место()
        => WpfHost.Run(() =>
        {
            var settings = new AppSettings();
            var fixedOrigin = new ScreenPoint(123, 456);
            settings.IncomingPlacement.Origin = fixedOrigin;
            var restores = 0;
            var saves = 0;
            using var presenter = new IncomingCallPresenter(_ => { }, () => 0)
            {
                RestorePlacement = () => { restores++; return settings.IncomingPlacement.Origin; },
                SavePlacement = origin => { saves++; settings.IncomingPlacement.Origin = origin; },
            };
            var policy = Policy(guarded: true, randomized: true);
            presenter.Show(Subject, policy, () => { }, () => { });
            var window = WindowOf(presenter);
            var placement = PlacementOf(presenter);
            Move(window, new ScreenPoint(placement.Bounds.MinX + 20, placement.Bounds.MinY + 20));
            Assert.Equal(window.PhysicalFrame.Origin, Field<ScreenPoint?>(presenter, "_lastOrigin"));
            Assert.Equal(fixedOrigin, settings.IncomingPlacement.Origin);
            Assert.Equal(0, restores);
            Assert.Equal(0, saves);

            presenter.Hide();
            presenter.Show(Subject, policy, () => { }, () => { });
            Assert.Equal(0, restores);
            Assert.True(PlacementOf(presenter).Bounds.Contains(WindowOf(presenter).PhysicalFrame));
        });

    [Fact]
    public void Программное_размещение_и_закрытие_не_сохраняют_случайные_координаты()
        => WpfHost.Run(() =>
        {
            var saves = 0;
            using var presenter = new IncomingCallPresenter(_ => { }, () => 0) { SavePlacement = _ => saves++ };
            presenter.Show(Subject, Policy(guarded: true, randomized: false), () => { }, () => { });
            var window = WindowOf(presenter);
            var frame = window.PhysicalFrame;
            window.PlaceAt(new ScreenPoint(frame.X + 10, frame.Y + 10));
            presenter.Hide();
            Assert.Equal(0, saves);
        });

    [Fact]
    public void Сохранённое_место_исчезнувшего_монитора_возвращается_в_видимую_область()
        => WpfHost.Run(() =>
        {
            var settings = new AppSettings();
            settings.IncomingPlacement.Origin = new ScreenPoint(1000000, -1000000);
            using var presenter = Presenter(settings);
            presenter.Show(Subject, Policy(guarded: true, randomized: false), () => { }, () => { });
            var bounds = PlacementOf(presenter).Bounds;
            var frame = WindowOf(presenter).PhysicalFrame;
            Assert.True(bounds.Contains(frame), $"bounds={bounds}; frame={frame}");
        });

    [Fact]
    public void Окончание_вызова_во_время_переноса_не_сохраняет_закрытое_окно()
        => WpfHost.Run(() =>
        {
            var saves = 0;
            using var presenter = new IncomingCallPresenter(_ => { }, () => 0) { SavePlacement = _ => saves++ };
            presenter.Show(Subject, Policy(guarded: true, randomized: false), () => { }, () => { });
            var window = WindowOf(presenter);
            Message(window, 0x0231);
            Assert.True(window.IsUserMoving);
            presenter.Hide();
            Message(window, 0x0232);
            Assert.False(presenter.IsVisible);
            Assert.Equal(0, saves);
        });

    [Fact]
    public void Старые_настройки_не_требуют_сохранённой_позиции()
    {
        var file = Path.Combine(Path.GetTempPath(), $"incoming-old-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, "{}");
            Assert.Null(AppSettings.Load(file).IncomingPlacement.Origin);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static IncomingCallPresenter Presenter(AppSettings settings) => new(_ => { }, () => 0)
    {
        RestorePlacement = () => settings.IncomingPlacement.Origin,
        SavePlacement = origin => settings.IncomingPlacement.Origin = origin,
    };

    private static CallGuardPolicy Policy(bool guarded, bool randomized) => new()
    {
        IsEnabled = guarded,
        IsRandomPositionEnabled = randomized,
        TunesRandomnessByHand = true,
        ScreenMargin = 10,
        MinimumTravel = 50,
    };

    private static IncomingCallWindow WindowOf(IncomingCallPresenter presenter) => Field<IncomingCallWindow>(presenter, "_window");
    private static IncomingCallPlacement PlacementOf(IncomingCallPresenter presenter) => Field<IncomingCallPlacement>(presenter, "_placement");
    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Move(IncomingCallWindow window, ScreenPoint origin)
    {
        Message(window, 0x0231);
        window.PlaceAt(origin);
        Message(window, 0x0232);
        Assert.False(window.IsUserMoving);
        Assert.Equal((nint)3, Message(window, 0x0021));
    }

    private static nint Message(IncomingCallWindow window, int message)
        => (nint)typeof(IncomingCallWindow).GetMethod("OnWindowMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [(nint)0, message, (nint)0, (nint)0, false])!;
}
