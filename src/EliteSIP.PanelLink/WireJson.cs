using System.Globalization;
using System.Text.Json;

namespace EliteSIP.PanelLink;

/// <summary>Мелочь, общая всем разборам присланного панелью.</summary>
internal static class WireJson
{
    /// <summary>
    /// Разбор нечувствителен к регистру имён — как <c>JSONDecoder</c> в
    /// оригинале не был чувствителен к порядку ключей. Числа в кавычках при
    /// этом не принимаются: панель кладёт их числами, и терпимость здесь
    /// означала бы, что мы принимаем чужой формат за свой.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Время из строки ISO 8601.
    ///
    /// Не разобравшееся время — не повод отказать: в оригинале здесь стояло
    /// <c>?? Date()</c>. Дата выдачи показывается человеку и никем не
    /// проверяется, а отказ от пакета из-за неё стоил бы рабочего места.
    /// </summary>
    internal static DateTimeOffset Timestamp(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    /// <summary>
    /// Кусок JSON как есть.
    ///
    /// Отсутствующий блок — это пустой объект, а не отказ: управляемых полей у
    /// предустановки может не быть вовсе.
    /// </summary>
    internal static string RawText(JsonElement element)
        => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : element.GetRawText();
}
