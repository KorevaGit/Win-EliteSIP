using System.Text;
using System.Text.RegularExpressions;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Incoming;

/// <summary>Подпись раздачи по имени, с которым она пришла в <c>From</c>.</summary>
///
/// <remarks>
/// Название линии едет в имени звонящего — номер очереди в вызов не попадает
/// вовсе, поэтому прежний словарь «номер → название» не сработал ни разу.
/// Правила «что искать в имени → как показать» заводятся в Spark (вкладка
/// «Очереди») и приезжают в файле предустановок. Имя без правила показывается
/// как пришло, только слипшиеся слова разделяются: перевод с транслита
/// угадывал бы, а ошибка в заголовке окна путает сильнее латиницы.
///
/// То же правило повторено в Spark (<c>frontend/src/lib/queueTitles.js</c>,
/// <c>preset/queues.go</c>): предпросмотр там обязан показывать то, что увидит
/// оператор. Менять — в обоих местах разом; примеры в проверках общие.
/// </remarks>
public static partial class QueueTitles
{
    private static volatile Rule[] _current = [];

    /// <summary>Правило, готовое к сверке: ключ посчитан один раз.</summary>
    public readonly record struct Rule(string Key, string Title);

    /// <summary>
    /// Действующие правила — снимок настроек. Снимок, а не сама коллекция:
    /// вызов разбирается не в потоке окна, а правила меняет приехавший файл.
    /// </summary>
    public static IReadOnlyList<Rule> Current => _current;

    /// <summary>Заменяет действующие правила.</summary>
    public static void Use(IEnumerable<QueueSetting> titles) => _current = Rules(titles);

    /// <summary>Правила из настроек; строки без искомого или без подписи не работают.</summary>
    public static Rule[] Rules(IEnumerable<QueueSetting> titles)
        => [.. titles
            .Select(title => new Rule(Key(title.Match), title.Title.Trim()))
            .Where(rule => rule.Key.Length > 0 && rule.Title.Length > 0)];

    /// <summary>По чему правило сверяется с именем: без регистра, пробелов и подчёркиваний.</summary>
    public static string Key(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder key = new(text.Length);
        foreach (var symbol in text)
        {
            if (symbol != '_' && !char.IsWhiteSpace(symbol))
            {
                key.Append(char.ToLowerInvariant(symbol));
            }
        }

        return key.ToString();
    }

    /// <summary>
    /// Имя в читаемом виде: подчёркивания — пробелы, слипшиеся слова разделены
    /// по смене регистра и на границе букв и цифр. Ничего не переводит.
    /// </summary>
    public static string Tidy(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = name.Replace('_', ' ');
        text = LowerThenUpper().Replace(text, "$1 $2");
        text = UpperRunThenWord().Replace(text, "$1 $2");
        text = LetterThenDigit().Replace(text, "$1 $2");
        text = DigitThenLetter().Replace(text, "$1 $2");
        text = Spaces().Replace(text, " ").Trim();

        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>
    /// Заголовок раздачи с таким именем: подпись первого подходящего правила,
    /// иначе причёсанное имя. <c>null</c> — имени нет, сказать нечего.
    /// </summary>
    ///
    /// <remarks>
    /// Имя без единой буквы именем не считается: так транк повторяет в нём
    /// номер, и заголовком встал бы мобильный лида.
    /// </remarks>
    public static string? Headline(string? name, IReadOnlyList<Rule>? rules = null)
    {
        if (name is null || !name.Any(char.IsLetter))
        {
            return null;
        }

        var key = Key(name);
        foreach (var rule in rules ?? _current)
        {
            if (key.Contains(rule.Key, StringComparison.Ordinal))
            {
                return rule.Title;
            }
        }

        return Tidy(name);
    }

    [GeneratedRegex(@"(\p{Ll})(\p{Lu})")]
    private static partial Regex LowerThenUpper();

    [GeneratedRegex(@"(\p{Lu})(\p{Lu}\p{Ll})")]
    private static partial Regex UpperRunThenWord();

    [GeneratedRegex(@"(\p{L})(\p{Nd})")]
    private static partial Regex LetterThenDigit();

    [GeneratedRegex(@"(\p{Nd})(\p{L})")]
    private static partial Regex DigitThenLetter();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
