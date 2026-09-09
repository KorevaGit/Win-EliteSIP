using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteSIP.PanelLink;

/// <summary>
/// Что канал говорит о свежем выпуске.
/// </summary>
///
/// <remarks>
/// <b>Тот же подписанный конверт, что у предустановок и помашинных объектов.</b>
/// Это не экономия, а то же решение, ради которого конверт вообще один:
/// вторая проверка подписи в приложении означала бы второе место, где её можно
/// однажды не сделать. Ключ тоже тот же — линия одна, и разделять её на «ключ
/// для данных» и «ключ для кода» значило бы завести второй способ перепутать,
/// какой из них чей.
///
/// В оригинале эту роль играл appcast Sparkle с подписью EdDSA. Здесь Sparkle
/// нет, а формат подписи сохранён — только вместо XML-фида наш обычный конверт,
/// потому что разбирать его уже умеет тот же код.
///
/// <b>Подпись Authenticode этого не заменяет и не заменяется этим.</b> Она
/// говорит Windows, что установщик не подделан; подпись манифеста говорит
/// приложению, что установщик выложили мы. Первую проверяет система при запуске
/// файла, вторую — мы до того, как файл вообще скачан.
/// </remarks>
public sealed record ReleaseManifest
{
    /// <summary>Версия формата, которую понимает эта сборка.</summary>
    ///
    /// <remarks>
    /// Правило то же, что у файла предустановок, и по той же причине: машина
    /// обязана работать со старой сборкой. Манифест новее — не отказ, а «нам
    /// нечего отсюда взять», и решает это <see cref="Verified"/>.
    /// </remarks>
    public const int SupportedFormat = 1;

    public required int Format { get; init; }

    /// <summary>Версия выпуска: три числа через точку.</summary>
    public required Version Version { get; init; }

    /// <summary>Откуда качать установщик.</summary>
    ///
    /// <remarks>
    /// Адрес полный, а не имя файла рядом с манифестом: выпуск может лежать в
    /// другом месте канала, и вычислять его из адреса манифеста значило бы
    /// зашить раскладку бакета в клиент.
    /// </remarks>
    public required Uri Url { get; init; }

    /// <summary>
    /// Отпечаток установщика SHA-256, шестнадцатеричный.
    /// </summary>
    ///
    /// <remarks>
    /// Он и есть то, что делает подпись манифеста подписью выпуска: сам
    /// установщик не подписан нашим ключом (его подписывает Authenticode), но
    /// его отпечаток лежит внутри подписанного конверта. Подменивший файл на
    /// канале не подменит отпечаток, не имея закрытого ключа.
    /// </remarks>
    public required string Sha256 { get; init; }

    /// <summary>Размер установщика в байтах. Показывается человеку, а не проверяется.</summary>
    public long Size { get; init; }

    public DateTimeOffset PublishedAt { get; init; }

    /// <summary>Что нового — одной строкой, для журнала и для окна.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>Проверяет подпись и разбирает.</summary>
    ///
    /// <param name="data">то, что скачали с канала.</param>
    /// <param name="publicKey">открытый ключ линии — тот же, что у предустановок.</param>
    public static ReleaseManifest Verified(byte[] data, PanelPublicKey publicKey)
    {
        var payload = SignedEnvelope.Open(data, publicKey);

        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(payload, WireJson.Options);
        }
        catch (JsonException)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (wire?.Version is null || wire.Url is null || wire.Sha256 is null)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (wire.Format > SupportedFormat)
        {
            throw new PanelLinkException(PanelLinkFailure.BundleTooNew);
        }

        if (!Version.TryParse(wire.Version, out var version))
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (!Uri.TryCreate(wire.Url, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps)
        {
            // Только https, и это не педантизм: манифест подписан, а адрес в нём
            // — нет смысла тащить установщик по каналу, который посредник может
            // подменить целиком. Отпечаток такую подмену поймает, но узнать об
            // этом после скачивания хуже, чем не начинать.
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        // Отпечаток проверяется на вид сразу: испорченный здесь означает, что
        // сверять скачанное будет не с чем, и узнавать об этом после закачки
        // установщика незачем.
        if (wire.Sha256.Length != 64 || !IsHex(wire.Sha256))
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        return new ReleaseManifest
        {
            Format = wire.Format,
            Version = version,
            Url = url,
            Sha256 = wire.Sha256,
            Size = wire.Size,
            PublishedAt = WireJson.Timestamp(wire.PublishedAt),
            Notes = wire.Notes ?? string.Empty,
        };
    }

    /// <summary>Новее ли выпуск, чем то, что установлено.</summary>
    ///
    /// <remarks>
    /// Строго новее: равная версия — это та же самая сборка, и предлагать
    /// «обновиться» до себя же нельзя. Меньшая — откат, и его линия обновлений
    /// не делает: откатывает администратор руками, потому что решение о том,
    /// что выпуск плохой, принимает человек.
    /// </remarks>
    public bool IsNewerThan(Version installed)
    {
        ArgumentNullException.ThrowIfNull(installed);

        return Version > installed;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Wire
    {
        [JsonPropertyName("format")]
        public int Format { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("published_at")]
        public string? PublishedAt { get; init; }

        [JsonPropertyName("notes")]
        public string? Notes { get; init; }
    }
}
