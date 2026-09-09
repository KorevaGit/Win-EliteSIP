using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteSIP.PanelLink;

/// <summary>
/// Пакет активации: всё, что нужно рабочему месту, одним зашифрованным файлом.
/// </summary>
///
/// <remarks>
/// Приложение скачивает его по адресу, выведенному из ключа, и распечатывает
/// тем же ключом. Панель при этом ничего не знает о машине: она положила пакет
/// и забыла, а забрал его тот, у кого ключ.
///
/// Тип сериализуемый — не для канала: по нему пакет ездит своим форматом,
/// зашифрованным и с заголовком, а разбирают его <c>Wire</c> ниже. Это для
/// черновика мастера: распечатанный пакет ложится на диск до конца настройки,
/// потому что ключ сгорает в тот же миг, когда его проверили, — см.
/// <c>ActivationDraftStore</c>.
/// </remarks>
public sealed record ActivationPackage
{
    /// <summary>
    /// Версия формата, которую понимает эта сборка.
    ///
    /// Пакет более новой версии не разбирается вовсе, и это не упрямство: поля
    /// внутри — учётные данные, и применить наполовину понятый пакет означало
    /// бы поднять рабочее место наполовину.
    ///
    /// Правило зеркально файлу предустановок: там машина терпима и пропускает
    /// незнакомое, потому что обязана работать со старой сборкой. Здесь —
    /// строга, потому что это разовое действие под присмотром человека, и отказ
    /// с внятной причиной лучше половины настройки.
    /// </summary>
    public const int SupportedFormat = 2;

    /// <summary>
    /// Заголовок в начале шифротекста. Он же уходит в дополнительные данные
    /// AES-GCM: подменивший заголовок ломает проверку целостности.
    ///
    /// Вторая версия — разбор 25 августа 2026: изменился и вывод ключа, и
    /// состав содержимого.
    /// </summary>
    internal static ReadOnlySpan<byte> Header => "ESIPA2"u8;

    private static ReadOnlySpan<byte> HeaderPrefix => "ESIPA"u8;

    public required int Format { get; init; }

    public required string InstallationID { get; init; }

    /// <summary>
    /// Помашинный ключ доступа к каналу раздачи.
    ///
    /// С него начинается всё, что машина получает после активации: файл
    /// предустановок, свой административный пароль и проверка отзыва. Общая пара
    /// из установщика открывает теперь только выпуски — поэтому уволенный с
    /// копией приложения тянет обновления и не тянет настройки конторы.
    ///
    /// <b>Панель убирает его на своей стороне — и машина перестаёт получать что
    /// бы то ни было.</b> Отсюда и взялся отзыв как техническое действие.
    /// </summary>
    public required string ChannelKey { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required string Employee { get; init; }

    public required string Number { get; init; }

    public required string SipPassword { get; init; }

    /// <summary>
    /// Предустановка, приехавшая с пакетом.
    ///
    /// Административного пароля в пакете нет. Он стал полем предустановки и
    /// приезжает отдельным помашинным объектом — <see cref="MachineAccess"/>.
    /// Держать его ещё и здесь значило бы завести второй источник одного факта:
    /// пакет выдаётся один раз, а пароль меняют когда угодно после.
    /// </summary>
    public required PresetInfo Preset { get; init; }

    /// <summary>Предустановка внутри пакета.</summary>
    public sealed record PresetInfo
    {
        public required string ID { get; init; }

        public required string Name { get; init; }

        public required int Revision { get; init; }

        public required int SchemaVersion { get; init; }

        /// <summary>
        /// Управляемые поля как есть, неразобранными.
        ///
        /// Разбирает их та же дорога, что и файл предустановок: правило
        /// «незнакомое пропускается, понятное применяется» должно быть одно на
        /// оба пути, а не два похожих.
        ///
        /// Строкой, а не байтами: в оригинале здесь лежал <c>Data</c> с
        /// пересобранным JSON, у которого ключи отсортированы, — иначе два
        /// одинаковых по смыслу пакета не сравнивались бы. В C# то же свойство
        /// даёт исходный текст: он сравнивается как строка и не зависит от того,
        /// как разбор представил бы числа.
        /// </summary>
        public required string Settings { get; init; }
    }

    /// <summary>
    /// Распечатывает пакет.
    /// </summary>
    ///
    /// <param name="sealedPackage">то, что скачали по адресу из ключа.</param>
    /// <param name="key">
    /// материал ключа, которым же посчитан и адрес. Одна прогонка на оба
    /// применения — см. <see cref="BoundActivationKey"/>.
    /// </param>
    public static ActivationPackage Open(byte[] sealedPackage, BoundActivationKey key)
    {
        ArgumentNullException.ThrowIfNull(sealedPackage);
        ArgumentNullException.ThrowIfNull(key);

        // Заголовок проверяется до всякой криптографии: пакет чужой версии надо
        // отличить от неподошедшего ключа, иначе человек пойдёт искать опечатку
        // в ключе вместо того, чтобы обновить приложение.
        if (sealedPackage.Length <= Header.Length)
        {
            throw new PanelLinkException(PanelLinkFailure.KeyDidNotOpen);
        }

        var head = sealedPackage.AsSpan(0, Header.Length);
        if (!head.SequenceEqual(Header))
        {
            // Начало «ESIPA» с другой цифрой — это наш формат, но не наша
            // версия. Новее — приложение отстало и его надо обновить; старее —
            // отстала панель, и обновление приложения делу не поможет. Второе
            // возможно, пока в конторе не выложили новую панель, и говорить в
            // этом случае «обновите приложение» значит отправить человека не
            // туда.
            if (head[..HeaderPrefix.Length].SequenceEqual(HeaderPrefix))
            {
                throw new PanelLinkException(head[^1] > Header[^1]
                    ? PanelLinkFailure.PackageTooNew
                    : PanelLinkFailure.KeyDidNotOpen);
            }

            throw new PanelLinkException(PanelLinkFailure.KeyDidNotOpen);
        }

        // Двенадцать байт nonce сразу за заголовком — как кладёт панель, — и
        // метка целостности в последних шестнадцати.
        const int NonceLength = 12;
        const int TagLength = 16;

        if (sealedPackage.Length <= Header.Length + NonceLength + TagLength)
        {
            throw new PanelLinkException(PanelLinkFailure.KeyDidNotOpen);
        }

        var body = sealedPackage.AsSpan(Header.Length);
        var nonce = body[..NonceLength];
        var ciphertext = body[NonceLength..^TagLength];
        var tag = body[^TagLength..];

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(key.CipherKey, TagLength);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, Header);
        }
        catch (CryptographicException error)
        {
            // Сюда приходит и неверный ключ, и испорченный файл. Ответ один и
            // тот же — см. PanelLinkFailure.KeyDidNotOpen.
            throw new PanelLinkException(PanelLinkFailure.KeyDidNotOpen, error);
        }

