using System.Collections.ObjectModel;
using System.Globalization;
using EliteSIP.App.Panel;
using EliteSIP.CallHistory;

namespace EliteSIP.App.History;

/// <summary>Кнопка фильтра: значок, подпись и то, что она отбирает.</summary>
public sealed record HistoryFilterItem(
    HistoryFilter Filter,
    string Title,
    bool IsIncoming,
    bool IsCompleted,
    CallOutcome Outcome);

/// <summary>Группа записей одного дня.</summary>
///
/// <remarks>
/// День — то, по чему в списке ориентируются: он единственная точка опоры при
/// прокрутке на три недели назад. Поэтому заголовок группы набран кеглем
/// раздела, а не мелкой серой подписью.
/// </remarks>
public sealed class HistoryDay(string title)
{
    public string Title { get; } = title;

    public ObservableCollection<CallRecord> Records { get; } = [];
}

/// <summary>Состояние окна истории.</summary>
///
/// <remarks>
/// <b>Окно ограничено активным профилем жёстко.</b> «Всех профилей» здесь нет:
/// если профили — это разные люди за одной машиной, то граница между их
/// звонками не удобство, а граница персональных данных.
///
/// Кнопок удаления в окне нет, и это решение, а не недоделка. История нужна в
/// том числе как свидетельство при разборе жалобы, а свидетельство, которое
/// может убрать заинтересованная сторона, свидетельством не является. Записи
/// уходят по сроку хранения либо вместе с профилем — и то и другое за паролем
/// администратора.
/// </remarks>
public sealed class CallHistoryViewModel : Observable
{
    /// <summary>Сколько записей забирается за раз.</summary>
    ///
    /// <remarks>
    /// Двести — та же страница, что в оригинале. Дальше окно догружает
    /// следующие, когда оператор долистал до низа: без догрузки всё, что старше
    /// первой страницы, было бы недостижимо ни одним действием.
    /// </remarks>
    private const int PageSize = 200;

    private readonly CallHistoryStore _store;
    private readonly Guid _profileId;
    private HistoryFilter _filter = HistoryFilter.All;
    private int _loaded;
    private bool _hasMore;

    public CallHistoryViewModel(
        CallHistoryStore store,
        Guid profileId,
        string profileTitle,
        Func<int> retentionDays)
    {
        _store = store;
        _profileId = profileId;
        ProfileTitle = profileTitle;
        OwnNumber = profileTitle;

        Calendar = new HistoryCalendar(
            () => _store.DaysWithCalls(new HistoryScope(_profileId)),
            retentionDays);

        // Выбор дня — такой же отбор, как фильтр: список читается заново и
        // начинается сверху.
        Calendar.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(HistoryCalendar.SelectedDay))
            {
                Reload();
            }
        };

        Filters =
        [
            // Значок на кнопке — тот же, что в строке списка, а не голая
            // стрелка: кнопка обязана показывать ровно ту фигуру, которую
            // человек будет искать глазами в списке, иначе она учит второму
            // языку вместо первого.
            // Подписи фильтров спрашиваются у пакета, а не заводятся здесь
            // своими: слова «Пропущенные» и «пропущен» — про одно и то же, и
            // жить им положено в одном каталоге.
            new(HistoryFilter.All, HistoryFilter.All.Title(), true, true, CallOutcome.Completed),
            new(HistoryFilter.Incoming, HistoryFilter.Incoming.Title(), true, true, CallOutcome.Completed),
            new(HistoryFilter.Outgoing, HistoryFilter.Outgoing.Title(), false, true, CallOutcome.Completed),
            new(HistoryFilter.Missed, HistoryFilter.Missed.Title(), true, false, CallOutcome.Missed),
        ];

        Reload();
    }

    /// <summary>Профиль в заголовке окна: список целиком принадлежит ему.</summary>
    ///
    /// <remarks>
    /// В строках профиля больше нет — повторять метку двести раз незачем.
    /// </remarks>
    public string ProfileTitle { get; }

    /// <summary>
    /// Свой добавочный — по нему вызов по сделке узнаётся в строке истории.
    /// Метка профиля и есть добавочный: профиль называется номером.
    /// </summary>
    public string OwnNumber { get; }

    public IReadOnlyList<HistoryFilterItem> Filters { get; }

    public ObservableCollection<HistoryDay> Days { get; } = [];

    public HistoryFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter == value)
            {
                return;
            }

            Set(ref _filter, value);

            // Новый отбор — новый список, и он обязан начинаться сверху.
            Reload();
        }
    }

    /// <summary>Есть ли что догружать.</summary>
    public bool HasMore
    {
        get => _hasMore;
        private set => Set(ref _hasMore, value);
    }

    public bool IsEmpty => Days.Count == 0;

    /// <summary>Записи есть — значит, снимок делать с чего.</summary>
    public bool HasRecords => !IsEmpty;

    public HistoryCalendar Calendar { get; }

    private HistoryScope Scope => new(
        _profileId,
        Calendar.SelectedDay is { } day ? new DateTimeOffset(day) : null);

    /// <summary>Читает первую страницу заново.</summary>
    public void Reload()
    {
        Days.Clear();
        _loaded = 0;
        Append(_store.Records(Scope, _filter, PageSize));
        NotifyChanged(nameof(IsEmpty));
        NotifyChanged(nameof(HasRecords));
    }

    /// <summary>Догружает следующую страницу.</summary>
    ///
    /// <remarks>
    /// Вешается на появление последней строки, а не на положение прокрутки:
    /// строка, доехавшая до глаз, — тот же сигнал, и он не зависит от того,
    /// сколько именно точек в списке.
    /// </remarks>
    public void LoadMore()
    {
        if (!HasMore)
        {
            return;
        }

        Append(_store.Records(Scope, _filter, PageSize, _loaded));
    }

    private void Append(IReadOnlyList<CallRecord> records)
    {
        foreach (var record in records)
        {
            var title = DayTitle(record.StartedAt);

            // Группы идут подряд и в порядке записей: список отсортирован по
            // времени вниз, значит новый день начинается ровно там, где
            // сменился заголовок. Искать группу по всему списку не нужно — и
            // нельзя: два одинаковых заголовка в разных местах означали бы
            // сбитую сортировку, и склеивать их было бы враньём.
            if (Days.Count == 0 || Days[^1].Title != title)
            {
                Days.Add(new HistoryDay(title));
            }

            Days[^1].Records.Add(record);
        }

        _loaded += records.Count;

        // Страница пришла неполной — значит, она последняя. Полная страница ещё
        // ничего не обещает, и лишний запрос на пустоту дешевле, чем строка,
        // которую нечем достать.
        HasMore = records.Count == PageSize;
        NotifyChanged(nameof(IsEmpty));
        NotifyChanged(nameof(HasRecords));
    }

    /// <summary>«Сегодня» и «Вчера» словами, остальное датой.</summary>
    ///
    /// <remarks>
    /// Два дня, а не больше: «позавчера» человек уже переводит в дату сам, а
    /// «в среду» на третьей неделе истории означает четыре разных среды.
    /// </remarks>
    private static string DayTitle(DateTimeOffset moment)
    {
        var day = moment.Date;
        var today = DateTime.Today;

        if (day == today)
        {
            return Resources.Strings.Get("HistoryToday");
        }

        return day == today.AddDays(-1)
            ? Resources.Strings.Get("HistoryYesterday")
            : day.ToString("d MMMM", CultureInfo.CurrentCulture);
    }
}
