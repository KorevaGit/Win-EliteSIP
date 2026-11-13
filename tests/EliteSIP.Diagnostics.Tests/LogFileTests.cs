using System.IO.Compression;

namespace EliteSIP.Diagnostics.Tests;

/// <summary>
/// Файл журнала: запись, ротация, уборка.
///
/// Проверяется то, что на разработке не всплывает никогда: рабочее место живёт
/// годами, и журнал без потолка однажды займёт диск целиком, а сломанная
/// дозапись потеряет ровно те строки, ради которых журнал заводили.
///
/// Перенесено из <c>Packages/Diagnostics/Tests/DiagnosticsTests/LogFileTests.swift</c>.
/// Отличие одно и вынужденное: каждый <see cref="LogFile"/> здесь закрывается
/// явно. На macOS каталог удалялся из-под открытого дескриптора, на Windows
/// открытый файл не даёт удалить ни себя, ни каталог.
/// </summary>
public sealed class LogFileTests : IDisposable
{
    private static void Queue(LogFile log, Action action)
    {
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var job = typeof(LogFile).GetNestedType("Job", System.Reflection.BindingFlags.NonPublic)!;
        typeof(LogFile).GetMethod("Enqueue", Flags)!.Invoke(log,
            [Activator.CreateInstance(job, [null, action, null])]);
    }

