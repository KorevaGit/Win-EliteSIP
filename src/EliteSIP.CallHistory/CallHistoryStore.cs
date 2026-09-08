using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EliteSIP.CallHistory;

/// <summary>Что показывать в списке.</summary>
public enum HistoryFilter
{
    All,
    Incoming,
    Outgoing,
    Missed,
}

/// <summary>Слова фильтров. Русские литералы до ресурсов W8, как и везде.</summary>
public static class HistoryFilters
{
    public static string Title(this HistoryFilter filter) => filter switch
    {
        HistoryFilter.All => "Все",
        HistoryFilter.Incoming => "Входящие",
        HistoryFilter.Outgoing => "Исходящие",
        HistoryFilter.Missed => "Пропущенные",
        _ => "Все",
    };

    /// <summary>
    /// Условие для <c>WHERE</c>. Без параметров: подставляются только константы
    /// самого перечисления, снаружи сюда ничего не попадает.
    /// </summary>
    internal static string? Condition(this HistoryFilter filter) => filter switch
    {
        HistoryFilter.All => null,
        HistoryFilter.Incoming => "direction = 0",
        HistoryFilter.Outgoing => "direction = 1",
        HistoryFilter.Missed => "direction = 0 AND answered_at IS NULL",
        _ => null,
    };
}

/// <summary>
/// Что показывает окно, кроме направления.
///
/// Отдельным типом, а не двумя параметрами у каждого метода: условие выборки
/// собирается в одном месте, и добавить третью грань (скажем, роль) можно, не
/// переписывая четыре сигнатуры. Внутрь строки SQL отсюда не попадает ничего —
/// и профиль, и границы дня уходят параметрами.
/// </summary>
/// <param name="ProfileId">
/// Профиль, чью историю показываем.
///
/// <b>Не опциональный по смыслу, а опциональный технически.</b> Граница жёсткая:
/// окно всегда показывает ровно один профиль, и <c>null</c> здесь означает
/// «профиля нет вовсе» — тогда не показывается ничего. Это не то же самое, что
/// «все профили»: такого режима у истории нет.
/// </param>
/// <param name="Day">
/// Начало выбранных местных суток. <c>null</c> — все дни. Границы считает
/// вызывающий своим календарём: SQLite про местное время знает только через
/// модификатор <c>'localtime'</c>, а он берёт пояс системы в момент запроса.
/// </param>
public readonly record struct HistoryScope(Guid? ProfileId, DateTimeOffset? Day = null);

/// <summary>
/// Чем закончилось открытие базы. Нужно приложению, чтобы написать об этом в
/// журнал: молча работающая история, которая ничего не помнит, — худший исход из
/// возможных.
/// </summary>
public abstract record HistoryOpenOutcome
{
    private HistoryOpenOutcome()
    {
    }

    /// <summary>База открыта, всё в порядке.</summary>
    public sealed record Ready : HistoryOpenOutcome;

    /// <summary>
    /// База была испорчена и отставлена в сторону под этим именем; работа
    /// продолжается с чистой.
    /// </summary>
    public sealed record ReplacedDamaged(string DamagedPath) : HistoryOpenOutcome;

    /// <summary>Базу открыть не удалось вовсе. История в этом запуске не пишется.</summary>
    public sealed record Unavailable(string Reason) : HistoryOpenOutcome;
}

/// <summary>
/// Локальная история звонков: запись по событию, выборка и уборка по сроку.
///
/// <b>Пишется по ходу звонка, а не в конце.</b> Строка заводится на первом гудке
/// и дописывается ответом, переводом и завершением. Приложение может упасть
/// посреди разговора, и запись «в конце» теряла бы ровно те звонки, после
/// которых оно упало, — то есть те, ради которых историю и открывают.
///
/// <b>База отдельно от журнала и от настроек.</b> Журнал — это то, что человек по
/// инструкции отправляет в поддержку. История же живёт своим сроком и своей
/// политикой удаления, и в архив для поддержки не попадает вовсе.
///
/// Работа с диском — на своём потоке, как у файлового журнала. Запись
/// асинхронна: <c>INSERT</c> на полном диске или на сетевом томе блокируется на
/// неопределённое время, а происходит он в момент, когда оператор нажал
/// «Позвонить». Чтение синхронно — его ждёт список, и ждать там нечего: выборка
/// идёт по индексу и с потолком в двести строк.
///
/// <b>Почему нет обёртки над SQLite.</b> В оригинале рядом лежал
/// <c>SQLiteDatabase.swift</c> — 180 строк вокруг C API, заведённые ровно затем,
/// чтобы в одном месте проверить глазами <c>sqlite3_finalize</c> и
/// <c>SQLITE_TRANSIENT</c>. У <c>Microsoft.Data.Sqlite</c> и то и другое уже
/// сделано: команда освобождается <c>using</c>, параметры копируются. Переносить
/// обёртку значило бы переносить решение чужой задачи.
/// </summary>
public sealed class CallHistoryStore : IDisposable
{
    /// <summary>Настройки хранилища.</summary>
    /// <param name="FilePath">Файл базы. Каталог создаётся сам.</param>
    /// <param name="MaximumAgeInDays">
    /// Сколько дней держим записи. Это решение про персональные данные, а не про
    /// диск: в записях лежат номера лидов. Поэтому срок задаёт администратор, а
    /// не сборка.
    /// </param>
    public sealed record Settings(string FilePath, int MaximumAgeInDays = 30);