        return Decode(plaintext);
    }

    /// <summary>Разбирает распечатанный JSON.</summary>
    internal static ActivationPackage Decode(byte[] plaintext)
    {
        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(plaintext, WireJson.Options);
        }
        catch (JsonException error)
        {
            // Ключ подошёл — расшифровка сошлась, — а содержимое не разобралось.
            // Значит панель новее нас или пакет собран не тем.
            throw new PanelLinkException(PanelLinkFailure.PackageTooNew, error);
        }

        if (wire is null || wire.InstallationID is null || wire.ChannelKey is null
            || wire.Employee is null || wire.Number is null || wire.SipPassword is null
            || wire.Preset is null || wire.Preset.ID is null || wire.Preset.Name is null)
        {
            // Недостающее обязательное поле — тот же случай, что и мусор вместо
            // JSON: пакет собран не тем, кем мы думали.
            throw new PanelLinkException(PanelLinkFailure.PackageTooNew);
        }

        if (wire.Format > SupportedFormat)
        {
            throw new PanelLinkException(PanelLinkFailure.PackageTooNew);
        }

        return new ActivationPackage
        {
            Format = wire.Format,
            InstallationID = wire.InstallationID,
            ChannelKey = wire.ChannelKey,
            IssuedAt = WireJson.Timestamp(wire.IssuedAt),
            Employee = wire.Employee,
            Number = wire.Number,
            SipPassword = wire.SipPassword,
            Preset = new PresetInfo
            {
                ID = wire.Preset.ID,
                Name = wire.Preset.Name,
                Revision = wire.Preset.Revision,
                SchemaVersion = wire.Preset.SchemaVersion,
                Settings = WireJson.RawText(wire.Preset.Settings),
            },
        };
    }

    /// <summary>Как пакет выглядит в канале. Наружу этот вид не выходит.</summary>
    private sealed class Wire
    {
        [JsonPropertyName("format")]
        public int Format { get; init; }

        [JsonPropertyName("installation_id")]
        public string? InstallationID { get; init; }

        [JsonPropertyName("channel_key")]
        public string? ChannelKey { get; init; }

        [JsonPropertyName("issued_at")]
        public string? IssuedAt { get; init; }

        [JsonPropertyName("employee")]
        public string? Employee { get; init; }

        [JsonPropertyName("number")]
        public string? Number { get; init; }

        [JsonPropertyName("sip_password")]
        public string? SipPassword { get; init; }

        [JsonPropertyName("preset")]
        public WirePreset? Preset { get; init; }
    }

    private sealed class WirePreset
    {
        [JsonPropertyName("id")]
        public string? ID { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("revision")]
        public int Revision { get; init; }

        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        /// <summary>
        /// Управляемые поля не разбираются здесь, а сохраняются как есть.
        /// </summary>
        [JsonPropertyName("settings")]
        public JsonElement Settings { get; init; }
    }
}

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
