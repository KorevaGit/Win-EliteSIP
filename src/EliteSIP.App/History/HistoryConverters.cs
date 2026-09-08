using System.Globalization;
using System.Text;
using System.Windows.Data;
using EliteSIP.App.Resources;
using EliteSIP.CallHistory;

namespace EliteSIP.App.History;

/// <summary>Правая колонка строки: длительность или слово исхода.</summary>
///
/// <remarks>
/// Длительность у состоявшегося разговора, слово — у всех остальных. Причины
/// отказа здесь нет: полностью она не помещается, а обрезанная («отказ 503
/// service…») не отвечает ни на один вопрос. Шести слов оператору хватает,
/// чтобы решить, что делать дальше, а подробности лежат в журнале.
/// </remarks>
public sealed class OutcomeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CallRecord record)
        {
            return string.Empty;
        }

        if (record.Duration is { } duration)
        {
            // Часы появляются только когда они есть: «00:05:12» в списке, где
            // все звонки короче часа, — это два лишних знака в каждой строке.
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss", culture)
                : duration.ToString(@"mm\:ss", culture);
        }

        return record.Outcome.Title() ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Время и дата в строке: <c>15:57 06.08.2026</c>.</summary>
///
/// <remarks>
/// Полная дата в каждой строке — при том, что день написан и в заголовке
/// группы. Повтор принят сознательно: строку показывают коллеге и
/// пересказывают в поддержку, и она обязана быть полной сама по себе.
/// </remarks>
public sealed class CallStampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTimeOffset moment
            ? moment.ToLocalTime().ToString("HH:mm dd.MM.yyyy", culture)
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Нижняя строка: сам номер и пометки перевода и конференции.</summary>
///
/// <remarks>
/// Номер стоит внизу всегда, даже когда он же написан наверху. Повтор выбран
/// сознательно: имён почти нет — сервер кладёт в <c>From</c> тот же номер, — и
/// без него нижняя строка у большинства звонков пустая. Строка при этом
/// выглядит наполовину отвалившейся, а высоту всё равно занимает, потому что
/// справа под ней стоит дата. Пустое место, которое нельзя убрать, хуже
/// повтора.
/// </remarks>
public sealed class CallSubtitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CallRecord record)
        {
            return string.Empty;
        }

        var text = new StringBuilder(record.Number);

        if (record.WasTransferred)
        {
            text.Append(" · ").Append(Strings.Get("HistoryTransferred"));
        }

        if (record.WasConference)
        {
            text.Append(" · ").Append(Strings.Get("HistoryConference"));
        }

        return text.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Входящий ли звонок — для стрелки значка.</summary>
public sealed class IsIncomingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CallRecord record && record.Direction == CallDirection.Incoming;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Равны ли два значения. Для отметки выбранного фильтра.</summary>
///
/// <remarks>
/// Многозначная привязка, а не довод у обычного конвертера: сравнивать надо
/// значение строки с состоянием окна, и оба приезжают из разных мест дерева.
/// </remarks>
public sealed class EqualityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
