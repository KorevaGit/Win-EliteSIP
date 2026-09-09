using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteSIP.AdminAccess;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Распечатанный пакет активации, переживающий закрытый мастер.
/// </summary>
///
/// <remarks>
/// <b>Ключ одноразовый, и сгорает он на «Проверить ключ», а не на «Далее».</b>
/// Так устроен канал раздачи: Worker столбит объект условной записью <i>до</i>
/// того, как отдать его, — иначе два одновременных запроса по одному ключу
/// получили бы пакет оба. Цена там названа прямо и принята: оборвавшаяся закачка
/// сжигает ключ.
///
/// Чего в той цене не было — это закрытого мастера. Человек нажимал «Проверить
/// ключ», видел своё имя, закрывал окно (передумал, сработала перезагрузка,
/// отвлекли) — и ключ был сожжён, а машина не настроена. Панель при этом
/// показывала «ждём активации»: она видит отметку, а машина ей о себе ещё не
/// сказала. Второй раз тот же ключ не открывался никогда, и выход был один —
/// просить новый.
///
/// Черновик закрывает ровно эту дыру и ничего больше. Пакет уже у нас в руках,
/// повторный заход на канал не нужен — нужно только не потерять то, что
/// приехало. Поэтому распечатанное ложится на диск сразу за успешной проверкой,
/// поднимается при следующем открытии мастера и стирается в тот миг, когда
/// настройка применилась.
///
/// <b>Одноразовость ключа это не ослабляет.</b> Сгоревший ключ остаётся
/// сгоревшим для всех: черновик лежит на той же машине и на чужой не поможет —
/// там его попросту нет.
/// </remarks>
internal static class ActivationDraftStore
{
    /// <summary>
    /// Черновик живёт столько же, сколько сам пакет в канале, — двое суток.
    /// </summary>
    ///
    /// <remarks>
    /// Не дольше: пролежавшая неделю копия SIP-пароля на машине, которую так и
    /// не настроили, — это не помощь, а забытая учётная запись. Срок тот же, что
    /// у пакета в бакете, и по той же причине: за этим порогом человеку в любом
    /// случае выпишут новый ключ.
    /// </remarks>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(48);

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Что сохраняем: сам пакет, помашинный доступ, забранный тем же заходом, и
    /// ключ, которым всё это открыли.
    /// </summary>
    ///
    /// <remarks>
    /// Ключ здесь — вопреки правилу «ключ нигде не сохраняется». Правило это
    /// имело смысл, пока ключ был пропуском: незачем держать на диске то, чем
    /// открывается пакет. К этому моменту он уже сожжён и не открывает ничего;
    /// зато он показывается в поле, а мастер, поднявшийся с чужим на вид
    /// черновиком и пустым полем ключа, объяснить себя не может.
    /// </remarks>
    internal sealed record Draft
    {
        public required string Key { get; init; }

        public required ActivationPackage Package { get; init; }

        public MachineAccess? Access { get; init; }

        public required DateTimeOffset SavedAt { get; init; }
    }

    /// <summary>
    /// Где лежит. Рядом с настройками, и файл целиком защищён DPAPI.
    /// </summary>
    ///
    /// <remarks>
    /// В оригинале черновик был обычным JSON под правами <c>0600</c>. На Windows
    /// права такой защиты не дают — это сквозное правило плана, — а внутри лежат
    /// пароль SIP, ключ канала и административный пароль разом. Поэтому не
    /// «файл, который трудно прочитать чужому пользователю», а файл, который
    /// чужому пользователю и на чужой машине не расшифровывается вовсе.
    /// </remarks>
    private static string Path =>
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Settings.AppSettings.DefaultPath)!,
            "activation-draft.dat");

    internal static void Save(string key, ActivationPackage package, MachineAccess? access)
    {
        Draft draft = new()
        {
            Key = key,
            Package = package,
            Access = access,
            SavedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            var path = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            var protectedDraft = ProtectedSecret.Protect(JsonSerializer.Serialize(draft, Format));

            // Через временный файл с переименованием — как и настройки: запись
            // поверх падает посреди файла ровно тогда, когда машину выключили
            // кнопкой.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, protectedDraft);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or System.Security.Cryptography.CryptographicException)
        {
            // Молча: не сохранившийся черновик — потеря удобства, а не отказ
            // настройки. Мастер в эту минуту держит пакет в памяти и доведёт
            // дело до конца.
        }
    }

    /// <summary>
    /// Поднимает черновик, если он есть и не просрочен.
    /// </summary>
    ///
    /// <remarks>
    /// Просроченный стирается здесь же: файл с паролем SIP не должен переживать
    /// свою полезность.
    /// </remarks>
    internal static Draft? Load()
    {
        string stored;
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            stored = File.ReadAllText(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        Draft? draft = null;
        try
        {
            var plain = ProtectedSecret.Unprotect(stored);
            if (plain is not null)
            {
                draft = JsonSerializer.Deserialize<Draft>(plain, Format);
            }
        }
        catch (JsonException)
        {
            draft = null;
        }

        if (draft is null)
        {
            // Испорченный — стереть, а не таскать: разобрать его уже нечем, а
            // мешать он будет каждой следующей загрузке. Сюда же приходит
            // черновик с чужой машины: DPAPI его не расшифровывает, и это не
            // ошибка, а построение.
            Clear();
            return null;
        }

        if (DateTimeOffset.UtcNow - draft.SavedAt >= Lifetime)
        {
            Clear();
            return null;
        }

        return draft;
    }

    internal static void Clear()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Не стёрся — переживёт себя и умрёт по сроку при следующей загрузке.
        }
    }
}
