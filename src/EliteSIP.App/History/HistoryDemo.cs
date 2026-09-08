using EliteSIP.CallHistory;

namespace EliteSIP.App.History;

/// <summary>
/// Кладёт в показательное хранилище десяток звонков — чтобы раскладку окна
/// можно было сверить снимком, не поднимая АТС.
/// </summary>
///
/// <remarks>
/// Пишет настоящие записи настоящим хранилищем, а не подсовывает окну готовый
/// список: проверять надо в том числе то, как окно читает базу — сортировку,
/// разбивку по дням и отбор фильтром.
///
/// Работает только с файлом <c>history-demo.db</c>, который заводится ключом
/// <c>--demo</c>. Боевая история отдельно, и показательные звонки в ней не
/// появляются никогда.
/// </remarks>
internal static class HistoryDemo
{
    public static void Seed(CallHistoryStore store, Guid profileId)
    {
        // Уже заполнено — второй раз не пишем: иначе каждый запуск удваивает
        // список, и снимки перестают быть сравнимыми между собой.
        if (store.Records(new HistoryScope(profileId), limit: 1).Count > 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;

        // Набор подобран так, чтобы в окне встретились все четыре значка и все
        // три дня: сегодня, вчера и позавчера — то есть заголовок датой.
        Add(store, profileId, "712", "Отдел продаж", now.AddMinutes(-12), CallDirection.Incoming, answered: true, seconds: 214);
        Add(store, profileId, "600", null, now.AddMinutes(-48), CallDirection.Outgoing, answered: true, seconds: 65);
        Add(store, profileId, "89261234567", null, now.AddHours(-2), CallDirection.Incoming, answered: false, outcome: CallOutcome.Missed);
        Add(store, profileId, "101", "Юрист", now.AddHours(-5), CallDirection.Outgoing, answered: false, outcome: CallOutcome.Busy);

        Add(store, profileId, "712", "Отдел продаж", now.AddDays(-1).AddHours(-1), CallDirection.Incoming, answered: true, seconds: 1832, transferred: true);
        Add(store, profileId, "204", null, now.AddDays(-1).AddHours(-3), CallDirection.Outgoing, answered: true, seconds: 43, conference: true);
        Add(store, profileId, "89031112233", null, now.AddDays(-1).AddHours(-6), CallDirection.Incoming, answered: false, outcome: CallOutcome.Missed);

        Add(store, profileId, "305", "Бухгалтерия", now.AddDays(-2).AddHours(-2), CallDirection.Outgoing, answered: false, outcome: CallOutcome.NoAnswer);
        Add(store, profileId, "9999", null, now.AddDays(-2).AddHours(-4), CallDirection.Outgoing, answered: false, outcome: CallOutcome.UnknownNumber);
        Add(store, profileId, "712", "Отдел продаж", now.AddDays(-2).AddHours(-7), CallDirection.Incoming, answered: true, seconds: 5);

        // Запись идёт своим потоком, а окно читает сразу за этим вызовом:
        // без ожидания первый снимок застаёт пустой список.
        store.Flush();
    }

    private static void Add(
        CallHistoryStore store,
        Guid profileId,
        string number,
        string? displayName,
        DateTimeOffset startedAt,
        CallDirection direction,
        bool answered,
        int seconds = 0,
        CallOutcome? outcome = null,
        bool transferred = false,
        bool conference = false)
    {
        var record = new CallRecord
        {
            CallId = Guid.NewGuid().ToString("N"),
            Direction = direction,
            Number = number,
            DisplayName = displayName,
            ProfileId = profileId,
            StartedAt = startedAt,
        };

        store.Begin(record);

        if (answered)
        {
            store.MarkAnswered(record.Id, startedAt.AddSeconds(2));
        }

        if (transferred)
        {
            store.MarkTransferred(record.Id);
        }

        if (conference)
        {
            store.MarkConference(record.Id);
        }

        store.Finish(
            record.Id,
            reason: "показательная запись",
            outcome: outcome,
            at: startedAt.AddSeconds(2 + seconds));
    }
}
