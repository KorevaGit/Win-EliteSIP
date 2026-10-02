using System.Globalization;
using System.Windows.Data;
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

/// <summary>Главная строка звонка. Значения: запись, свой добавочный, прятать ли мобильные.</summary>
///
/// <remarks>Правило — в <see cref="HistoryPresentation"/>.</remarks>
public sealed class CallTitleConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length > 0 && values[0] is CallRecord record
            ? HistoryPresentation.Title(record, CallRowValues.OwnNumber(values), CallRowValues.MasksMobileNumbers(values))
            : string.Empty;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Нижняя строка звонка. Значения: запись, свой добавочный, прятать ли мобильные.</summary>
///
/// <remarks>Правило — в <see cref="HistoryPresentation"/>.</remarks>
public sealed class CallSubtitleConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length > 0 && values[0] is CallRecord record
            ? HistoryPresentation.Subtitle(record, CallRowValues.OwnNumber(values), CallRowValues.MasksMobileNumbers(values))
            : string.Empty;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
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

/// <summary>Разбор значений строки истории — общий у заголовка и нижней строки.</summary>
file static class CallRowValues
{
    public static string OwnNumber(object?[] values) => values.Length > 1 ? values[1] as string ?? string.Empty : string.Empty;

    /// <remarks>
    /// Не пришло — прятать: строка без третьего значения не должна открыть
    /// номер, который место велело скрывать.
    /// </remarks>
    public static bool MasksMobileNumbers(object?[] values) => values.Length <= 2 || values[2] is not false;
}
