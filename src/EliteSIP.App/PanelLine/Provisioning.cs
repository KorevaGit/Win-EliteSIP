using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Заводская настройка: единственное, что приложение знает о конторе до того,
/// как в него ввели ключ.
/// </summary>
///
/// <remarks>
/// <b>Заводских предустановок нет.</b> Ни макросов, ни очередей, ни адреса АТС:
/// всё это приезжает после активации и только после неё. До ввода ключа
/// бинарник знает о конторе ровно две вещи — адрес канала раздачи и открытый
/// ключ, которым канал подписывает. Первое не секрет, второе тем более.
///
/// Пара авторизации канала — секрет, и в Git ей нельзя, поэтому файл лежит
/// рядом с приложением и кладётся туда сборкой выпуска. Что она даёт, сказано
/// прямо: защиту от случайного обхода и индексации, но не от того, у кого уже
/// есть копия приложения. Настоящую защиту канала даёт подпись Ed25519.
///
/// <b>Нет файла — линия панели выключена целиком, а приложение работает.</b>
/// Так и задумано: софтфон нужен для звонков, а не для того, чтобы
/// синхронизироваться. Ключ вписывается перед первой выкладкой, и до тех пор
/// машина живёт своим умом.
///
/// В оригинале то же самое лежало двумя частями — пара в <c>provisioning.json</c>
/// внутри бандла, открытый ключ линии в <c>Info.plist</c>. Здесь оба в одном
/// файле: разносить их было следствием того, что в <c>Info.plist</c> секретам
/// нельзя, а ключ подписи не секрет. Двух мест это стоило, а давало только
/// второй способ однажды перепутать, какой ключ чей.
/// </remarks>
internal static class Provisioning
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<Secrets?> Loaded = new(Read);

    /// <summary>Секреты этой сборки. <c>null</c> — файла рядом нет.</summary>
    internal static Secrets? Current => Loaded.Value;

    /// <summary>Что лежит в файле.</summary>
    internal sealed class Secrets
    {
        /// <summary>Канал раздачи. Необязателен: без него линия просто молчит.</summary>
        public UpdateChannel? Updates { get; init; }

        /// <summary>
        /// Открытый ключ линии панели, base64.
        ///
        /// Им подписаны файл предустановок, помашинный доступ и отзыв. В
        /// оригинале лежит в <c>Info.plist</c> под именем
        /// <c>ESPresetsPublicKey</c>.
        /// </summary>
        public string? PresetsPublicKey { get; init; }

        /// <summary>
        /// Открытый ключ линии выпусков, base64.
        /// </summary>
        ///
        /// <remarks>
        /// <b>Ключей два, и это не наша прихоть.</b> W12 свёл их в один — «второй
        /// ключ означал бы второй способ однажды перепутать, какой из них чей», —
        /// и решение было бы верным, если бы в бою ключ был один. Он не один: у
        /// оригинала выпуски подписывает Sparkle своим ключом
        /// (<c>SUPublicEDKey</c>), а панель — своим.
        ///
        /// Цену слияния мы узнали 10 сентября 2026: в заводскую настройку уехал
        /// ключ Sparkle вместо ключа панели, и линия панели замолчала целиком —
        /// без предустановок, без административного пароля, с отвергнутым
        /// отзывом. Отвергала она молча, потому что молчание и есть правильный
        /// ответ на неподтверждённую подпись, и разобрать это удалось только
        /// сверив настоящий объект панели с обоими ключами.
        ///
        /// <c>null</c> — линия выпусков выключена, а панель работает.
        /// </remarks>
        public string? ReleasesPublicKey { get; init; }
    }

    /// <summary>Откуда рабочее место берёт настройки и обновления.</summary>
    internal sealed class UpdateChannel
    {
        /// <summary>Корень канала линии панели: активация, предустановки, помашинный доступ.</summary>
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>
        /// Корень канала выпусков. Не задан — выпуски там же, где панель (<see cref="BaseUrl"/>).
        /// </summary>
        ///
        /// <remarks>
        /// Разведено с 0.1.56, когда выпуски переехали из R2 на свой сервер
        /// (<c>https://update.elitesip.vip:8081</c>), а панель — нет: новый
        /// сервер отдаёт только <c>releases/</c>. Один адрес на всё увёл бы туда
        /// и активацию, и предустановки, и отзыв, и линия панели замолчала бы
        /// так же тихо, как 10 сентября 2026. Пара Basic у обоих каналов одна.
        /// </remarks>
        [JsonPropertyName("releasesURL")]
        public string? ReleasesBaseUrl { get; init; }

        public string User { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        /// <summary>
        /// Адрес пакета активации.
        ///
        /// Приставка <c>activations/</c> принадлежит раскладке бакета, а не
        /// расчёту адреса: ключ даёт только шестнадцатеричное имя.
        /// </summary>
        public Uri? ActivationUrl(string objectName)
            => objectName.Length == 0 ? null : Address("activations/" + objectName);

        /// <summary>
        /// Адрес файла предустановок.
        ///
        /// Файл один на контору: машина ищет в нём себя по идентификатору
        /// предустановки.
        /// </summary>
        public Uri? PresetsUrl() => Address("presets/current.json");

        /// <summary>
        /// Адрес манифеста выпуска.
        ///
        /// Корень свой, если задан <see cref="ReleasesBaseUrl"/>, пара — та же, что у
        /// предустановок: второй секрет ничего
        /// не добавил бы — оба всё равно видны через `strings`, а настоящую
        /// защиту линии даёт подпись Ed25519.
        ///
        /// Среза здесь нет, в отличие от appcast оригинала: сборка одна.
        /// </summary>
        public Uri? ReleasesUrl() => Address(
            string.IsNullOrWhiteSpace(ReleasesBaseUrl) ? BaseUrl : ReleasesBaseUrl, "releases/current.json");

        /// <summary>
        /// Адрес помашинного объекта: <c>access/&lt;id&gt;</c> или
        /// <c>revoked/&lt;id&gt;</c>.
        ///
        /// Приставка приходит сюда строкой, а не перечислением: их две, обе живут
        /// в контракте с панелью, и заводить ради них тип значило бы держать
        /// третье место, где это же написано.
        /// </summary>
        public Uri? MachineUrl(string prefix, string installationID)
            => installationID.Length == 0 ? null : Address($"{prefix}/{installationID}");

        private Uri? Address(string path) => Address(BaseUrl, path);

        private static Uri? Address(string baseUrl, string path)
        {
            var root = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

            return Uri.TryCreate(root + path, UriKind.Absolute, out var url) ? url : null;
        }
    }

    /// <summary>Заголовок Basic для общей пары канала.</summary>
    ///
    /// <remarks>
    /// Общей парой берётся только пакет активации: машины в этот момент ещё нет,
    /// и помашинного ключа тоже. Всё, что после активации, ходит помашинной
    /// парой — см. <see cref="ChannelRequest"/>.
    /// </remarks>
    internal static string BasicHeader(string user, string password)
        => "Basic " + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));

    private static Secrets? Read()
    {
        foreach (var path in Places())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var secrets = JsonSerializer.Deserialize<Secrets>(File.ReadAllText(path), Format);
                if (secrets is not null)
                {
                    return secrets;
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                // Испорченный файл — то же, что его отсутствие: линия молчит, а
                // приложение работает. Ронять из-за него запуск нельзя.
            }
        }

        return null;
    }

    /// <summary>
    /// Где искать файл. Порядок значим: рядом с приложением — то, что положила
    /// сборка выпуска; в каталоге настроек — то, что положил администратор
    /// руками на отдельно взятой машине.
    /// </summary>
    ///
    /// <remarks>
    /// Второго места в оригинале не было — там отладочная сборка читала конфиг
    /// из дерева проекта по <c>#filePath</c>. В .NET такого приёма нет, а путь
    /// машины сборщика в выпуск уезжать не должен, поэтому вместо него обычный
    /// каталог настроек: он один и тот же у отладочной сборки и у выпуска.
    /// </remarks>
    private static IEnumerable<string> Places()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "provisioning.json");
        yield return Path.Combine(
            Path.GetDirectoryName(Settings.AppSettings.DefaultPath)!, "provisioning.json");
    }
}

