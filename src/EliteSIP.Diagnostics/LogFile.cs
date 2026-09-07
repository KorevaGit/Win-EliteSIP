using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace EliteSIP.Diagnostics;

/// <summary>
/// Журнал в файле: запись, ротация и уборка старого.
///
/// Пишет с отдельного потока. Журнал наполняется из потока интерфейса — там
/// живёт модель приложения, — и класть на него запись в файл нельзя: очередь
/// событий SIP на активном звонке идёт десятками строк в секунду, а запись на
/// полном диске или на сетевом томе блокируется на неопределённое время.
///
/// Ротация по размеру и уборка по сроку и по числу файлов — не аккуратность, а
/// условие работоспособности: рабочее место живёт годами, и журнал без потолка
/// однажды займёт диск целиком. Ошибки записи глушатся намеренно: софтфон,
/// упавший из-за собственного журнала, — худший из возможных исходов
/// диагностики.
///
/// Перенесено из macOS-версии
/// (<c>Packages/Diagnostics/Sources/Diagnostics/LogFile.swift</c>) вместе с
/// тестами. Что изменилось: очередь <c>DispatchQueue</c> стала
/// <see cref="Channel"/> с одним читателем, а дескриптор POSIX —
/// <see cref="AppendOnlyFile"/>. Причина последнего в самом
/// <see cref="AppendOnlyFile"/>, и она та же, что была в оригинале.
/// </summary>
public sealed class LogFile : IDisposable
{
    /// <summary>Настройки журнала.</summary>
    public sealed class Settings
    {
        public Settings(
            string directory,
            int maximumFileBytes = 4 * 1024 * 1024,
            int keptFiles = 5,
            int maximumAgeInDays = 14)
        {
            Directory = directory;
            MaximumFileBytes = maximumFileBytes;
            KeptFiles = keptFiles;
            MaximumAgeInDays = maximumAgeInDays;
        }

        /// <summary>Каталог журнала. Файлы внутри создаются и удаляются сами.</summary>
        public string Directory { get; }

        /// <summary>
        /// Потолок текущего файла. По достижении файл откладывается в архив, а
        /// запись продолжается в новый.
        /// </summary>
        public int MaximumFileBytes { get; }

        /// <summary>Сколько отложенных файлов держим, не считая текущего.</summary>
        public int KeptFiles { get; }

        /// <summary>Сколько дней храним отложенные файлы.</summary>
        public int MaximumAgeInDays { get; }
    }

    public const string CurrentFileName = "elitesip.log";
    private const string RotatedPrefix = "elitesip-";
    private const string FileExtension = "log";