    /// <summary>
    /// Строка приложения, которой закрываются записи, оставшиеся открытыми.
    ///
    /// Отдельной константой, потому что она попадает в глаза оператору и её
    /// нельзя менять по частям в двух местах.
    /// </summary>
    public const string InterruptedReason = "приложение завершилось";

    private const string RecordColumns =
        "id, call_id, server_call_id, direction, role, number, sip_login, display_name, " +
        "profile_id, profile_label, started_at, answered_at, ended_at, end_reason, " +
        "outcome_code, was_transferred, was_conference, was_distribution";

    private readonly Settings _settings;

    /// <summary>
    /// Очередь работы с диском. Одна на хранилище, и она же — весь порядок: в
    /// оригинале ту же роль играла последовательная <c>DispatchQueue</c>.
    /// </summary>
    private readonly BlockingCollection<Action> _work = new(new ConcurrentQueue<Action>());

    private readonly Thread _worker;

    /// <summary>
    /// Трогается только на своём потоке. <c>null</c> — база недоступна, и тогда
    /// весь остальной код превращается в набор пустых действий: софтфон, упавший
    /// из-за собственной истории, — исход хуже, чем софтфон без истории.
    /// </summary>
    private SqliteConnection? _database;

    private HistoryOpenOutcome _outcome = new HistoryOpenOutcome.Unavailable("база ещё не открывалась");

    private bool _disposed;

    public CallHistoryStore(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        // Синхронно и до запуска потока: приложение сразу после создания
        // спрашивает исход, чтобы написать о нём в журнал, а незакрытые записи
        // прошлого запуска обязаны закрыться до того, как заведётся первая
        // новая.
        Open();

        _worker = new Thread(RunWorkerLoop)
        {
            IsBackground = true,
            Name = "EliteSIP.History",
        };
        _worker.Start();
    }

    /// <summary>Чем закончилось открытие.</summary>
    public HistoryOpenOutcome OpenOutcome => _outcome;

    public string FilePath => _settings.FilePath;

    // MARK: - Очередь

    private void RunWorkerLoop()
    {
        foreach (Action item in _work.GetConsumingEnumerable())
        {
            item();
        }
    }

    /// <summary>Отправить работу и не ждать. Порядок сохраняется.</summary>
    private void Post(Action action)
    {
        if (_work.IsAddingCompleted)
        {
            return;
        }

        try
        {
            _work.Add(action);
        }
        catch (InvalidOperationException)
        {
            // Хранилище закрывают ровно один раз и в конце работы приложения;
            // гонка с закрытием означает «писать уже некуда», а не поломку.
        }
    }