    [Fact]
    public async Task Dispose_drains_pending_writes_and_releases_file()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Queue(log, () => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        log.Write("last-before-exit", "info");
        var stopping = Task.Run(log.Dispose);
        try
        {
            Assert.NotSame(stopping, await Task.WhenAny(stopping, Task.Delay(150)));
        }
        finally { release.Set(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        using var exclusive = new FileStream(log.CurrentFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(exclusive);
        Assert.Contains("last-before-exit", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        log.Write("after-exit", "info");
        Assert.False(log.Flush(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Dispose_timeout_leaves_file_owned_by_worker_until_it_finishes()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        log.Flush();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var drained = new ManualResetEventSlim();
        Queue(log, () => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        log.Write("after-slow-disk", "info");
        Queue(log, drained.Set);
        try
        {
            await Task.Run(log.Dispose).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Throws<IOException>(() =>
            {
                using var exclusive = new FileStream(log.CurrentFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
        }
        finally { release.Set(); }
        Assert.True(drained.Wait(TimeSpan.FromSeconds(3)));
        // A second Dispose waits for the same worker's finally, without closing its file.
        log.Dispose();
        using var file = new FileStream(log.CurrentFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(file);
        Assert.Contains("after-slow-disk", await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Flush_is_bounded_when_disk_queue_is_stalled()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Queue(log, () => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try { Assert.False(log.Flush(TimeSpan.FromMilliseconds(50))); }
        finally { release.Set(); }
        Assert.True(log.Flush(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Disk_operation_error_does_not_kill_log_queue()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        Queue(log, () => throw new IOException("injected disk error"));
        log.Write("after-disk-error", "info");
        Assert.True(log.Flush(TimeSpan.FromSeconds(3)));
        Assert.Contains("after-disk-error", Read(log.CurrentFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_worker_does_not_leave_flush_waiting_or_file_open()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        log.Flush();
        Queue(log, () => throw new NotSupportedException("injected unexpected worker fault"));
        Assert.False(await Task.Run(() => log.Flush(TimeSpan.FromSeconds(10))).WaitAsync(TimeSpan.FromSeconds(3)));
        log.Dispose();
        using var file = new FileStream(log.CurrentFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Support_archive_reports_unconfirmed_flush_instead_of_waiting_forever()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        log.Write("already-written", "info");
        log.Flush();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Queue(log, () => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        var destination = Path.Combine(_directory, "stalled.zip");
        try
        {
            await Task.Run(() => SupportArchive.MakeFromDirectory(_directory, "probe", destination, log))
                .WaitAsync(TimeSpan.FromSeconds(8));
            using var archive = ZipFile.OpenRead(destination);
            using var reader = new StreamReader(archive.GetEntry("collection.txt")!.Open());
            Assert.Contains("последние строки могут отсутствовать", await reader.ReadToEndAsync(), StringComparison.Ordinal);
            Assert.NotNull(archive.GetEntry(LogFile.CurrentFileName));
        }
        finally { release.Set(); }
        log.Flush();
    }

    private readonly string _directory;

    public LogFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"elitesip-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Каталог во временной папке; не убрался — уберёт система.
        }
    }

    /// <summary>Читает файл, который прямо сейчас открыт журналом на запись.</summary>
    private static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Строка_доезжает_до_файла_и_остаётся_замаскированной()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        log.Write("REGISTER response=\"deadbeef\"", "debug");
        log.Flush();

        string text = Read(log.CurrentFilePath);

        Assert.Contains("[debug]", text, StringComparison.Ordinal);

        // Маскирование обязано работать на пути в файл, а не рядом с ним.
        Assert.DoesNotContain("deadbeef", text, StringComparison.Ordinal);
        Assert.Contains("скрыто", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Перезапуск_дописывает_а_не_начинает_сначала()
    {
        using (var first = new LogFile(new LogFile.Settings(_directory)))
        {
            first.Write("первая сессия", "info");
            first.Flush();
        }

        using var second = new LogFile(new LogFile.Settings(_directory));
        second.Write("вторая сессия", "info");
        second.Flush();

        string text = Read(second.CurrentFilePath);

        // Перезапуск не повод терять журнал.
        Assert.Contains("первая сессия", text, StringComparison.Ordinal);
        Assert.Contains("вторая сессия", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Смена настроек журнала пересоздаёт <see cref="LogFile"/>, и короткое
    /// время два экземпляра держат один файл: у старого может остаться строка в
    /// очереди. Со своими смещениями они затирали бы записи друг друга — отсюда
    /// открытие только на дозапись. Проверяется буквально это: ни одна строка не
    /// пропала.
    /// </summary>
    [Fact]
    public void Два_экземпляра_на_одном_файле_не_затирают_друг_друга()
    {
        using var old = new LogFile(new LogFile.Settings(_directory));
        using var fresh = new LogFile(new LogFile.Settings(_directory));

        for (int index = 0; index < 50; index++)
        {
            old.Write($"старый {index}", "info");
            fresh.Write($"новый {index}", "info");
        }

        old.Flush();
        fresh.Flush();

        string[] lines = Read(fresh.CurrentFilePath)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(100, lines.Length);
        Assert.Equal(50, lines.Count(line => line.Contains("старый ", StringComparison.Ordinal)));
        Assert.Equal(50, lines.Count(line => line.Contains("новый ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Файл_не_растёт_бесконечно()
    {
        using var log = new LogFile(new LogFile.Settings(_directory, maximumFileBytes: 2_000, keptFiles: 5));
        for (int index = 0; index < 200; index++)
        {
            log.Write(string.Concat(Enumerable.Repeat($"строка {index} ", 4)), "info");
        }

        log.Flush();

        long current = new FileInfo(log.CurrentFilePath).Length;
        Assert.True(current < 2_000 * 2, $"текущий файл обязан оставаться в пределах потолка, а он {current}");

        IReadOnlyList<string> files = log.Files();
        Assert.True(files.Count > 1, "отложенных файлов не появилось — ротации не было");
        Assert.Equal(log.CurrentFilePath, files[0]);
    }

    [Fact]
    public void Отложенных_файлов_не_больше_чем_разрешено()
    {
        const int Kept = 2;

        using var log = new LogFile(new LogFile.Settings(_directory, maximumFileBytes: 500, keptFiles: Kept));
        for (int index = 0; index < 400; index++)
        {
            log.Write($"наполнение {index} {new string('x', 40)}", "info");
        }

        log.Flush();

        List<string> rotated = log.Files()
            .Where(path => Path.GetFileName(path) != LogFile.CurrentFileName)
            .ToList();

        Assert.True(rotated.Count <= Kept, $"осталось {rotated.Count} файлов при потолке {Kept}");
    }

    [Fact]
    public void Старое_убирается_по_сроку()
    {
        // Файл, который «пролежал» месяц. Дата правится руками: ждать сутки в
        // тесте нельзя, а срок хранения проверить надо.
        string stale = Path.Combine(_directory, "elitesip-2020-01-01-000000.log");
        File.WriteAllText(stale, "старое");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

        using var log = new LogFile(new LogFile.Settings(_directory, maximumAgeInDays: 14));
        log.Write("свежая строка", "info");
        log.Flush();

        Assert.False(File.Exists(stale), "файл старше срока обязан исчезнуть");
        Assert.Contains("свежая строка", Read(log.CurrentFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Перевод_строки_не_разрывает_запись()
    {
        string line = LogFile.FormatLine(
            new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Local),
            "info",
            "первая\r\nвторая\nтретья");

        Assert.DoesNotContain('\n', line);
        Assert.Contains("первая ⏎ вторая ⏎ третья", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Архив_для_поддержки_собирается_и_несёт_справку()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        log.Write("строка для архива", "info");
        log.Flush();

        string destination = Path.Combine(_directory, SupportArchive.SuggestedName());
        SupportArchive.Make(log.Files(), "EliteSIP 0.1.0\nWindows 10 22H2", destination);

        Assert.True(new FileInfo(destination).Length > 0);
        Assert.EndsWith(".zip", destination, StringComparison.Ordinal);
    }

    [Fact]
    public void Сборка_из_каталога_сбрасывает_очередь_и_передаёт_файлы_а_не_строки()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        log.Write("REGISTER 401 then 200; Contact 203.0.113.7:41234", "info");
        var destination = Path.Combine(_directory, "support.zip");
        SupportArchive.MakeFromDirectory(_directory, "summary", destination, log);
        using var archive = ZipFile.OpenRead(destination);
        using var reader = new StreamReader(archive.GetEntry(LogFile.CurrentFileName)!.Open());
        Assert.Contains("REGISTER 401 then 200", reader.ReadToEnd(), StringComparison.Ordinal);
        Assert.NotNull(archive.GetEntry("collection.txt"));
    }

    [Fact]
    public void Без_писателя_собираются_старые_журналы_и_аварии_с_маскированием()
    {
        File.WriteAllText(Path.Combine(_directory, "elitesip.log"), "root registration");
        Directory.CreateDirectory(Path.Combine(_directory, "Logs"));
        File.WriteAllText(Path.Combine(_directory, "Logs", "elitesip.log"), "legacy registration");
        File.WriteAllText(Path.Combine(_directory, "crash.log"), "failure password=secret-from-crash");
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "must-not-be-exported");
        var destination = Path.Combine(_directory, "support.zip");
        SupportArchive.MakeFromDirectory(_directory, "summary", destination);
        using var archive = ZipFile.OpenRead(destination);
        Assert.Null(archive.GetEntry("settings.json"));
        Assert.Equal(archive.Entries.Count, archive.Entries.Select(entry => entry.FullName).Distinct().Count());
        var text = string.Join('\n', archive.Entries.Select(entry =>
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }));
        Assert.Contains("root registration", text, StringComparison.Ordinal);
        Assert.Contains("legacy registration", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-from-crash", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Отсутствие_журналов_указывается_явно()
    {
        var destination = Path.Combine(_directory, "support.zip");
        SupportArchive.MakeFromDirectory(_directory, "logging disabled", destination);
        using var archive = ZipFile.OpenRead(destination);
        using var reader = new StreamReader(archive.GetEntry("collection.txt")!.Open());
        Assert.Contains("не найдены", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Настройки уезжают внутри архива, а не отдельной кнопкой.
    ///
    /// Кнопок было две, и вторую надо было знать: половина обращений в
    /// поддержку приезжала без настроек.
    /// </summary>
    [Fact]
    public void Архив_несёт_то_что_положили_рядом_с_журналом()
    {
        using var log = new LogFile(new LogFile.Settings(_directory));
        log.Write("строка для архива", "info");
        log.Flush();

        string destination = Path.Combine(_directory, SupportArchive.SuggestedName());
        SupportArchive.Make(
            log.Files(),
            "EliteSIP 0.1.0",
            destination,
            new Dictionary<string, byte[]>
            {
                ["settings.json"] = System.Text.Encoding.UTF8.GetBytes("{\"здесь\":\"настройки\"}"),
            });

        using ZipArchive archive = ZipFile.OpenRead(destination);
        List<string> found = archive.Entries.Select(entry => entry.Name).ToList();

        Assert.Contains("settings.json", found);
        Assert.Contains("summary.txt", found);
        Assert.Contains(LogFile.CurrentFileName, found);
    }
}
