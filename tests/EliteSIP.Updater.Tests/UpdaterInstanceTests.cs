using System.Security.AccessControl;
using System.Security.Principal;
using EliteSIP.Updater;

namespace EliteSIP.Updater.Tests;

/// <summary>
/// Оператор не должен отключать обновления, заняв мьютекс обновляльщика
/// раньше него (0.1.67).
/// </summary>
public sealed class UpdaterInstanceTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"updater-{Guid.NewGuid():N}.log");

    /// <summary>Своё имя на каждый прогон: настоящий обновляльщик на машине не мешает.</summary>
    private readonly string _name = $@"Local\EliteSIP.Updater.Tests.{Guid.NewGuid():N}";

    public void Dispose()
    {
        File.Delete(_logPath);
    }

    [Fact]
    public void Свободный_мьютекс_занимается()
    {
        Assert.True(UpdaterInstance.TryClaim(new UpdaterLog(_logPath), out var mutex, _name));
        Assert.NotNull(mutex);
        mutex.Dispose();
    }

    [Fact]
    public void Мьютекс_созданный_администратором_считается_настоящим()
    {
        using var genuine = CreateOwnedBy(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (genuine is null)
        {
            // Без прав администратора назначить владельцем группу нельзя.
            return;
        }

        Assert.True(UpdaterInstance.CreatedByService(genuine));
        Assert.False(UpdaterInstance.TryClaim(new UpdaterLog(_logPath), out _, _name));
    }

    [Fact]
    public void Мьютекс_созданный_пользователем_не_останавливает_обновляльщик()
    {
        // Владелец назначается явно: у процесса с правами администратора
        // владельцем по умолчанию стала бы группа администраторов, и проверка
        // ничего бы не проверяла.
        using var identity = WindowsIdentity.GetCurrent();
        using var squatter = CreateOwnedBy(identity.User!)!;

        Assert.False(UpdaterInstance.CreatedByService(squatter));

        // Из другого потока: свой поток вошёл бы в мьютекс повторно и счёл его своим.
        bool proceeds = false;
        Mutex? claimed = null;
        var other = new Thread(() => proceeds = UpdaterInstance.TryClaim(new UpdaterLog(_logPath), out claimed, _name));
        squatter.WaitOne();
        try
        {
            other.Start();
            other.Join();
        }
        finally
        {
            squatter.ReleaseMutex();
        }

        Assert.True(proceeds);
        Assert.Null(claimed);
        Assert.Contains("создан не SYSTEM", File.ReadAllText(_logPath), StringComparison.Ordinal);
    }

    /// <summary>Мьютекс с заданным владельцем. <c>null</c> — назначить такого владельца нельзя.</summary>
    private Mutex? CreateOwnedBy(SecurityIdentifier owner)
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        security.SetOwner(owner);

        try
        {
            return MutexAcl.Create(initiallyOwned: false, _name, out _, security);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
