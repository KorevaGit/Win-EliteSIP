using System.IO;
using System.Reflection;
using System.Text.Json;
using EliteSIP.App.PanelLine;
using EliteSIP.CallHistory;
using Microsoft.Data.Sqlite;

namespace EliteSIP.App.Tests;

public sealed class MachineResetTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("elitesip-reset-test-").FullName;

    [Fact]
    public void Сброс_убирает_резервные_копии_и_история_не_возвращается_после_открытия()
    {
        var path = Path.Combine(_directory, "history.db");
        var record = new CallRecord { ProfileId = Guid.NewGuid(), CallId = "test-call", Number = "172", DisplayName = "Test", Direction = CallDirection.Incoming };
        using (var store = new CallHistoryStore(new(path)))
        {
            var connection = (SqliteConnection)typeof(CallHistoryStore).GetField("_database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA query_only=ON;"; command.ExecuteNonQuery(); }
            store.Begin(record);
            store.MarkAnswered(record.Id);
            store.Flush();
            Assert.True(File.Exists(path + ".pending"));
        }
        var recovery = Directory.CreateDirectory(path + ".recovery").FullName;
        File.WriteAllText(Path.Combine(recovery, record.Id.ToString("N") + ".json"), JsonSerializer.Serialize(record));
        File.WriteAllText(Path.Combine(recovery, "partial.json.tmp"), "incomplete");
        File.WriteAllText(path + ".pending.tmp", "incomplete");
        var unrelated = Path.Combine(_directory, "keep.txt");
        File.WriteAllText(unrelated, "keep");

        Assert.Empty(MachineReset.Wipe(_directory));
        Assert.False(File.Exists(path + ".pending"));
        Assert.False(File.Exists(path + ".pending.tmp"));
        Assert.False(Directory.Exists(recovery));
        Assert.True(File.Exists(unrelated));
        using var reopened = new CallHistoryStore(new(path));
        Assert.Empty(reopened.Records(new(record.ProfileId)));
    }

    [Fact]
    public void Занятый_снимок_попадает_в_отчёт_и_не_мешает_удалению_остального()
    {
        var recovery = Directory.CreateDirectory(Path.Combine(_directory, "history.db.recovery")).FullName;
        var locked = Path.Combine(recovery, "locked.json");
        using var file = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var pending = Path.Combine(_directory, "history.db.pending");
        File.WriteAllText(pending, "pending");
        var failures = MachineReset.Wipe(_directory);
        Assert.Contains("history.db.recovery/locked.json", failures);
        Assert.False(File.Exists(pending));
        Assert.True(File.Exists(locked));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
