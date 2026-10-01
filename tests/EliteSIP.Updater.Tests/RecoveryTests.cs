using EliteSIP.Updater;

namespace EliteSIP.Updater.Tests;

/// <summary>
/// Аварийный путь: выпуск, который не запускается, лечится следующим без
/// согласия оператора (0.1.67).
/// </summary>
public sealed class RecoveryTests : IDisposable
{
    private static readonly Version Installed = new(0, 1, 67, 0);

    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "EliteSIP.Recovery.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string SeenPath => Path.Combine(_root, "installed-seen");

    [Fact]
    public void Новый_выпуск_запоминается_текущим_временем_и_дальше_не_сдвигается()
    {
        Assert.Equal(Now, Recovery.FirstSeen(Installed, SeenPath, Now));
        Assert.Equal(Now, Recovery.FirstSeen(Installed, SeenPath, Now.AddHours(5)));
    }

    [Fact]
    public void Смена_выпуска_начинает_отсчёт_заново()
    {
        Recovery.FirstSeen(new Version(0, 1, 66, 0), SeenPath, Now);

        Assert.Equal(Now.AddHours(3), Recovery.FirstSeen(Installed, SeenPath, Now.AddHours(3)));
    }

    [Fact]
    public void Время_из_будущего_не_принимается()
    {
        // Иначе переведённые назад часы машины откладывали бы путь навсегда.
        Recovery.FirstSeen(Installed, SeenPath, Now.AddDays(10));

        Assert.Equal(Now, Recovery.FirstSeen(Installed, SeenPath, Now));
    }

    [Fact]
    public void Не_запускавшийся_дольше_часа_выпуск_застрял()
    {
        Assert.True(Recovery.IsStuck(Installed, started: null, Now, Now + Recovery.Grace, appRunning: false));
        Assert.True(Recovery.IsStuck(Installed, new Version(0, 1, 66), Now, Now + Recovery.Grace, appRunning: false));
    }

    [Fact]
    public void Запускавшийся_выпуск_не_застрял()
    {
        Assert.False(Recovery.IsStuck(Installed, new Version(0, 1, 67), Now, Now.AddDays(3), appRunning: false));
    }

    [Fact]
    public void Меньше_часа_после_установки_рано()
    {
        Assert.False(Recovery.IsStuck(Installed, started: null, Now, Now + Recovery.Grace - TimeSpan.FromMinutes(1), appRunning: false));
    }

    [Fact]
    public void Запущенное_приложение_выключает_путь()
    {
        // Без согласия нельзя ставить поверх идущего разговора, даже если
        // отметка о запуске почему-то не записалась.
        Assert.False(Recovery.IsStuck(Installed, started: null, Now, Now.AddDays(3), appRunning: true));
    }

    [Fact]
    public void О_поломке_выпуска_сказано_один_раз()
    {
        var noted = Path.Combine(_root, "recovery-noted");

        Assert.True(Recovery.NoteOnce(Installed, noted));
        Assert.False(Recovery.NoteOnce(Installed, noted));
        Assert.True(Recovery.NoteOnce(new Version(0, 1, 68, 0), noted));
    }

    [Fact]
    public void Отметка_о_запуске_лежит_в_общем_каталоге_а_отсчёт_у_себя()
    {
        Assert.StartsWith(Paths.Shared, Paths.Started, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Paths.TrustedUpdates, Paths.InstalledSeen, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Paths.TrustedUpdates, Paths.RecoveryNoted, StringComparison.OrdinalIgnoreCase);
    }
}