/// <summary>Общее для всех трёх заходов на канал.</summary>
internal static class ChannelRequest
{
    /// <summary>
    /// Сколько ждать ответа.
    ///
    /// Двадцать секунд у помашинных объектов и предустановок, тридцать у пакета
    /// активации: тот заход человек ждёт у экрана и повторить его не может —
    /// ключ одноразовый.
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    internal static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Сколько ждать закачку выпуска.
    /// </summary>
    ///
    /// <remarks>
    /// Отдельный срок, и он обязан быть отдельным. Общие двадцать секунд
    /// задуманы под манифест в несколько сотен байт; ими же мерялась и закачка
    /// установщика, и пока выпуск весил пять мегабайт это сходило с рук. С
    /// переходом на самодостаточную сборку он стал весить пятьдесят четыре — и
    /// обновление начало обрываться на девятнадцатой секунде с сообщением
    /// «установщик не скачался», в котором ни слова о сроке.
    ///
    /// Четверть часа: на плохом канале конторы полсотни мегабайт идут минуты,
    /// а торопиться тут некуда — закачка фоновая и человека не ждёт.
    /// </remarks>
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Общий клиент на все линии.
    /// </summary>
    ///
    /// <remarks>
    /// Один на приложение, а не по одному на запрос: <see cref="HttpClient"/>
    /// держит пул соединений, и создание его на каждый заход кончается
    /// исчерпанием портов. Здесь заходов немного, но правило дешевле соблюдать
    /// всегда, чем вспоминать, где оно действительно нужно.
    ///
    /// <b>Кэш запрещён явно</b> — тем же заголовком, каким в оригинале
    /// запрещался <c>URLCache</c>. Без него ответ канала с кэширующими
    /// заголовками оседает в процессе, и новая ревизия не доезжает до истечения
    /// его срока: канал при этом отвечает, отметка связи обновляется, и выглядит
    /// всё исправным.
    /// </remarks>
    internal static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        // Срок держит каждый запрос сам, а не клиент.
        //
        // У клиента он по умолчанию сто секунд, и это ещё один потолок поверх
        // наших: закачка выпуска обрывалась бы на нём даже с честным
        // пятнадцатиминутным сроком. Сроки у трёх заходов разные — манифест,
        // пакет активации и установщик, — и одним числом на клиенте их не
        // выразить.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,

