using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using EliteSIP.Audio;
using EliteSIP.CallHistory;
using Microsoft.Data.Sqlite;

internal static class IncidentProbes
{
    internal static async Task RunAsync()
    {
        Console.WriteLine("Incident probes: isolated child process and temporary history only.");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        }
        start.ArgumentList.Add("--audio-thread-child");
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine($"Audio worker child exit: 0x{child.ExitCode:X8}");
        Console.Write(await output);
        var stderr = await errors;
        Console.WriteLine(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        if (child.ExitCode != 0) { throw new InvalidOperationException("Guarded audio worker terminated its process"); }

        var directory = Path.GetFullPath("artifacts/backend-audit/incidents");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"history-{Guid.NewGuid():N}.db");
        using var store = new CallHistoryStore(new CallHistoryStore.Settings(path));
        var database = (SqliteConnection)typeof(CallHistoryStore).GetField("_database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using (var command = database.CreateCommand()) { command.CommandText = "PRAGMA query_only=ON;"; command.ExecuteNonQuery(); }
        var missing = Record();
        store.Begin(missing);
        store.MarkAnswered(missing.Id);
        store.Finish(missing.Id, "normal BYE");
        store.Flush();
        Console.WriteLine($"History write failure: Begin/Answered/Finish returned; rows={store.Records(new HistoryScope(missing.ProfileId)).Count}; open outcome={store.OpenOutcome}");
        using (var command = database.CreateCommand()) { command.CommandText = "PRAGMA query_only=OFF;"; command.ExecuteNonQuery(); }
        var control = Record();
        store.Begin(control);
        store.MarkAnswered(control.Id);
        store.Finish(control.Id, "normal BYE");
        store.Flush();
        Console.WriteLine($"History control with writes restored: rows={store.Records(new HistoryScope(control.ProfileId)).Count}");
        var recovered = store.Records(new HistoryScope(missing.ProfileId));
        Console.WriteLine($"Original failed history write recovered: rows={recovered.Count}; answered={recovered.Count > 0 && recovered[0].AnsweredAt is not null}");
        if (recovered.Count != 1 || recovered[0].AnsweredAt is null || recovered[0].EndedAt is null) { throw new InvalidOperationException("History replay lost call state"); }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var post = typeof(CallHistoryStore).GetMethod("Post", BindingFlags.Instance | BindingFlags.NonPublic)!;
        post.Invoke(store, [(Action)(() => { entered.Set(); release.Wait(); })]);
        if (!entered.Wait(TimeSpan.FromSeconds(3))) { throw new InvalidOperationException("History gate not entered"); }
        var queued = Record();
        store.Begin(queued);
        store.MarkAnswered(queued.Id);
        try
        {
            queued.AnsweredAt = DateTimeOffset.Now;
            var confirmed = await store.EnsureDurableAsync(queued);
            Console.WriteLine($"Accepted-call durability with stalled queue: confirmed={confirmed}; recoverySnapshot={File.Exists(Path.Combine(path + ".recovery", queued.Id.ToString("N") + ".json"))}");
            if (!confirmed) { throw new InvalidOperationException("Accepted call is not durable"); }
            using var observer = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            observer.Open();
            using var query = observer.CreateCommand();
            query.CommandText = "SELECT count(*) FROM calls WHERE id=$id";
            query.Parameters.AddWithValue("$id", queued.Id.ToString());
            Console.WriteLine($"Primary SQLite still queued (recovery snapshot is separate): committed rows={query.ExecuteScalar()}");
        }
        finally { release.Set(); }
        store.Flush();
        Console.WriteLine($"History after queue drain: rows={store.Records(new HistoryScope(queued.ProfileId)).Count}");

        var batchProfile = Guid.NewGuid();
        var batchClock = Stopwatch.StartNew();
        for (var index = 0; index < 1000; index++)
        {
            var record = new CallRecord { CallId = Guid.NewGuid().ToString(), ProfileId = batchProfile,
                Direction = CallDirection.Incoming, Number = "queue-load-test", SipLogin = "724-test", WasDistribution = true };
            store.Begin(record);
            store.MarkAnswered(record.Id);
            store.Finish(record.Id, "normal BYE");
        }
        store.Flush();
        var saved = store.Records(new HistoryScope(batchProfile), limit: 1500).Count;
        Console.WriteLine($"History healthy load: 1000 accepted queue calls; saved={saved}; elapsed={batchClock.Elapsed.TotalMilliseconds:0} ms");
        if (saved != 1000) { throw new InvalidOperationException("History load lost rows"); }
    }

    private static CallRecord Record() => new()
    {
        CallId = Guid.NewGuid().ToString(), ProfileId = Guid.NewGuid(), Direction = CallDirection.Incoming,
        Number = "queue-test", SipLogin = "176-test", WasDistribution = true,
    };

    internal static void CrashWorkerChild()
    {
        _ = SetErrorMode(0x0002); // suppress Windows crash dialog for this disposable probe only
        AppDomain.CurrentDomain.UnhandledException += (_, value) => Console.WriteLine($"Unhandled worker exception; IsTerminating={value.IsTerminating}");
        var startThread = typeof(WasapiVoiceAudioEngine).GetMethod("StartThread", BindingFlags.Static | BindingFlags.NonPublic)!;
#pragma warning disable CA2201 // Intentional fault injection in a disposable child, never a production throw.
        startThread.Invoke(null, ["audit-audio-worker", (Action)(() => throw new COMException("Injected AUDCLNT_E_DEVICE_INVALIDATED before loop catch", unchecked((int)0x88890004))), (Action<Exception>)(error => Console.WriteLine($"Worker failure captured: {error.GetType().Name}"))]);
#pragma warning restore CA2201
        Thread.Sleep(2000);
        Console.WriteLine("Child survived guarded audio worker failure");
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SetErrorMode(uint mode);
}
