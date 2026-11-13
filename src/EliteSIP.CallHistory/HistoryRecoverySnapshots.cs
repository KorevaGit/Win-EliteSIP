using System.Text.Json;

namespace EliteSIP.CallHistory;

/// <summary>Независимая запись начала/ответа, если основная очередь не отвечает.</summary>
internal sealed class HistoryRecoverySnapshots(string databasePath, int days) : IDisposable
{
    private readonly string _directory = databasePath + ".recovery";
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Lock _gate = new();
    private int _users;
    private bool _disposed;

    internal async Task<bool> SaveAsync(CallRecord record, CancellationToken cancellationToken)
    {
        lock (_gate) { if (_disposed) { return false; } _users++; }
        try { if (!await _writing.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false)) { EndUser(); return false; } }
        catch (OperationCanceledException) { EndUser(); return false; }
        var write = Task.Run(() =>
        {
            var path = Path.Combine(_directory, record.Id.ToString("N") + ".json");
            var temporary = path + ".tmp";
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_directory);
                HistoryFilePrivacy.Restrict(temporary);
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, record);
                    file.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
            catch (OperationCanceledException) { return false; }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                _writing.Release();
                EndUser();
            }
        });
        try { return await write.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { return false; }
    }

    internal IReadOnlyList<CallRecord> Load()
    {
        List<CallRecord> records = [];
        try
        {
            if (!Directory.Exists(_directory)) { return records; }
            foreach (var temporary in Directory.EnumerateFiles(_directory, "*.tmp").Take(10000))
            {
                if (File.GetLastWriteTimeUtc(temporary) < DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 3650))) { File.Delete(temporary); }
            }
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Take(10000))
            {
                try
                {
                    if (new FileInfo(path).Length > 1024 * 1024) { continue; }
                    var record = JsonSerializer.Deserialize<CallRecord>(File.ReadAllText(path));
                    if (record is null || Path.GetFileNameWithoutExtension(path) != record.Id.ToString("N")) { continue; }
                    if (record.StartedAt < DateTimeOffset.Now.AddDays(-Math.Clamp(days, 1, 3650))) { Complete(record.Id); continue; }
                    records.Add(record);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return records;
    }

    internal void Complete(Guid id)
    {
        File.Delete(Path.Combine(_directory, id.ToString("N") + ".json"));
    }

    internal void Delete(Guid? profile)
    {
        foreach (var record in Load()) { if (profile is null || record.ProfileId == profile) { Complete(record.Id); } }
    }

    private void EndUser()
    {
        lock (_gate) { if (--_users == 0 && _disposed) { _writing.Dispose(); } }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            if (_users == 0) { _writing.Dispose(); }
        }
    }
}
