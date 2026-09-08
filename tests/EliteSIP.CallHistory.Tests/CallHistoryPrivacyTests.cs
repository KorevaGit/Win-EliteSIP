using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using EliteSIP.CallHistory;

namespace EliteSIP.CallHistory.Tests;

/// <summary>
/// Права на файлы истории.
///
/// В базе лежат номера лидов — те самые персональные данные, ради которых у
/// истории вообще есть срок хранения и отдельная политика удаления. В оригинале
/// файл закрывался правами <c>0600</c>; здесь проверяется их windows-аналог:
/// наследование снято, в списке доступа — только владелец.
///
/// Не на Windows проверка не делает ничего: списков доступа там нет, а
/// подделывать их нечем.
/// </summary>
public sealed class CallHistoryPrivacyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "elitesip-history-perm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void База_и_её_спутники_закрыты_от_всех_кроме_владельца()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CallHistoryStore.Settings settings = new(Path.Combine(_directory, "call-history.sqlite"));

        using CallHistoryStore store = new(settings);
        Assert.IsType<HistoryOpenOutcome.Ready>(store.OpenOutcome);
        store.Flush();

        AssertOwnerOnly(settings.FilePath);

        // `-wal` и `-shm` заводит SQLite первой же транзакцией, и данные в них те
        // же самые. Если их нет — база закрыта начисто, и проверять нечего.
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string path = settings.FilePath + suffix;
            if (File.Exists(path))
            {
                AssertOwnerOnly(path);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnly(string path)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        FileSecurity security = new FileInfo(path).GetAccessControl();

        Assert.True(
            security.AreAccessRulesProtected,
            $"наследование прав каталога снято: {path}");

        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));

        Assert.All(
            rules.Cast<FileSystemAccessRule>(),
            rule => Assert.Equal(identity.User, rule.IdentityReference));
    }
}