    /// <summary>
    /// Куда журнал кладётся по умолчанию: <c>%LOCALAPPDATA%\EliteSIP\Logs</c>.
    ///
    /// На macOS это был <c>~/Library/Logs/EliteSIP</c> — каталог, куда смотрят и
    /// Console, и человек, которого попросили прислать журнал. Прямого аналога в
    /// Windows нет, и выбран <c>LocalApplicationData</c>, а не роуминговый
    /// <c>ApplicationData</c>: в домене роуминговый профиль ездит по сети при
    /// каждом входе, и журнал на четыре мегабайта ездил бы вместе с ним.
    /// </summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EliteSIP",
        "Logs");

    /// <summary>
    /// Журнал пишется без BOM. Файл читают <c>grep</c>, «Блокнот» и человек в
    /// поддержке; лишние три байта в начале не нужны никому из них.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Settings _settings;
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Task _worker;

    /// <summary>Открытый файл и его размер. Трогаются только на потоке очереди.</summary>
    private AppendOnlyFile? _file;
    private long _writtenBytes;

    public LogFile(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        _worker = Task.Factory.StartNew(
            RunQueue,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Enqueue(new Job(Work: () =>
        {
            OpenFile();
            Prune();
        }));
    }

    public string CurrentFilePath => Path.Combine(_settings.Directory, CurrentFileName);

    /// <summary>Все файлы журнала, новые первыми. Текущий всегда первый.</summary>
    public IReadOnlyList<string> Files()
    {
        string[] contents;
        try
        {
            contents = System.IO.Directory.GetFiles(_settings.Directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            contents = [];
        }

        // Имя отложенного файла — это его время, поэтому обратный порядок по
        // имени и есть «новые первыми». Дата изменения для этого не годится:
        // уборка по сроку её читает, а ротация не трогает.
        List<string> rotated = contents
            .Where(path => Path.GetFileName(path).StartsWith(RotatedPrefix, StringComparison.Ordinal))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        string current = CurrentFilePath;
        var result = new List<string>(rotated.Count + 1);
        if (File.Exists(current))
        {
            result.Add(current);
        }

        result.AddRange(rotated);
        return result;
    }

    /// <summary>Пишет одну строку. Возврат немедленный: файл трогается на своей очереди.</summary>
    public void Write(string message, string level, DateTime? date = null)
    {
        // Маскирование делается здесь, а не на очереди, ровно по одной причине:
        // так его нельзя обойти, добавив второй путь записи.
        string line = FormatLine(date ?? DateTime.Now, level, LogRedaction.Redact(message));
        Enqueue(new Job(Line: line));
    }

    /// <summary>
    /// Дожидается, пока всё записанное окажется в файле.
    ///
    /// Нужен ровно двум вызывающим: сборке архива для поддержки и проверкам.
    /// В обычной работе ждать журнал незачем.
    /// </summary>
    public void Flush()
    {
        RunOnQueueAndWait(() => _file?.Flush());
    }

    /// <summary>Стирает журнал целиком, включая отложенные файлы.</summary>
    public void RemoveAll()
    {
        RunOnQueueAndWait(() =>
        {
            CloseFile();
            _writtenBytes = 0;
            foreach (string path in Files())
            {
                TryDelete(path);
            }

            OpenFile();
        });
    }

    /// <summary>
    /// Закрывает файл и останавливает очередь.
    ///
    /// В оригинале это делал <c>deinit</c>, и звать его было незачем. Здесь
    /// звать обязательно: пока дескриптор открыт, Windows не даст ни удалить
    /// каталог журнала, ни переименовать файл — то есть незакрытый
    /// <see cref="LogFile"/> ломает и уборку, и следующую ротацию.
    /// </summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _worker.Wait(TimeSpan.FromSeconds(5));
        CloseFile();
    }

    // MARK: - Внутреннее, всё на очереди

    private sealed record Job(string? Line = null, Action? Work = null, TaskCompletionSource? Done = null);

    private void Enqueue(Job job)
    {
        // Очередь закрыта — значит журнал уже остановлен. Терять строку на этом
        // этапе допустимо; бросать исключение из журнала — нет.
        _ = _queue.Writer.TryWrite(job);
    }

    private void RunOnQueueAndWait(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Job(Work: work, Done: done)))
        {
            return;
        }

        done.Task.Wait();
    }

    private async Task RunQueue()
    {
        await foreach (Job job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (job.Line is not null)
            {
                Append(job.Line);
            }

            job.Work?.Invoke();
            job.Done?.TrySetResult();
        }
    }

    private void OpenFile()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_settings.Directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        string path = CurrentFilePath;
        _file = AppendOnlyFile.Open(path);

        // Дописываем, а не начинаем сначала: перезапуск приложения не повод
        // терять то, ради чего журнал и заводился.
        _writtenBytes = _file is null ? 0 : LengthOf(path);
    }

    private void CloseFile()
    {
        _file?.Dispose();
        _file = null;
    }

    private void Append(string line)
    {
        byte[] data = Utf8NoBom.GetBytes(line + "\n");

        _file ??= AppendOnlyFile.Open(CurrentFilePath);
        if (_file is null)
        {
            return;
        }

        _writtenBytes += _file.Append(data);

        if (_writtenBytes >= _settings.MaximumFileBytes)
        {
            Rotate();
        }
    }

    private void Rotate()
    {
        CloseFile();

        string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        string destination = Path.Combine(
            _settings.Directory,
            $"{RotatedPrefix}{stamp}.{FileExtension}");

        // Совпадение имени возможно только при двух ротациях в одну секунду —
        // то есть при потолке в несколько байт. Такой файл проще перезаписать,
        // чем городить счётчик.
        TryDelete(destination);
        try
        {
            File.Move(CurrentFilePath, destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Не переехало — значит текущий файл останется расти дальше. Это
            // хуже, чем ротация, но лучше, чем потеря журнала.
        }

        OpenFile();
        Prune();
    }

    /// <summary>Убирает лишние файлы: сначала по сроку, потом по числу.</summary>
    private void Prune()
    {
        List<string> rotated = Files()
            .Where(path => !string.Equals(Path.GetFileName(path), CurrentFileName, StringComparison.Ordinal))
            .ToList();

        if (_settings.MaximumAgeInDays > 0)
        {
            DateTime deadline = DateTime.UtcNow.AddDays(-_settings.MaximumAgeInDays);
            var survivors = new List<string>(rotated.Count);
            foreach (string path in rotated)
            {
                if (File.GetLastWriteTimeUtc(path) < deadline)
                {
                    TryDelete(path);
                }
                else
                {
                    survivors.Add(path);
                }
            }

            rotated = survivors;
        }

        int kept = Math.Max(_settings.KeptFiles, 0);
        if (rotated.Count <= kept)
        {
            return;
        }

        foreach (string path in rotated.Skip(kept))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Файл держит кто-то ещё — переживём до следующей уборки.
        }
    }

    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // MARK: - Формат

    /// <summary>
    /// Одна запись — одна строка.
    ///
    /// Перевод строки внутри сообщения заменяется значком: журнал разбирают
    /// <c>grep</c> и глаза, и запись, размазанная по трём строкам, ломает обоих.
    ///
    /// Время локальное и с миллисекундами: журнал сверяют с рассказом оператора
    /// («около двух часов»), а не с UTC.
    /// </summary>
    internal static string FormatLine(DateTime date, string level, string message)
    {
        string flattened = message
            .Replace("\r\n", " ⏎ ", StringComparison.Ordinal)
            .Replace("\n", " ⏎ ", StringComparison.Ordinal)
            .Replace("\r", " ⏎ ", StringComparison.Ordinal);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{date:yyyy-MM-dd HH:mm:ss.fff} [{level}] {flattened}");
    }
}
