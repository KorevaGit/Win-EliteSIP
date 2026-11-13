using System.Reflection;
using Microsoft.Data.Sqlite;

namespace EliteSIP.CallHistory.Tests;

public sealed class HistoryRecoveryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("history-recovery-").FullName;
    private string PathName => Path.Combine(_directory, "history.db");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static CallRecord Call(Guid profile) => new() { CallId = Guid.NewGuid().ToString(), Number = "172", Direction = CallDirection.Incoming, ProfileId = profile, DisplayName = "Test caller" };
    private static void Post(CallHistoryStore store, Action action) => typeof(CallHistoryStore).GetMethod("Post", Private)!.Invoke(store, [action]);
    private static void ReadOnly(CallHistoryStore store, bool readOnly)
    {
        Post(store, () =>
        {
            var database = (SqliteConnection)typeof(CallHistoryStore).GetField("_database", Private)!.GetValue(store)!;
            using var command = database.CreateCommand();
            command.CommandText = readOnly ? "PRAGMA query_only=ON;" : "PRAGMA query_only=OFF;";
            command.ExecuteNonQuery();
        });
        store.Flush();
    }

    [Fact]
    public async Task Failed_writes_are_durable_and_replayed_after_restart_in_order()
    {
        var profile = Guid.NewGuid();
        var call = Call(profile);
        using (var store = new CallHistoryStore(new(PathName)))
        {
            ReadOnly(store, true);
            store.Begin(call);
            store.MarkAnswered(call.Id, call.StartedAt.AddSeconds(1));
            store.Finish(call.Id, "normal BYE", CallOutcome.Completed, call.StartedAt.AddSeconds(4));
            Assert.True(await store.FlushAsync());
            Assert.Empty(store.Records(new(profile)));
            Assert.True(File.Exists(PathName + ".pending"));
        }
        using var reopened = new CallHistoryStore(new(PathName));
        var recovered = Assert.Single(reopened.Records(new(profile)));
        Assert.NotNull(recovered.AnsweredAt);
        Assert.Equal("normal BYE", recovered.EndReason);
        Assert.Equal(CallOutcome.Completed, recovered.OutcomeCode);
        Assert.Equal(TimeSpan.FromSeconds(3), recovered.Duration);
        Assert.False(File.Exists(PathName + ".pending"));
    }

    [Fact]
    public async Task Failed_profile_delete_is_replayed_without_resurrecting_calls()
    {
        var first = Guid.NewGuid();
        var other = Guid.NewGuid();
        using (var store = new CallHistoryStore(new(PathName)))
        {
            ReadOnly(store, true);
            store.Begin(Call(first));
            store.Begin(Call(other));
            store.DeleteHistory(first);
            Assert.True(await store.FlushAsync());
        }
        using var reopened = new CallHistoryStore(new(PathName));
        Assert.Empty(reopened.Records(new(first)));
        Assert.Single(reopened.Records(new(other)));
    }

    [Fact]
    public async Task Blocked_queue_has_independent_durable_snapshot_before_acceptance()
    {
        var profile = Guid.NewGuid();
        var call = Call(profile);
        using (var store = new CallHistoryStore(new(PathName)))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            Post(store, () => { entered.Set(); release.Wait(); });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            store.Begin(call);
            call.AnsweredAt = call.StartedAt.AddSeconds(1);
            try
            {
                Assert.True(await store.EnsureDurableAsync(call));
                Assert.True(File.Exists(Path.Combine(PathName + ".recovery", call.Id.ToString("N") + ".json")));
                using var observer = new SqliteConnection($"Data Source={PathName};Mode=ReadOnly;Pooling=False");
                observer.Open();
                using var query = observer.CreateCommand();
                query.CommandText = "SELECT count(*) FROM calls";
                Assert.Equal(0L, query.ExecuteScalar());
            }
            finally { release.Set(); }
            store.Flush();
        }
        using var reopened = new CallHistoryStore(new(PathName));
        var recovered = Assert.Single(reopened.Records(new(profile)));
        Assert.NotNull(recovered.AnsweredAt);
        Assert.Equal(CallHistoryStore.InterruptedReason, recovered.EndReason);
        Assert.Empty(Directory.EnumerateFiles(PathName + ".recovery", "*.json"));
    }

    [Fact]
    public async Task Unwritable_database_and_recovery_report_unconfirmed_instead_of_crashing()
    {
        var parent = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(parent, "blocked");
        using var store = new CallHistoryStore(new(Path.Combine(parent, "history.db")));
        var record = Call(Guid.NewGuid());
        store.Begin(record);
        Assert.False(await store.EnsureDurableAsync(record));
        Assert.True(store.WriteFailureCount > 0);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