    /// <summary>
    /// Отправить работу и дождаться ответа.
    ///
    /// Через ту же очередь, а не под замком рядом с ней: иначе чтение обгоняло
    /// бы ещё не выполненные записи, и список, открытый сразу после звонка,
    /// показывал бы историю без него.
    /// </summary>
    private T Await<T>(Func<T> action)
    {
        if (_work.IsAddingCompleted)
        {
            return action();
        }

        using ManualResetEventSlim done = new(false);
        T result = default!;
        Exception? failure = null;

        Post(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        return failure is null ? result : throw failure;
    }

    /// <summary>
    /// Дожидается, пока всё записанное окажется в базе.
    ///
    /// Нужен проверкам и списку, который открывают сразу после звонка.
    /// </summary>
    public void Flush() => Await(() => true);

    // MARK: - Открытие

    private void Open()
    {
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_settings.FilePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Файл заводим сами и сразу закрываем от всех, кроме владельца.
            // SQLite создал бы его с наследованными правами каталога, то есть
            // читаемым всеми, кому доступен каталог, — а в нём номера лидов,
            // ради которых у истории вообще есть срок хранения. Пустой файл
            // SQLite примет как новую базу.
            HistoryFilePrivacy.Restrict(_settings.FilePath);

            SqliteConnection database = OpenConnection();

            // Порча файла не гипотетическая: рабочее место выключают из розетки,
            // а базу кладут в профиль пользователя, который на части машин
            // синхронизируется в облако. Работать с битой базой хуже, чем начать
            // заново: выборка из неё возвращает случайные строки, и по ним потом
            // разбирают жалобу.
            if (!IsIntact(database))
            {
                database.Dispose();
                string damaged = Quarantine();
                database = OpenConnection();
                Prepare(database);
                _database = database;
                _outcome = new HistoryOpenOutcome.ReplacedDamaged(damaged);
                return;
            }

            Prepare(database);
            // Повторно: журнал упреждающей записи и разделяемую память SQLite
            // заводит сам, и заводит их первой же транзакцией — то есть внутри
            // `Prepare`, а не раньше. В них лежит то же, что и в базе.
            HistoryFilePrivacy.Restrict(_settings.FilePath);
            _database = database;
            _outcome = new HistoryOpenOutcome.Ready();
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            _database?.Dispose();
            _database = null;
            _outcome = new HistoryOpenOutcome.Unavailable(error.Message);
        }
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _settings.FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Пул держал бы соединение открытым после `Dispose`, и файл
            // испорченной базы не удалось бы отставить в сторону: Windows не даёт
            // переименовать файл, который кто-то держит открытым.
            Pooling = false,
        };

        SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Проверка целостности файла. <c>true</c>, только если SQLite ответил ровно
    /// <c>ok</c>.
    ///
    /// <c>quick_check</c>, а не полный <c>integrity_check</c>: полный на большой
    /// базе читает её целиком, а проверка стоит на пути запуска приложения.
    /// Быструю проверку проходит всё, что можно читать без риска, и этого
    /// достаточно — цель здесь не аудит файла, а «не работать молча с битой
    /// базой».
    /// </summary>
    private static bool IsIntact(SqliteConnection database)
    {
        try
        {
            using SqliteCommand command = database.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";
            return command.ExecuteScalar() as string == "ok";
        }
        catch (SqliteException)
        {
            // Затёртый заголовок файла — это отказ уже на первом запросе, а не
            // ответ «не ok». Для нас оба случая одинаковы.
            return false;
        }
    }

