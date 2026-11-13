using System.Text.Json;

namespace EliteSIP.CallHistory;

/// <summary>Очередь отказавших записей. Принадлежит только потоку истории.</summary>
internal sealed class HistoryWriteJournal
{
    internal sealed record Parameter(string Name, JsonElement Value);
    internal sealed record Entry(DateTimeOffset At, string Sql, Parameter[] Parameters);
    private readonly string _path;
    private readonly List<Entry> _entries = [];
    private bool _dirty;
    private long _serializedBytes;
    private const int MaximumBytes = 32 * 1024 * 1024;
    internal long LostCount { get; private set; }
    internal int Count => _entries.Count;
    internal bool IsDurable => !_dirty && LostCount == 0;
    internal IReadOnlyList<Entry> Entries => _entries;

    internal HistoryWriteJournal(string path, int days)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) { return; }
            if (new FileInfo(path).Length > MaximumBytes) { LostCount++; return; }
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<Entry>(line);
                    if (entry is null || entry.Parameters is null || !Allowed(entry.Sql)) { LostCount++; continue; }
                    if (entry.At < DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650))) { _dirty = true; continue; }
                    if (_entries.Count < 10000) { _entries.Add(entry); _serializedBytes += System.Text.Encoding.UTF8.GetByteCount(line) + 1; }
                    else { LostCount++; break; }
                }
                catch (JsonException) { LostCount++; }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LostCount++; }
    }

    internal bool Add(string sql, (string Name, object? Value)[] parameters)
    {
        if (!Allowed(sql)) { return true; } // VACUUM не является данными звонка.
        if (_entries.Count >= 10000) { LostCount++; return false; }
        var entry = new Entry(DateTimeOffset.UtcNow, sql,
            parameters.Select(value => new Parameter(value.Name, JsonSerializer.SerializeToElement(value.Value))).ToArray());
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
        if (_serializedBytes + bytes.Length > MaximumBytes) { LostCount++; return false; }
        var append = !_dirty;
        _entries.Add(entry);
        _serializedBytes += bytes.Length;
        _dirty = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            HistoryFilePrivacy.Restrict(_path);
            if (append)
            {
                using var file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
                _dirty = false;
            }
            else { Persist(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return IsDurable;
    }

    internal bool Persist()
    {
        if (!_dirty) { return IsDurable; }
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            HistoryFilePrivacy.Restrict(temporary);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var entry in _entries)
                {
                    file.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n"));
                }
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return IsDurable;
    }

    internal void Committed()
    {
        // Сначала убираем файл. Если удаление не удалось, повтор всей пачки
        // безопасен: записи/отметки/удаления истории идемпотентны.
        try { File.Delete(_path); _entries.Clear(); _serializedBytes = 0; _dirty = false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static object? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? (object)integer : value.GetDouble(),
        _ => throw new JsonException("Unsupported history parameter"),
    };

    private static bool Allowed(string sql)
    {
        var statement = sql.Trim().TrimEnd(';');
        return !statement.Contains(';', StringComparison.Ordinal)
            && (statement.StartsWith("INSERT OR REPLACE INTO calls ", StringComparison.Ordinal)
                || statement.StartsWith("UPDATE calls SET ", StringComparison.Ordinal)
                || statement.StartsWith("DELETE FROM calls", StringComparison.Ordinal));
    }
}
