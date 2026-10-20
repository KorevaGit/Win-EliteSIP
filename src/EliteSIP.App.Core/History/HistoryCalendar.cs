using System.Globalization;
using EliteSIP.App.Panel;

namespace EliteSIP.App.History;

/// <summary>Клетка календаря — один день.</summary>
///
/// <remarks>
/// Дни соседних месяцев и дни за сроком хранения показываются погашенными, а не
/// прячутся: пустая клетка ломает сетку недели, и считать по ней даты
/// становится нельзя.
/// </remarks>
public sealed class CalendarDay(DateTime date, bool isInMonth, bool isAvailable, bool hasCalls, bool isSelected)
{
    public DateTime Date { get; } = date;

    public string Number { get; } = date.Day.ToString(CultureInfo.CurrentCulture);

    public bool IsInMonth { get; } = isInMonth;

    /// <summary>Можно ли выбрать: свой месяц и внутри срока хранения.</summary>
    public bool IsAvailable { get; } = isAvailable;

    /// <summary>Были ли в этот день звонки — точка под числом.</summary>
    ///
    /// <remarks>
    /// Отвечает на «работал ли я в этот день», а не «были ли в этот день
    /// пропущенные»: считается без фильтра, иначе календарь пустел бы при его
    /// переключении.
    /// </remarks>
    public bool HasCalls { get; } = hasCalls;

    public bool IsSelected { get; } = isSelected;
}

/// <summary>Неделя — строка сетки.</summary>
public sealed class CalendarWeek(IReadOnlyList<CalendarDay> days)
{
    public IReadOnlyList<CalendarDay> Days { get; } = days;
}

/// <summary>
/// Месяц сеткой семь на шесть: то, чем в истории выбирают один день.
/// </summary>
///
/// <remarks>
/// Границы — не «тридцать дней», а срок хранения: тридцать всего лишь
/// умолчание, и администратор ставит своё. Календарь, предлагающий день, записи
/// за который уже удалены, обещает то, чего нет.
///
/// <b>Неделя начинается с понедельника независимо от региона системы.</b>
/// Рабочая неделя колл-центра идёт с понедельника по пятницу, и в сетке,
/// начатой с воскресенья, она разорвана пополам. Цена названа прямо: у
/// человека с американским регионом календарь начнётся не там, где он привык.
/// Принято ради того, ради чего календарь и открывают, — найти рабочий день.
/// </remarks>
public sealed class HistoryCalendar : Observable
{
    private readonly Func<IReadOnlySet<DateTimeOffset>> _daysWithCalls;
    private readonly Func<int> _retentionDays;

    private DateTime _month = DateTime.Today;
    private DateTime? _selectedDay;
    private IReadOnlySet<DateTimeOffset> _marked = new HashSet<DateTimeOffset>();

    public HistoryCalendar(Func<IReadOnlySet<DateTimeOffset>> daysWithCalls, Func<int> retentionDays)
    {
        _daysWithCalls = daysWithCalls;
        _retentionDays = retentionDays;
        Rebuild();
    }

    /// <summary>Выбранный день. <c>null</c> — все дни.</summary>
    public DateTime? SelectedDay
    {
        get => _selectedDay;
        set
        {
            Set(ref _selectedDay, value);
            NotifyChanged(nameof(HasSelectedDay));
            NotifyChanged(nameof(SelectedDayTitle));
            Rebuild();
        }
    }

    public bool HasSelectedDay => _selectedDay is not null;

    /// <summary>Подпись на кнопке — та же дата, что в строках списка.</summary>
    public string SelectedDayTitle
        => _selectedDay?.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) ?? string.Empty;

    public string MonthTitle => _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Сокращения дней в том порядке, в каком стоят колонки сетки.</summary>
    ///
    /// <remarks>
    /// Берутся у культуры, а не переводом: они зависят от языка, и список из
    /// семи строк пришлось бы переводить руками. Порядок при этом наш — от
    /// понедельника, тем же сдвигом, каким строится сетка. Разъехавшись,
    /// подписи и числа встают под чужими днями недели, и это не заметно, пока
    /// не начнёшь считать по календарю рабочие дни.
    /// </remarks>
    public IReadOnlyList<string> WeekdayTitles { get; } = Weekdays();

    public IReadOnlyList<CalendarWeek> Weeks { get; private set; } = [];

    public bool CanShowPreviousMonth => CanShow(_month.AddMonths(-1));

    public bool CanShowNextMonth => CanShow(_month.AddMonths(1));

    /// <summary>Открывает календарь на месяце выбранного дня.</summary>
    ///
    /// <remarks>
    /// Каждое открытие возвращает календарь к выбранному дню: пролистав до
    /// прошлого месяца и закрыв окно, оператор ожидает открыть его там же, где
    /// стоит отбор, а не там, где случайно остановился.
    /// </remarks>
    public void Reset()
    {
        _month = _selectedDay ?? DateTime.Today;
        _marked = _daysWithCalls();
        Rebuild();
    }

    public void ShiftMonth(int months)
    {
        var shifted = _month.AddMonths(months);
        if (!CanShow(shifted))
        {
            return;
        }

        _month = shifted;
        Rebuild();
    }

    /// <summary>Раскладывает шесть недель по семь дней.</summary>
    ///
    /// <remarks>
    /// Шесть, а не пять: месяц из 31 дня, начавшийся в воскресенье, занимает
    /// именно шесть строк, и сетка не должна прыгать в высоте от месяца к
    /// месяцу.
    /// </remarks>
    private void Rebuild()
    {
        var first = new DateTime(_month.Year, _month.Month, 1);

        // Сдвиг до понедельника той недели, в которую попадает первое число.
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);

        var horizon = Horizon;
        var today = DateTime.Today;

        var weeks = new List<CalendarWeek>(6);
        for (var week = 0; week < 6; week++)
        {
            var days = new List<CalendarDay>(7);
            for (var day = 0; day < 7; day++)
            {
                var date = start.AddDays((week * 7) + day);
                var isInMonth = date.Month == _month.Month && date.Year == _month.Year;

                days.Add(new CalendarDay(
                    date,
                    isInMonth,
                    isAvailable: isInMonth && date >= horizon && date <= today,
                    hasCalls: _marked.Any(marked => marked.Date == date),
                    isSelected: _selectedDay == date));
            }

            weeks.Add(new CalendarWeek(days));
        }

        Weeks = weeks;

        foreach (var name in new[] { nameof(Weeks), nameof(MonthTitle), nameof(CanShowPreviousMonth), nameof(CanShowNextMonth) })
        {
            NotifyChanged(name);
        }
    }

    /// <summary>Дальше этого дня записей уже нет — их убрал срок хранения.</summary>
    private DateTime Horizon => DateTime.Today.AddDays(-Math.Clamp(_retentionDays(), 1, 3650));

    /// <summary>Месяц показывается, если в него попадает хоть один день срока.</summary>
    private bool CanShow(DateTime candidate)
    {
        var start = new DateTime(candidate.Year, candidate.Month, 1);
        return start.AddMonths(1) > Horizon && start <= DateTime.Today;
    }

    private static string[] Weekdays()
    {
        var names = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;

        // Первым понедельник: у .NET массив начинается с воскресенья всегда,
        // независимо от культуры, — значит сдвиг здесь постоянный и от региона
        // не зависит.
        return [.. names.Skip(1), names[0]];
    }
}