    /// <summary>
    /// Отставляет испорченную базу в сторону и возвращает её новое имя.
    ///
    /// Именно отставляет, а не удаляет. Файл с номерами лидов — это то, что
    /// нельзя стирать походя, и это же единственный шанс достать из него
    /// что-нибудь руками, если жалоба важнее аккуратности.
    /// </summary>
    private string Quarantine()
    {
        // Время в имени — UTC: имя файла читают в переписке с поддержкой, и
        // «когда именно это случилось» не должно зависеть от того, у кого на
        // машине какой пояс. Двоеточия в имени файла Windows не примет.
        string stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ", CultureInfo.InvariantCulture);
        string directory = Path.GetDirectoryName(Path.GetFullPath(_settings.FilePath)) ?? ".";
        string name = Path.GetFileNameWithoutExtension(_settings.FilePath);
        string damaged = Path.Combine(directory, $"{name}-повреждена-{stamp}.sqlite");

        if (File.Exists(damaged))
        {
            File.Delete(damaged);
        }

        File.Move(_settings.FilePath, damaged);

        // Спутники WAL относятся к отставленной базе и с новой несовместимы.
        foreach (string suffix in HistoryFilePrivacy.Companions)
        {
            string path = _settings.FilePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return damaged;
    }

    private void Prepare(SqliteConnection database)
    {
        // FULL, а не NORMAL: событий здесь единицы на звонок, экономить нечего,
        // а обещание «история переживает падение» не должно зависеть от того,
        // успела ли система сбросить кэш.
        Execute(database, "PRAGMA journal_mode = WAL;");
        Execute(database, "PRAGMA synchronous = FULL;");

        Execute(database, """
            CREATE TABLE IF NOT EXISTS calls (
                id TEXT PRIMARY KEY NOT NULL,
                call_id TEXT NOT NULL,
                server_call_id TEXT,
                direction INTEGER NOT NULL,
                role INTEGER NOT NULL,
                number TEXT NOT NULL,
                sip_login TEXT,
                display_name TEXT,
                profile_id TEXT,
                profile_label TEXT,
                started_at REAL NOT NULL,
                answered_at REAL,
                ended_at REAL,
                end_reason TEXT,
                outcome_code INTEGER,
                was_transferred INTEGER NOT NULL DEFAULT 0,
                was_conference INTEGER NOT NULL DEFAULT 0,
                was_distribution INTEGER NOT NULL DEFAULT 0
            );
            """);

        Migrate(database);

        // Индекс по времени — то, ради чего выбран SQLite. Список открывается
        // выборкой «последние двести по убыванию времени», и без индекса она на
        // десяти тысячах записей означает сортировку всей таблицы при каждом
        // открытии окна.
        Execute(database, "CREATE INDEX IF NOT EXISTS calls_started_at ON calls(started_at DESC);");
        // Направление стоит первым: фильтр «пропущенные» отбирает по нему, а
        // порядок внутри отбора всё равно по времени.
        Execute(
            database,
            "CREATE INDEX IF NOT EXISTS calls_direction_started_at ON calls(direction, started_at DESC);");
        // Переопределение имени из EliteDash (M9) ищет по номеру.
        Execute(database, "CREATE INDEX IF NOT EXISTS calls_number ON calls(number);");
        // Профиль первым: история жёстко ограничена активным профилем, то есть
        // **каждая** выборка отбирает по нему. Без этого индекса приёмка «десять
        // тысяч записей не замедляют открытие окна» перестала бы выполняться.
        Execute(
            database,
            "CREATE INDEX IF NOT EXISTS calls_profile_started_at ON calls(profile_id, started_at DESC);");

        CloseInterrupted(database);
        DeleteExpired(database, DateTimeOffset.Now);
    }

    /// <summary>
    /// Догоняет схему до текущей.
    ///
    /// Отдельным шагом, а не «пересоздать таблицу»: в ней лежат номера лидов за
    /// месяц, и терять их при обновлении приложения нельзя.
    /// <c>CREATE TABLE IF NOT EXISTS</c> выше на существующей базе не делает
    /// ничего — новую колонку добавляет только <c>ALTER TABLE</c>.
    ///
    /// Наличие колонки проверяется, а не глотается ошибка: <c>ALTER TABLE</c> на
    /// уже добавленной колонке — это ошибка, неотличимая от настоящей поломки, и
    /// молча пропускать её значило бы не заметить, что схема не сошлась.
    /// </summary>
    private static void Migrate(SqliteConnection database)
    {
        HashSet<string> columns = new(StringComparer.Ordinal);
        using (SqliteCommand command = database.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(calls);";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("outcome_code"))
        {
            Execute(database, "ALTER TABLE calls ADD COLUMN outcome_code INTEGER;");
        }

        // Раздача лида. Умолчание — ноль, то есть «обычный звонок»: у записей,
        // заведённых раньше, признака нет и взять его негде.
        if (!columns.Contains("was_distribution"))
        {
            Execute(database, "ALTER TABLE calls ADD COLUMN was_distribution INTEGER NOT NULL DEFAULT 0;");
        }
    }

    /// <summary>
    /// Закрывает записи, оставшиеся открытыми с прошлого запуска.
    ///
    /// Открытая запись означает ровно одно: приложение завершилось посреди
    /// звонка. Оставить её открытой навсегда нельзя — она выглядела бы как вечно
    /// идущий разговор, — а придумывать ей время окончания нечестно. Поэтому
    /// концом становится время начала, а причиной — прямая строка о том, что
    /// произошло.
    /// </summary>
    private static void CloseInterrupted(SqliteConnection database)
        => Execute(
            database,
            "UPDATE calls SET ended_at = started_at, end_reason = $reason WHERE ended_at IS NULL;",
            ("$reason", InterruptedReason));

    // MARK: - Запись

    /// <summary>Заводит запись о начавшемся звонке.</summary>
    public void Begin(CallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Post(() =>
        {
            if (_database is not { } database)
            {
                return;
            }

            TryExecute(
                database,
                $"""
                INSERT OR REPLACE INTO calls ({RecordColumns})
                VALUES ($id, $callId, $serverCallId, $direction, $role, $number, $sipLogin,
                        $displayName, $profileId, $profileLabel, $startedAt, $answeredAt,
                        $endedAt, $endReason, $outcomeCode, $wasTransferred, $wasConference,
                        $wasDistribution);
                """,
                ("$id", record.Id.ToString()),
                ("$callId", record.CallId),
                ("$serverCallId", record.ServerCallId),
                ("$direction", (long)record.Direction),
                ("$role", (long)record.Role),
                ("$number", record.Number),
                ("$sipLogin", record.SipLogin),
                ("$displayName", record.DisplayName),
                ("$profileId", record.ProfileId?.ToString()),
                ("$profileLabel", record.ProfileLabel),
                ("$startedAt", Seconds(record.StartedAt)),
                ("$answeredAt", Seconds(record.AnsweredAt)),
                ("$endedAt", Seconds(record.EndedAt)),
                ("$endReason", record.EndReason),
                ("$outcomeCode", Code(record.OutcomeCode)),
                ("$wasTransferred", record.WasTransferred ? 1L : 0L),
                ("$wasConference", record.WasConference ? 1L : 0L),
                ("$wasDistribution", record.WasDistribution ? 1L : 0L));
        });
    }

    /// <summary>
    /// Отмечает ответ. Повторный вызов ничего не меняет: время ответа — первое, а
    /// не последнее, и повторное согласование медиа не должно его сдвигать.
    /// </summary>
    public void MarkAnswered(Guid id, DateTimeOffset? at = null)
        => Update(
            "UPDATE calls SET answered_at = $at WHERE id = $id AND answered_at IS NULL;",
            ("$at", Seconds(at ?? DateTimeOffset.Now)),
            ("$id", id.ToString()));

    /// <summary>Отмечает, что по звонку был перевод.</summary>
    public void MarkTransferred(Guid id)
        => Update("UPDATE calls SET was_transferred = 1 WHERE id = $id;", ("$id", id.ToString()));

    /// <summary>Отмечает, что звонок стал конференцией.</summary>
    public void MarkConference(Guid id)
        => Update("UPDATE calls SET was_conference = 1 WHERE id = $id;", ("$id", id.ToString()));

    /// <summary>
    /// Закрывает запись.
    ///
    /// Условие <c>ended_at IS NULL</c> защищает от второго закрытия: причину
    /// звонку назначает первое событие, которое его завершило, а не последнее.
    /// Иначе «переведён на 601» затиралось бы обычным «завершён», приезжающим
    /// следом по той же линии. Код исхода идёт тем же запросом и под тем же
    /// условием: слово и причина обязаны быть из одного события, иначе строка
    /// скажет «занято» под причиной «переведён».
    /// </summary>
    public void Finish(Guid id, string reason, CallOutcome? outcome = null, DateTimeOffset? at = null)
        => Update(
            """
            UPDATE calls SET ended_at = $at, end_reason = $reason, outcome_code = $outcome
            WHERE id = $id AND ended_at IS NULL;
            """,
            ("$at", Seconds(at ?? DateTimeOffset.Now)),
            ("$reason", reason),
            ("$outcome", Code(outcome)),
            ("$id", id.ToString()));

    /// <summary>Привязывает идентификатор звонка со стороны сервера. Задел под M9.</summary>
    public void AttachServerCallId(string serverCallId, Guid id)
        => Update(
            "UPDATE calls SET server_call_id = $server WHERE id = $id;",
            ("$server", serverCallId),
            ("$id", id.ToString()));

    /// <summary>
    /// Переопределяет отображаемое имя для всех записей с этим номером.
    ///
    /// Задел под синхронизацию с EliteDash (M9). Трогает только
    /// <c>display_name</c>: номер и SIP-логин остаются такими, какими пришли, —
    /// иначе пересчитать имя заново после смены списка у EliteDash будет не из
    /// чего.
    /// </summary>
    public void OverrideDisplayName(string? displayName, string number)
        => Update(
            "UPDATE calls SET display_name = $name WHERE number = $number;",
            ("$name", displayName),
            ("$number", number));

    private void Update(string sql, params (string Name, object? Value)[] parameters)
        => Post(() =>
        {
            if (_database is { } database)
            {
                TryExecute(database, sql, parameters);
            }
        });

    // MARK: - Чтение

    /// <summary>
    /// Условие и параметры под фильтр и область.
    ///
    /// Единственное место, где собирается <c>WHERE</c>. Внутрь строки попадают
    /// только константы самого перечисления; профиль и границы дня уходят
    /// параметрами — их значения приходят снаружи.
    /// </summary>
    private static (string Clause, List<(string Name, object? Value)> Parameters) Selection(
        HistoryFilter filter,
        HistoryScope scope)
    {
        List<string> conditions = [];
        List<(string Name, object? Value)> parameters = [];

        if (filter.Condition() is { } condition)
        {
            conditions.Add(condition);
        }

        // Профиль отбирается всегда, даже когда его нет: null означает «профиля
        // нет», и показывать в этом случае надо ничего, а не всё. Записи с пустым
        // `profile_id` не видны никогда — граница строгая, и «ничей» звонок не
        // имеет права всплыть в чужой истории.
        conditions.Add("profile_id = $profile");
        parameters.Add(("$profile", scope.ProfileId?.ToString()));

        if (scope.Day is { } day)
        {
            conditions.Add("started_at >= $dayStart AND started_at < $dayEnd");
            parameters.Add(("$dayStart", Seconds(day)));
            parameters.Add(("$dayEnd", Seconds(day.AddDays(1))));
        }

        return ("WHERE " + string.Join(" AND ", conditions), parameters);
    }

    /// <summary>
    /// Записи, новые первыми.
    ///
    /// <paramref name="offset"/> — не украшение: окно догружает следующие двести,
    /// когда оператор долистал до низа. Без этого всё, что старше первой
    /// страницы, было недостижимо ни одним действием.
    /// </summary>
    public IReadOnlyList<CallRecord> Records(
        HistoryScope scope,
        HistoryFilter filter = HistoryFilter.All,
        int limit = 200,
        int offset = 0)
        => Await<IReadOnlyList<CallRecord>>(() =>
        {
            if (_database is not { } database)
            {
                return [];
            }

            (string clause, List<(string Name, object? Value)> parameters) = Selection(filter, scope);
            parameters.Add(("$limit", (long)limit));
            parameters.Add(("$offset", (long)offset));

            List<CallRecord> records = [];
            try
            {
                using SqliteCommand command = Command(
                    database,
                    $"""
                    SELECT {RecordColumns} FROM calls {clause}
                    ORDER BY started_at DESC LIMIT $limit OFFSET $offset;
                    """,
                    parameters);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(ReadRecord(reader));
                }
            }
            catch (SqliteException)
            {
                return [];
            }

            return records;
        });

    /// <summary>
    /// Сколько записей лежит на машине всего — по всем профилям.
    ///
    /// Единственное место, которое смотрит поверх границы профилей, и оно
    /// административное: в «Управлении» этим числом отвечают на вопрос «сколько
    /// персональных данных здесь накоплено», а он про машину, а не про того, кто
    /// сейчас за ней сидит. Записи при этом не показываются — только считаются.
    /// </summary>
    public int TotalCount()
        => Await(() => _database is { } database ? (int)ScalarInteger(database, "SELECT count(*) FROM calls;") : 0);

    /// <summary>
    /// Номера, с которых на эту машину приходили вызовы, — под подсказки словаря
    /// очередей.
    ///
    /// Второе и последнее место, смотрящее поверх границы профилей, и по той же
    /// причине: словарь очередей общий для машины, а не для того, кто сейчас за
    /// ней сидит.
    ///
    /// <b><paramref name="maximumDigits"/> — не украшение, а граница между
    /// очередью и лидом.</b> Номер очереди на FreePBX короткий, номер лида —
    /// полный телефонный. Без отсечки подсказка превратилась бы в список номеров
    /// клиентов, вывешенный в окне настроек. Вписать длинный номер руками
    /// по-прежнему можно: отсечка ограничивает подсказку, а не словарь.
    /// </summary>
    public IReadOnlyList<NumberSighting> IncomingNumbers(int maximumDigits, int limit = 20)
        => Await<IReadOnlyList<NumberSighting>>(() =>
        {
            if (_database is not { } database)
            {
                return [];
            }

            List<NumberSighting> sightings = [];
            try
            {
                using SqliteCommand command = Command(
                    database,
                    """
                    SELECT number, max(started_at), count(*) FROM calls
                    WHERE direction = 0 AND number <> ''
                    GROUP BY number ORDER BY max(started_at) DESC LIMIT $limit;
                    """,
                    [("$limit", (long)(Math.Max(1, limit) * 4))]);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    sightings.Add(new NumberSighting(
                        reader.GetString(0),
                        FromSeconds(reader.GetDouble(1)),
                        (int)reader.GetInt64(2)));
                }
            }
            catch (SqliteException)
            {
                return [];
            }

            // Отсечка по длине — уже здесь, а не в SQL: SQLite умеет считать
            // цифры только выражением на весь столбец, и оно не пользуется
            // индексом. Выборка и без того ограничена лимитом.
            return sightings
                .Where(sighting => sighting.DigitCount <= maximumDigits)
                .Take(Math.Max(1, limit))
                .ToList();
        });

