using EliteSIP.CallHistory;
using Microsoft.Data.Sqlite;

namespace EliteSIP.CallHistory.Tests;

/// <summary>
/// История звонков: запись по ходу, выборка, срок хранения и порча файла.
///
/// Проверяется здесь то, что на разработке не всплывает никогда. Историю
/// открывают через полгода после установки, на машине, которую пару раз
/// выключили из розетки посреди разговора, — и именно тогда выясняется, что
/// записи не закрылись, база не читается, а открытие панели занимает секунды.
/// </summary>
public sealed class CallHistoryStoreTests : IDisposable
{
    /// <summary>
    /// Профиль, которому принадлежат записи почти всех проверок.
    ///
    /// Свой у каждого экземпляра: xUnit создаёт класс заново под каждую
    /// проверку, и пересечься они не могут даже теоретически.
    /// </summary>
    private readonly Guid _profile = Guid.NewGuid();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "elitesip-history-test-" + Guid.NewGuid().ToString("N"));

    private HistoryScope Scope => new(_profile);

    private CallHistoryStore.Settings MakeSettings(int days = 30)
        => new(Path.Combine(_directory, "call-history.sqlite"), days);

    private CallRecord Record(
        CallDirection direction = CallDirection.Outgoing,
        string number = "601",
        DateTimeOffset? startedAt = null)
        => new()
        {
            CallId = Guid.NewGuid().ToString(),
            Direction = direction,
            Number = number,
            SipLogin = "SIP/" + number,
            ProfileId = _profile,
            ProfileLabel = "Боевой",
            StartedAt = startedAt ?? DateTimeOffset.Now,
        };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // MARK: - Запись по ходу звонка

    [Fact]
    public void Звонок_пишется_на_первом_гудке_и_дописывается_ответом_и_концом()
    {
        using CallHistoryStore store = new(MakeSettings());
        Assert.IsType<HistoryOpenOutcome.Ready>(store.OpenOutcome);

        CallRecord call = Record();
        store.Begin(call);
        store.Flush();

        // Открытая запись видна ещё до конца разговора — ради этого запись и
        // заводится в начале.
        Assert.Null(store.Records(Scope)[0].EndedAt);

        DateTimeOffset answered = call.StartedAt.AddSeconds(4);
        store.MarkAnswered(call.Id, answered);
        store.Finish(call.Id, "Завершён", at: answered.AddSeconds(90));
        store.Flush();

        CallRecord stored = store.Records(Scope)[0];
        Assert.Equal(call.Id, stored.Id);
        Assert.Equal("601", stored.Number);
        Assert.Equal("SIP/601", stored.SipLogin);
        Assert.Equal("Боевой", stored.ProfileLabel);
        Assert.True(stored.IsAnswered);
        Assert.Equal(TimeSpan.FromSeconds(90), stored.Duration);
        Assert.Equal("Завершён", stored.EndReason);
    }

    [Fact]
    public void Причину_завершения_назначает_первое_событие_а_не_последнее()
    {
        using CallHistoryStore store = new(MakeSettings());
        CallRecord call = Record();
        store.Begin(call);

        // Успешный перевод завершает нашу ногу сразу, и обычное «Завершён»
        // приезжает следом по той же линии. Затирать им единственное
        // подтверждение оператору нельзя.
        store.Finish(call.Id, "Переведён на 601");
        store.Finish(call.Id, "Завершён");
        store.Flush();

        Assert.Equal("Переведён на 601", store.Records(Scope)[0].EndReason);
    }

    [Fact]
    public void Время_ответа_не_сдвигается_повторным_событием()
    {
        using CallHistoryStore store = new(MakeSettings());
        CallRecord call = Record();
        DateTimeOffset first = DateTimeOffset.FromUnixTimeSeconds(1_000).ToLocalTime();

        store.Begin(call);
        store.MarkAnswered(call.Id, first);
        store.MarkAnswered(call.Id, first.AddSeconds(60));
        store.Flush();

        Assert.Equal(first, store.Records(Scope)[0].AnsweredAt);
    }

    [Fact]
    public void Перевод_и_конференция_остаются_в_записи()
    {
        using CallHistoryStore store = new(MakeSettings());
        CallRecord call = Record();

        store.Begin(call);
        store.MarkTransferred(call.Id);
        store.MarkConference(call.Id);
        store.Flush();

        CallRecord stored = store.Records(Scope)[0];
        Assert.True(stored.WasTransferred);
        Assert.True(stored.WasConference);
    }

    // MARK: - Приёмка: падение приложения

    [Fact]
    public void История_переживает_принудительное_завершение_приложения()
    {
        CallHistoryStore.Settings settings = MakeSettings();
        CallRecord call = Record();

        using (CallHistoryStore store = new(settings))
        {
            // Записи закрыть некому: так выглядит завершение процесса посреди
            // разговора.
            store.Begin(call);
            store.MarkAnswered(call.Id);
            store.Flush();
        }

        using CallHistoryStore reopened = new(settings);
        CallRecord stored = reopened.Records(Scope)[0];

        Assert.Equal(call.Id, stored.Id);
        Assert.Equal(CallHistoryStore.InterruptedReason, stored.EndReason);
        Assert.Equal(stored.StartedAt, stored.EndedAt);
    }

    // MARK: - Приёмка: срок хранения

    [Fact]
    public void Удаление_по_сроку_работает()
    {
        using CallHistoryStore store = new(MakeSettings(days: 30));
        DateTimeOffset now = DateTimeOffset.Now;

        store.Begin(Record(number: "601", startedAt: now.AddDays(-29)));
        store.Begin(Record(number: "602", startedAt: now.AddDays(-31)));
        store.Flush();

        Assert.Equal(1, store.Prune(now));
        Assert.Equal(["601"], store.Records(Scope).Select(record => record.Number));
    }

    [Fact]
    public void Срок_применяется_и_при_открытии_базы_а_не_только_по_кнопке()
    {
        CallHistoryStore.Settings settings = MakeSettings(days: 1);

        using (CallHistoryStore store = new(settings))
        {
            store.Begin(Record(startedAt: DateTimeOffset.Now.AddDays(-10)));
            store.Flush();
            Assert.Single(store.Records(Scope));
        }

        using CallHistoryStore reopened = new(settings);
        Assert.Empty(reopened.Records(Scope));
    }

    [Fact]
    public void Нулевой_срок_не_означает_удалить_всё()
    {
        using CallHistoryStore store = new(MakeSettings(days: 0));

        store.Begin(Record(startedAt: DateTimeOffset.Now.AddHours(-1)));
        store.Flush();
        store.Prune();

        Assert.Single(store.Records(Scope));
    }

    // MARK: - Выборка

    [Fact]
    public void Фильтр_по_направлению_отбирает_то_что_обещает()
    {
        using CallHistoryStore store = new(MakeSettings());
        DateTimeOffset now = DateTimeOffset.Now;

        CallRecord answeredIncoming = Record(CallDirection.Incoming, "701", now);
        store.Begin(answeredIncoming);
        store.MarkAnswered(answeredIncoming.Id);

        store.Begin(Record(CallDirection.Incoming, "702", now.AddSeconds(-1)));
        store.Begin(Record(CallDirection.Outgoing, "601", now.AddSeconds(-2)));
        store.Flush();

        Assert.Equal(3, store.Records(Scope).Count);
        Assert.Equal(
            ["701", "702"],
            store.Records(Scope, HistoryFilter.Incoming).Select(record => record.Number));
        Assert.Equal(
            ["601"],
            store.Records(Scope, HistoryFilter.Outgoing).Select(record => record.Number));
        Assert.Equal(
            ["702"],
            store.Records(Scope, HistoryFilter.Missed).Select(record => record.Number));
        Assert.Equal(1, store.Count(Scope, HistoryFilter.Missed));
    }

    [Fact]
    public void Неотвеченный_исходящий_пропущенным_не_считается()
    {
        Assert.False(Record(CallDirection.Outgoing).IsMissed);
        Assert.True(Record(CallDirection.Incoming).IsMissed);
    }

    [Fact]
    public void Записи_возвращаются_новыми_первыми()
    {
        using CallHistoryStore store = new(MakeSettings());
        DateTimeOffset now = DateTimeOffset.Now;

        store.Begin(Record(number: "старый", startedAt: now.AddMinutes(-10)));
        store.Begin(Record(number: "новый", startedAt: now));
        store.Flush();

        Assert.Equal(["новый", "старый"], store.Records(Scope).Select(record => record.Number));
    }

    // MARK: - Приёмка: десять тысяч записей

    [Fact]
    public void Десять_тысяч_записей_не_замедляют_открытие_панели()
    {
        using CallHistoryStore store = new(MakeSettings(days: 3650));
        DateTimeOffset now = DateTimeOffset.Now;

        for (int index = 0; index < 10_000; index++)
        {
            store.Begin(Record(
                index % 2 == 0 ? CallDirection.Incoming : CallDirection.Outgoing,
                (600 + (index % 40)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                now.AddSeconds(-index)));
        }

        store.Flush();
        Assert.Equal(10_000, store.TotalCount());

        // Панель открывается одной выборкой первой страницы. Порог намеренно
        // щедрый: цель проверки — поймать чтение всей таблицы, а не измерить
        // машину сборки. Полный проход по десяти тысячам с разбором строк не
        // уложился бы и в секунду.
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<CallRecord> page = store.Records(Scope, HistoryFilter.Missed, limit: 200);
        watch.Stop();

        Assert.Equal(200, page.Count);
        Assert.True(
            watch.Elapsed < TimeSpan.FromMilliseconds(200),
            $"выборка обязана идти по индексу, а не читать таблицу целиком: {watch.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void Страницы_не_пересекаются_и_продолжают_друг_друга()
    {
        using CallHistoryStore store = new(MakeSettings());
        DateTimeOffset now = DateTimeOffset.Now;

        for (int index = 0; index < 10; index++)
        {
            store.Begin(Record(
                number: index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                startedAt: now.AddSeconds(-index)));
        }

        store.Flush();

        Assert.Equal(
            ["0", "1", "2", "3"],
            store.Records(Scope, limit: 4).Select(record => record.Number));
        Assert.Equal(
            ["4", "5", "6", "7"],
            store.Records(Scope, limit: 4, offset: 4).Select(record => record.Number));
    }

    // MARK: - Порча файла

    [Fact]
    public void Испорченная_база_отставляется_в_сторону_а_работа_продолжается()
    {
        CallHistoryStore.Settings settings = MakeSettings();

        using (CallHistoryStore store = new(settings))
        {
            store.Begin(Record());
            store.Flush();
        }

        // Затираем заголовок файла — так выглядит порча, которую SQLite замечает
        // сразу и на которой обычный клиент падает при первом запросе.
        using (FileStream file = File.OpenWrite(settings.FilePath))
        {
            file.Write(new byte[512]. Select(_ => (byte)0x41).ToArray());
        }

        using CallHistoryStore reopened = new(settings);

        HistoryOpenOutcome.ReplacedDamaged replaced =
            Assert.IsType<HistoryOpenOutcome.ReplacedDamaged>(reopened.OpenOutcome);
        Assert.True(
            File.Exists(replaced.DamagedPath),
            "файл с номерами лидов не стирают походя — его отставляют в сторону");

        // И самое главное: история продолжает работать.
        reopened.Begin(Record(number: "603"));
        reopened.Flush();
        Assert.Equal(["603"], reopened.Records(Scope).Select(record => record.Number));
    }

    // MARK: - Задел под EliteDash

    [Fact]
    public void Псевдоним_переопределяется_не_затирая_номер_и_логин()
    {
        using CallHistoryStore store = new(MakeSettings());

        store.Begin(Record(number: "79001234567"));
        store.Flush();

        store.OverrideDisplayName("Иванов, ООО «Ромашка»", "79001234567");
        store.Flush();

        CallRecord stored = store.Records(Scope)[0];
        Assert.Equal("Иванов, ООО «Ромашка»", stored.DisplayName);
        Assert.Equal("79001234567", stored.Number);
        Assert.Equal("SIP/79001234567", stored.SipLogin);
        Assert.Equal("Иванов, ООО «Ромашка»", stored.Title);
    }

    [Fact]
    public void Идентификатор_со_стороны_сервера_дописывается_в_готовую_запись()
    {
        using CallHistoryStore store = new(MakeSettings());
        CallRecord call = Record();

        store.Begin(call);
        store.AttachServerCallId("1754212800.42", call.Id);
        store.Flush();

        Assert.Equal("1754212800.42", store.Records(Scope)[0].ServerCallId);
    }

    // MARK: - Граница профиля

    [Fact]
    public void Чужой_профиль_не_виден_ни_в_выборке_ни_в_счёте()
    {
        using CallHistoryStore store = new(MakeSettings());

        store.Begin(Record(number: "601"));
        store.Begin(new CallRecord
        {
            CallId = Guid.NewGuid().ToString(),
            Direction = CallDirection.Outgoing,
            Number = "чужой",
            ProfileId = Guid.NewGuid(),
            ProfileLabel = "Лаба",
        });
        store.Flush();

        Assert.Equal(["601"], store.Records(Scope).Select(record => record.Number));
        Assert.Equal(1, store.Count(Scope));

        // Обе записи на диске есть — граница проходит по выборке, а не по
        // записи: администратор считает всё, что накоплено на машине.
        Assert.Equal(2, store.TotalCount());
    }

    [Fact]
    public void Запись_без_профиля_не_видна_никому()
    {
        using CallHistoryStore store = new(MakeSettings());

        store.Begin(new CallRecord
        {
            CallId = Guid.NewGuid().ToString(),
            Direction = CallDirection.Outgoing,
            Number = "ничей",
            ProfileId = null,
        });
        store.Flush();

        Assert.Empty(store.Records(Scope));
        Assert.Empty(store.Records(new HistoryScope(null)));
        Assert.Equal(1, store.TotalCount());
    }

    [Fact]
    public void Удаление_профиля_уносит_его_историю_и_не_трогает_чужую()
    {
        using CallHistoryStore store = new(MakeSettings());
        Guid other = Guid.NewGuid();

        store.Begin(Record(number: "601"));
        store.Begin(Record(number: "602"));
        store.Begin(new CallRecord
        {
            CallId = Guid.NewGuid().ToString(),
            Direction = CallDirection.Outgoing,
            Number = "чужой",
            ProfileId = other,
        });
        store.Flush();

        Assert.Equal(2, store.DeleteHistory(_profile));
        Assert.Empty(store.Records(Scope));
        Assert.Equal(1, store.Count(new HistoryScope(other)));
    }

    /// <summary>
    /// Проверки в оригинале нет: <c>deleteAll</c> там появился этапом позже
    /// тестов. Обещание у него сильное — «стереть можно, бесследно нет», — и
    /// число, которое возвращается для журнала, обязано быть верным.
    /// </summary>
    [Fact]
    public void Полное_стирание_возвращает_число_для_журнала()
    {
        using CallHistoryStore store = new(MakeSettings());

        store.Begin(Record(number: "601"));
        store.Begin(Record(number: "602"));
        store.Flush();

        Assert.Equal(2, store.DeleteAll());
        Assert.Equal(0, store.TotalCount());
    }

    // MARK: - Отбор по дню

    [Fact]
    public void Отбор_по_дню_берёт_местные_сутки_целиком()
    {
        using CallHistoryStore store = new(MakeSettings(days: 3650));
        DateTimeOffset today = StartOfLocalDay(DateTimeOffset.Now);
        DateTimeOffset yesterday = StartOfLocalDay(DateTimeOffset.Now.AddDays(-1));

        // Первая и последняя минуты суток: границы должны попадать внутрь, а не
        // срезаться — звонок в 00:03 принадлежит своему дню, а не прошлому.
        store.Begin(Record(number: "начало", startedAt: today.AddMinutes(1)));
        store.Begin(Record(number: "конец", startedAt: today.AddDays(1).AddMinutes(-1)));
        store.Begin(Record(number: "вчера", startedAt: yesterday.AddHours(12)));
        store.Flush();

        HistoryScope day = new(_profile, today);
        Assert.Equal(
            new HashSet<string> { "начало", "конец" },
            store.Records(day).Select(record => record.Number).ToHashSet());
        Assert.Equal(2, store.Count(day));
        Assert.Equal(1, store.Count(new HistoryScope(_profile, yesterday)));
    }

    [Fact]
    public void Дни_со_звонками_перечисляются_началами_местных_суток()
    {
        using CallHistoryStore store = new(MakeSettings(days: 3650));
        DateTimeOffset today = StartOfLocalDay(DateTimeOffset.Now);
        DateTimeOffset older = StartOfLocalDay(DateTimeOffset.Now.AddDays(-5));

        store.Begin(Record(startedAt: today.AddHours(9)));
        store.Begin(Record(startedAt: today.AddHours(18)));
        store.Begin(Record(startedAt: older.AddHours(11)));
        store.Flush();

        // Два звонка одного дня дают одну точку, а не две.
        Assert.Equal(new HashSet<DateTimeOffset> { today, older }, store.DaysWithCalls(Scope));
    }

    [Fact]
    public void Дни_чужого_профиля_в_календарь_не_попадают()
    {
        using CallHistoryStore store = new(MakeSettings(days: 3650));
        DateTimeOffset today = StartOfLocalDay(DateTimeOffset.Now);

        store.Begin(new CallRecord
        {
            CallId = Guid.NewGuid().ToString(),
            Direction = CallDirection.Outgoing,
            Number = "чужой",
            ProfileId = Guid.NewGuid(),
            StartedAt = today.AddHours(9),
        });
        store.Flush();

        Assert.Empty(store.DaysWithCalls(Scope));
    }

    [Fact]
    public void Дни_за_сроком_хранения_в_календарь_не_попадают()
    {
        using CallHistoryStore store = new(MakeSettings(days: 7));
        DateTimeOffset today = StartOfLocalDay(DateTimeOffset.Now);

        // Запись старше срока могла бы дожить до открытия календаря: уборка идёт
        // при открытии базы и раз в сутки, а не в момент запроса.
        store.Begin(Record(startedAt: StartOfLocalDay(DateTimeOffset.Now.AddDays(-30)).AddHours(9)));
        store.Begin(Record(startedAt: today.AddHours(9)));
        store.Flush();

        Assert.Equal(new HashSet<DateTimeOffset> { today }, store.DaysWithCalls(Scope));
    }

    // MARK: - Подсказки словаря очередей

    /// <summary>
    /// Длинные номера — это номера лидов, и в подсказке словаря очередей им не
    /// место: она вывешена в окне настроек.
    /// </summary>
    [Fact]
    public void В_подсказку_попадают_короткие_номера_и_только_входящие()
    {
        using CallHistoryStore store = new(MakeSettings());
        DateTimeOffset now = DateTimeOffset.Now;

        store.Begin(Record(CallDirection.Incoming, "7001", now));
        store.Begin(Record(CallDirection.Incoming, "7001", now.AddSeconds(-1)));
        store.Begin(Record(CallDirection.Incoming, "79001234567", now.AddSeconds(-2)));
        store.Begin(Record(CallDirection.Outgoing, "601", now.AddSeconds(-3)));
        store.Flush();

        IReadOnlyList<NumberSighting> sightings = store.IncomingNumbers(maximumDigits: 5);

        NumberSighting only = Assert.Single(sightings);
        Assert.Equal("7001", only.Number);
        Assert.Equal(2, only.Count);
    }

    // MARK: - Исход

    [Fact]
    public void Код_исхода_записывается_вместе_с_причиной()
    {
        using CallHistoryStore store = new(MakeSettings());
        CallRecord call = Record();

        store.Begin(call);
        store.Finish(call.Id, "занято", CallOutcome.Busy);
        store.Flush();

        CallRecord stored = store.Records(Scope)[0];
        Assert.Equal(CallOutcome.Busy, stored.OutcomeCode);
        Assert.Equal(CallOutcome.Busy, stored.Outcome);
    }

    [Fact]
    public void Состоявшийся_разговор_и_пропущенный_не_спрашивают_у_кода()
    {
        using CallHistoryStore store = new(MakeSettings());

        // Разговор состоялся: даже если код по недосмотру сказал бы другое,
        // ответ даёт время ответа — иначе история разошлась бы сама с собой.
        CallRecord talked = Record(number: "601");
        store.Begin(talked);
        store.MarkAnswered(talked.Id);
        store.Finish(talked.Id, "Завершён", CallOutcome.Failed);

        // Входящий без ответа — пропущенный, и это то же самое условие, по
        // которому отбирает фильтр «Пропущенные».
        CallRecord missed = Record(CallDirection.Incoming, "701");
        store.Begin(missed);
        store.Finish(missed.Id, "отклонён", CallOutcome.Declined);

        // Исходящий без ответа и без кода — «не ответил», а не пустота.
        CallRecord old = Record(number: "602");
        store.Begin(old);
        store.Finish(old.Id, "Завершён");
        store.Flush();

        Dictionary<string, CallOutcome> stored = store.Records(Scope)
            .ToDictionary(record => record.Number, record => record.Outcome);

        Assert.Equal(CallOutcome.Completed, stored["601"]);
        Assert.Equal(CallOutcome.Missed, stored["701"]);
        Assert.Equal(CallOutcome.NoAnswer, stored["602"]);
    }

    [Fact]
    public void Все_шесть_слов_исхода_различны_а_у_состоявшегося_слова_нет()
    {
        List<string> titles = Enum.GetValues<CallOutcome>()
            .Select(outcome => outcome.Title())
            .OfType<string>()
            .ToList();

        Assert.Null(CallOutcome.Completed.Title());
        Assert.Equal(6, titles.Count);
        Assert.Equal(6, titles.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Коды_ответа_SIP_переводятся_в_слова()
    {
        Assert.Equal(CallOutcome.Busy, CallOutcomes.ForFailure(486));
        Assert.Equal(CallOutcome.Busy, CallOutcomes.ForFailure(600));
        Assert.Equal(CallOutcome.UnknownNumber, CallOutcomes.ForFailure(404));
        Assert.Equal(CallOutcome.Declined, CallOutcomes.ForFailure(603));
        Assert.Equal(CallOutcome.Declined, CallOutcomes.ForFailure(403));
        Assert.Equal(CallOutcome.NoAnswer, CallOutcomes.ForFailure(408));
        Assert.Equal(CallOutcome.NoAnswer, CallOutcomes.ForFailure(480));
        Assert.Equal(CallOutcome.NoAnswer, CallOutcomes.ForFailure(487));
        Assert.Equal(CallOutcome.Failed, CallOutcomes.ForFailure(503));
    }

    // MARK: - Миграция

    [Fact]
    public void База_без_колонки_исхода_открывается_а_не_заводится_заново()
    {
        CallHistoryStore.Settings settings = MakeSettings();
        Directory.CreateDirectory(_directory);

        // Схема до появления `outcome_code`: та же таблица без двух колонок.
        using (SqliteConnection old = new(new SqliteConnectionStringBuilder
        {
            DataSource = settings.FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ConnectionString))
        {
            old.Open();
            using (SqliteCommand create = old.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE calls (
                        id TEXT PRIMARY KEY NOT NULL, call_id TEXT NOT NULL, server_call_id TEXT,
                        direction INTEGER NOT NULL, role INTEGER NOT NULL, number TEXT NOT NULL,
                        sip_login TEXT, display_name TEXT, profile_id TEXT, profile_label TEXT,
                        started_at REAL NOT NULL, answered_at REAL, ended_at REAL, end_reason TEXT,
                        was_transferred INTEGER NOT NULL DEFAULT 0,
                        was_conference INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                create.ExecuteNonQuery();
            }

            using SqliteCommand insert = old.CreateCommand();
            insert.CommandText = """
                INSERT INTO calls (id, call_id, direction, role, number, profile_id, started_at,
                                   answered_at, ended_at, end_reason, was_transferred, was_conference)
                VALUES ($id, 'старый', 1, 0, '601', $profile, $started, NULL, $ended, 'занято', 0, 0);
                """;
            double seconds = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("$profile", _profile.ToString());
            insert.Parameters.AddWithValue("$started", seconds);
            insert.Parameters.AddWithValue("$ended", seconds);
            insert.ExecuteNonQuery();
        }

        using CallHistoryStore store = new(settings);
        Assert.IsType<HistoryOpenOutcome.Ready>(store.OpenOutcome);

        CallRecord stored = store.Records(Scope)[0];
        Assert.Equal("601", stored.Number);
        Assert.Null(stored.OutcomeCode);
        Assert.Equal(CallOutcome.NoAnswer, stored.Outcome);

        // И новая запись в мигрированную базу пишется уже с кодом.
        CallRecord fresh = Record(number: "602");
        store.Begin(fresh);
        store.Finish(fresh.Id, "нет номера", CallOutcome.UnknownNumber);
        store.Flush();

        Assert.Equal(
            CallOutcome.UnknownNumber,
            store.Records(Scope).First(record => record.Number == "602").OutcomeCode);
    }

    private static DateTimeOffset StartOfLocalDay(DateTimeOffset moment)
    {
        DateTime day = moment.LocalDateTime.Date;
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }
}
