using System.Reflection;
using EliteSIP.App.Shell;

namespace EliteSIP.App.Tests;

/// <summary>
/// Какое непойманное исключение интерфейса софтфон переживает (0.1.67).
/// </summary>
public sealed class CrashGuardTests
{
    private long _now;

    private CrashGuard Guard() => new(() => _now);

    [Fact]
    public void Обычное_исключение_команды_переживается()
    {
        Assert.True(Guard().ShouldSurvive(new InvalidOperationException("канал закрыт")));
    }

    [Fact]
    public void Испорченный_процесс_не_переживается()
    {
        var guard = Guard();

        // Исключения среды выполнения создаются здесь руками намеренно:
        // проверяется именно реакция на них, бросать их никто не собирается.
#pragma warning disable CA2201
        Assert.False(guard.ShouldSurvive(new OutOfMemoryException()));
        Assert.False(guard.ShouldSurvive(new TypeInitializationException("X", null)));
        Assert.False(guard.ShouldSurvive(new TargetInvocationException(new AccessViolationException())));
#pragma warning restore CA2201
    }

    [Fact]
    public void Серия_в_минуту_роняет_процесс()
    {
        var guard = Guard();

        for (int i = 0; i < CrashGuard.BurstLimit; i++)
        {
            _now += 1000;
            Assert.True(guard.ShouldSurvive(new InvalidOperationException("снова")));
        }

        _now += 1000;
        Assert.False(guard.ShouldSurvive(new InvalidOperationException("снова")));
    }

    [Fact]
    public void Редкие_исключения_в_серию_не_складываются()
    {
        var guard = Guard();

        for (int i = 0; i < CrashGuard.BurstLimit * 3; i++)
        {
            _now += (long)CrashGuard.BurstWindow.TotalMilliseconds / 2;
            Assert.True(guard.ShouldSurvive(new InvalidOperationException("снова")));
        }
    }
}