    public int Count(HistoryScope scope, HistoryFilter filter = HistoryFilter.All)
        => Await(() =>
        {
            if (_database is not { } database)
            {
                return 0;
            }

            (string clause, List<(string Name, object? Value)> parameters) = Selection(filter, scope);
            return (int)ScalarInteger(database, $"SELECT count(*) FROM calls {clause};", parameters);
        });

    /// <summary>
    /// Дни, в которые у профиля были звонки, — под точки в календаре.
    ///
    /// Возвращает начала местных суток. Группировка идёт в SQLite модификатором
    /// <c>'localtime'</c>, а не вычитанием часов: смещение пояса не постоянно —
    /// внутри срока хранения может лежать переход на летнее время, и звонок в
    /// ночь перевода иначе попал бы в соседний день.
    ///
    /// Фильтр направления сюда не передаётся намеренно. Точка отвечает на
    /// «работал ли я в этот день», а не «были ли в этот день пропущенные»: иначе
    /// календарь пустел бы при переключении фильтра, и день, который оператор
    /// точно помнит, оказывался бы неотмеченным.
    /// </summary>
    public IReadOnlySet<DateTimeOffset> DaysWithCalls(HistoryScope scope, DateTimeOffset? now = null)
        => Await<IReadOnlySet<DateTimeOffset>>(() =>
        {
            if (_database is not { } database)
            {
                return new HashSet<DateTimeOffset>();
            }

            int days = Math.Clamp(_settings.MaximumAgeInDays, 1, 3650);
            DateTimeOffset horizon = (now ?? DateTimeOffset.Now).AddDays(-days);

            HashSet<DateTimeOffset> result = [];
            try
            {
                using SqliteCommand command = Command(
                    database,
                    """
                    SELECT DISTINCT date(started_at, 'unixepoch', 'localtime') FROM calls
                    WHERE profile_id = $profile AND started_at >= $horizon;
                    """,
                    [
                        ("$profile", scope.ProfileId?.ToString()),
                        ("$horizon", Seconds(horizon)),
                    ]);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0))
                    {
                        continue;
                    }

                    if (DateTime.TryParseExact(
                            reader.GetString(0),
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out DateTime day))
                    {
                        // Смещение берётся для самого дня, а не для сегодняшнего:
                        // иначе после перехода на летнее время половина точек
                        // календаря разъехалась бы на час.
                        result.Add(new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Unspecified),
                            TimeZoneInfo.Local.GetUtcOffset(day)));
                    }
                }
            }
            catch (SqliteException)
            {
                return new HashSet<DateTimeOffset>();
            }

            return result;
        });

    /// <summary>
    /// Удаляет историю профиля целиком. Возвращает, сколько удалила.
    ///
    /// Синхронно и с ответом, потому что вызывающему он нужен дважды: спросить
    /// человека до удаления («будут удалены 137 записей») и написать число в
    /// журнал после. Отменить это нельзя — поэтому спрашивают заранее, а не
    /// предлагают возврат потом.
    /// </summary>
    public int DeleteHistory(Guid profileId)
        => Await(() =>
        {
            if (_database is not { } database)
            {
                return 0;
            }

            return TryExecute(
                database,
                "DELETE FROM calls WHERE profile_id = $profile;",
                ("$profile", profileId.ToString()));
        });

    /// <summary>
    /// Стирает историю целиком и возвращает, сколько записей было.
    ///
    /// <b>Целиком, а не выборочно.</b> Выборочное удаление и есть заметание
    /// следов — стереть один неудобный звонок; отсутствие всей истории заметно
    /// само по себе. Число возвращается затем, чтобы вызывающий записал его в
    /// журнал: стереть можно, бесследно — нет.
    /// </summary>
    public int DeleteAll()
        => Await(() =>
        {
            if (_database is not { } database)
            {
                return 0;
            }

            int before = (int)ScalarInteger(database, "SELECT count(*) FROM calls;");
            TryExecute(database, "DELETE FROM calls;");
            // Место возвращается системе сразу: иначе файл базы остаётся прежнего
            // размера, и «стёр историю» выглядит как «ничего не произошло» для
            // того, кто смотрит на диск.
            TryExecute(database, "VACUUM;");
            return before;
        });

    // MARK: - Срок хранения

    /// <summary>
    /// Удаляет записи старше срока. Возвращает, сколько удалила.
    ///
    /// Синхронно, потому что вызывающему нужен ответ для журнала: удаление
    /// персональных данных — это то, о чём в журнале должна остаться строка.
    /// </summary>
    public int Prune(DateTimeOffset? now = null)
        => Await(() => _database is { } database ? DeleteExpired(database, now ?? DateTimeOffset.Now) : 0);

    private int DeleteExpired(SqliteConnection database, DateTimeOffset now)
    {
        // Ноль и меньше означало бы «удалять всё, что старше сейчас», то есть
        // историю длиной в один звонок. Значение приезжает из файла настроек,
        // который правит человек, поэтому границы ставятся здесь, а не только в
        // интерфейсе.
        int days = Math.Clamp(_settings.MaximumAgeInDays, 1, 3650);
        DateTimeOffset horizon = now.AddDays(-days);
        return TryExecute(
            database,
            "DELETE FROM calls WHERE started_at < $horizon;",
            ("$horizon", Seconds(horizon)));
    }

    // MARK: - Мелочь вокруг SQLite

    private static SqliteCommand Command(
        SqliteConnection database,
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters)
    {
        SqliteCommand command = database.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            // Пустота — это `NULL` в базе, а не пустая строка и не ноль: колонка
            // «имени нет» и колонка «имя пустое» в выборках ведут себя
            // по-разному, и `answered_at IS NULL` — то, на чём стоит весь фильтр
            // пропущенных.
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static void Execute(SqliteConnection database, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(database, sql, parameters);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// То же, но отказ базы не роняет приложение.
    ///
    /// Возврат — сколько строк тронуто. Отказы глотаются осознанно и только на
    /// записи: полный диск не повод обрывать разговор, о котором делается запись.
    /// </summary>
    private static int TryExecute(
        SqliteConnection database,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        try
        {
            using SqliteCommand command = Command(database, sql, parameters);
            return command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static long ScalarInteger(
        SqliteConnection database,
        string sql,
        IReadOnlyList<(string Name, object? Value)>? parameters = null)
    {
        try
        {
            using SqliteCommand command = Command(database, sql, parameters ?? []);
            return command.ExecuteScalar() is long value ? value : 0;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Время в базе — секунды от эпохи дробным числом, как в оригинале. Через
    /// миллисекунды, а не <c>ToUnixTimeSeconds</c>: последний отбрасывает
    /// дробную часть, и два звонка внутри одной секунды перестали бы
    /// упорядочиваться.
    /// </summary>
    private static double? Seconds(DateTimeOffset? value)
    {
        if (value is not { } moment)
        {
            return null;
        }

        return moment.ToUnixTimeMilliseconds() / 1000.0;
    }

    private static DateTimeOffset FromSeconds(double seconds)
        => DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)).ToLocalTime();

    private static long? Code(CallOutcome? outcome)
        => outcome is { } value ? (long)value : null;

    private static CallRecord ReadRecord(SqliteDataReader reader)
        => new()
        {
            Id = Guid.TryParse(reader.GetString(0), out Guid id) ? id : Guid.NewGuid(),
            CallId = reader.GetString(1),
            ServerCallId = reader.IsDBNull(2) ? null : reader.GetString(2),
            Direction = (CallDirection)reader.GetInt64(3),
            Role = (CallRole)reader.GetInt64(4),
            Number = reader.GetString(5),
            SipLogin = reader.IsDBNull(6) ? null : reader.GetString(6),
            DisplayName = reader.IsDBNull(7) ? null : reader.GetString(7),
            ProfileId = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
            ProfileLabel = reader.IsDBNull(9) ? null : reader.GetString(9),
            StartedAt = FromSeconds(reader.GetDouble(10)),
            AnsweredAt = reader.IsDBNull(11) ? null : FromSeconds(reader.GetDouble(11)),
            EndedAt = reader.IsDBNull(12) ? null : FromSeconds(reader.GetDouble(12)),
            EndReason = reader.IsDBNull(13) ? null : reader.GetString(13),
            // Ноль здесь — это и «пусто», и «такого кода нет»: значения
            // перечисления начинаются с единицы именно затем, чтобы одно
            // прочтение отвечало на оба вопроса.
            OutcomeCode = reader.IsDBNull(14) ? null : ToOutcome(reader.GetInt64(14)),
            WasTransferred = reader.GetInt64(15) != 0,
            WasConference = reader.GetInt64(16) != 0,
            WasDistribution = reader.GetInt64(17) != 0,
        };

    private static CallOutcome? ToOutcome(long raw)
        => Enum.IsDefined(typeof(CallOutcome), (int)raw) ? (CallOutcome)raw : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _work.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(5));
        _database?.Dispose();
        _database = null;
        _work.Dispose();
    }
}