        DefaultRequestHeaders =
        {
            CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
            },
        },
    };

    /// <summary>
    /// Помашинная пара: имя пользователя — идентификатор машины, пароль — ключ
    /// канала.
    /// </summary>
    ///
    /// <remarks>
    /// <b>Не общая пара из файла заводской настройки.</b> Общая лежит открытым
    /// текстом в каждом приложении и открывает теперь только выпуски: иначе
    /// уволенный с копией приложения тянул бы настройки конторы бесконечно, а
    /// отрезать его было бы нечем — сменить пару значит переустановить
    /// приложение на всех тридцати машинах.
    /// </remarks>
    internal static void Authorize(HttpRequestMessage request, string installationID, string channelKey)
        => request.Headers.TryAddWithoutValidation(
            "Authorization", Provisioning.BasicHeader(installationID, channelKey));

    /// <summary>
    /// По этим заголовкам панель показывает, кто отстал настолько, что новые
    /// поля до него не доезжают. Больше она о машинах не узнаёт ничего.
    /// </summary>
    ///
    /// <remarks>
    /// Идентификатора машины среди них нет: он приезжает именем пользователя в
    /// Basic и <b>проверен</b>, а не объявлен. Два места для одного факта
    /// однажды разошлись бы.
    /// </remarks>
    internal static void Describe(HttpRequestMessage request, int appliedRevision)
    {
        request.Headers.TryAddWithoutValidation("X-EliteSIP-App", AppVersion);
        request.Headers.TryAddWithoutValidation(
            "X-EliteSIP-Revision", appliedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static string AppVersion { get; } =
        typeof(ChannelRequest).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
}
