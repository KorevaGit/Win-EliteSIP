using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>
/// Картинки смайликов: разбор строки на текст и смайлики и сами изображения.
/// </summary>
///
/// <remarks>
/// Шрифт Segoe UI Emoji в Windows 10 рисует смайлики плоскими и монохромными, а
/// подписи, которые администратор набирает в Spark, задуманы с картинками Apple
/// (0.1.66). Пакет — один файл <c>emoji.pak</c> рядом с exe: zip без сжатия, по
/// картинке 64×64 на смайлик, имя — коды через «_». Отдельными файлами было бы
/// 3944 штуки на каждое обновление — против них и затеян весь переход на 54.
///
/// <b>Пакет открывается только при первом смайлике.</b> Обычная подпись без
/// единого символа выше U+2000 до него не доходит, и запуск за пакет не платит.
///
/// Имена в пакете кодов без селектора вариантов (FE0F), а в тексте он то есть,
/// то нет — сравнивается без него.
/// </remarks>
internal sealed class EmojiPack
{
    /// <summary>
    /// Знаки, которые без FE0F остаются текстом: ©, ®, ™ и стрелки. Остальные
    /// (⭐, ✅, ⚡) клавиатура телефона ставит без селектора, и рисовать их надо.
    /// </summary>
    private static bool StaysText(int codePoint) =>
        codePoint is 0xA9 or 0xAE or 0x2122 or 0x25AA or 0x25AB or 0x25B6 or 0x25C0
        || codePoint is >= 0x2190 and <= 0x21FF;

    private readonly Dictionary<string, string> _names;
    private readonly Func<string, Stream?> _open;
    private readonly Dictionary<string, BitmapSource?> _images = [];
    private readonly int _longest;

    internal EmojiPack(IEnumerable<string> names, Func<string, Stream?> open)
    {
        _names = [];
        foreach (var name in names)
        {
            var key = Normalize(Path.GetFileNameWithoutExtension(name));
            _names[key] = name;
        }

        _open = open;
        _longest = _names.Keys.Select(key => key.Count(c => c == '_') + 1).DefaultIfEmpty(1).Max() * 2;
    }

    /// <summary>Пакет рядом с exe. <c>null</c> — его нет, и подписи остаются текстом.</summary>
    internal static EmojiPack? Shared => _shared.Value;

    private static readonly Lazy<EmojiPack?> _shared = new(Load);

    private static EmojiPack? Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "emoji.pak");
            if (!File.Exists(path))
            {
                return null;
            }

            // Поток живёт, пока живёт приложение: картинки читаются по мере
            // надобности, а держать 15 МБ в памяти ради десятка смайликов незачем.
            var archive = new ZipArchive(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                ZipArchiveMode.Read);

            return new EmojiPack(
                archive.Entries.Select(entry => entry.Name),
                name =>
                {
                    lock (archive)
                    {
                        var entry = archive.GetEntry(name);
                        if (entry is null)
                        {
                            return null;
                        }

                        using var stream = entry.Open();
                        var copy = new MemoryStream((int)entry.Length);
                        stream.CopyTo(copy);
                        copy.Position = 0;
                        return copy;
                    }
                });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Есть ли в строке хоть что-то, похожее на смайлик, — дёшево, без пакета.</summary>
    internal static bool MayContainEmoji(string? text)
        => text is { Length: > 0 } && text.Any(c => c > ' ' || char.IsSurrogate(c));

    /// <summary>Часть строки: текст или смайлик (тогда <c>Key</c> — имя картинки).</summary>
    internal readonly record struct Piece(string? Text, string? Key);

    internal List<Piece> Split(string text)
    {
        var runes = text.EnumerateRunes().ToList();
        var pieces = new List<Piece>();
        var buffer = new StringBuilder();
        var index = 0;

        while (index < runes.Count)
        {
            var matched = 0;
            string? name = null;

            for (var length = Math.Min(_longest, runes.Count - index); length >= 1; length--)
            {
                var key = Normalize(string.Join('_', runes.Skip(index).Take(length).Select(Hex)));
                if (!_names.TryGetValue(key, out var found))
                {
                    continue;
                }

                var baseRune = runes[index].Value;
                var explicitVariant = length > 1 && runes[index + 1].Value == 0xFE0F;
                if (StaysText(baseRune) && !explicitVariant && !key.Contains('_', StringComparison.Ordinal))
                {
                    continue;
                }

                matched = length;
                name = found;
                break;
            }

            if (matched == 0)
            {
                buffer.Append(runes[index].ToString());
                index++;
                continue;
            }

            if (buffer.Length > 0)
            {
                pieces.Add(new Piece(buffer.ToString(), null));
                buffer.Clear();
            }

            pieces.Add(new Piece(null, name));
            index += matched;
        }

        if (buffer.Length > 0)
        {
            pieces.Add(new Piece(buffer.ToString(), null));
        }

        return pieces;
    }

    /// <summary>Картинка смайлика; <c>null</c> — не читается.</summary>
    internal BitmapSource? Image(string name)
    {
        if (_images.TryGetValue(name, out var cached))
        {
            return cached;
        }

        BitmapSource? image = null;
        try
        {
            if (_open(name) is { } stream)
            {
                using (stream)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
            }
        }
        catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException)
        {
            image = null;
        }

        _images[name] = image;
        return image;
    }

    private static string Hex(Rune rune) => rune.Value.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>Без селектора вариантов и без ведущих нулей.</summary>
    private static string Normalize(string key)
        => string.Join('_', key.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.TrimStart('0'))
            .Where(part => part.Length > 0 && part != "fe0f"));
}
