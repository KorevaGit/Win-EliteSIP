using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteSIP.PanelLink;

/// <summary>
/// Файл предустановок: то, чем панель правит уже настроенные рабочие места.
/// </summary>
///
/// <remarks>
/// Отдельная от линии обновлений дорога — для данных, а не для кода. Приложение
/// опрашивает её тем же таймером, что и обновления, проверяет подпись и
/// применяет.
///
/// <b>Подпись проверяется до разбора содержимого.</b> Не после и не «заодно»:
/// проверка идёт по байтам <c>payload</c> как они пришли, поэтому не зависит ни
/// от канонизации JSON, ни от того, как посредник переставит ключи. Разбирать
/// сначала значило бы подписывать своё представление о файле, а не файл.
/// </remarks>
public sealed record PresetBundle
{
    /// <summary>Версия формата, которую понимает эта сборка.</summary>
    public const int SupportedFormat = 1;

    public required int Format { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required IReadOnlyList<Entry> Presets { get; init; }

    /// <summary>Одна предустановка в файле.</summary>
    public sealed record Entry
    {
        /// <summary>
        /// Машина ищет себя по нему, а <b>не по имени</b>: имя переименовывают,
        /// и поиск по нему разорвал бы связь у всех машин разом.
        /// </summary>
        public required string ID { get; init; }

        public required string Name { get; init; }

        public required int Revision { get; init; }

        public required int SchemaVersion { get; init; }

        /// <summary>Управляемые поля как есть — разбирает их приложение.</summary>
        public required string Fields { get; init; }
    }

    /// <summary>Проверяет подпись и разбирает файл.</summary>
    ///
    /// <param name="data">то, что скачали с канала.</param>
    /// <param name="publicKey">открытый ключ линии предустановок.</param>
    public static PresetBundle Verified(byte[] data, PanelPublicKey publicKey)
    {
        // Конверт общий с помашинными объектами — см. SignedEnvelope.
        return Decode(SignedEnvelope.Open(data, publicKey));
    }

    /// <summary>Разбирает уже проверенное содержимое.</summary>
    internal static PresetBundle Decode(byte[] payload)
    {
        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(payload, WireJson.Options);
        }
        catch (JsonException)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (wire?.Presets is null)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (wire.Format > SupportedFormat)
        {
            throw new PanelLinkException(PanelLinkFailure.BundleTooNew);
        }

        var entries = new List<Entry>(wire.Presets.Count);
        foreach (var entry in wire.Presets)
        {
            if (entry.ID is null || entry.Name is null)
            {
                // Запись без опознавательных знаков — это не предустановка. Файл
                // целиком из-за неё не теряется: машина ищет в нём себя по
                // идентификатору, и безымянная запись просто никогда не найдётся.
                throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
            }

            entries.Add(new Entry
            {
                ID = entry.ID,
                Name = entry.Name,
                Revision = entry.Revision,
                SchemaVersion = entry.SchemaVersion,
                Fields = WireJson.RawText(entry.Fields),
            });
        }

        return new PresetBundle
        {
            Format = wire.Format,
            GeneratedAt = WireJson.Timestamp(wire.GeneratedAt),
            Presets = entries,
        };
    }

    /// <summary>
    /// Своя запись в файле.
    /// </summary>
    ///
    /// <remarks>
    /// По <see cref="Entry.ID"/>, а не по имени. Отсутствие себя в файле не
    /// ошибка: предустановку могли заархивировать, и машина продолжает работать
    /// с тем, что применила раньше.
    /// </remarks>
    public Entry? EntryOf(string id)
        => Presets.FirstOrDefault(entry => string.Equals(entry.ID, id, StringComparison.Ordinal));

    private sealed class Wire
    {
        [JsonPropertyName("format")]
        public int Format { get; init; }

        [JsonPropertyName("generated_at")]
        public string? GeneratedAt { get; init; }

        [JsonPropertyName("presets")]
        public IReadOnlyList<WireEntry>? Presets { get; init; }
    }

    private sealed class WireEntry
    {
        [JsonPropertyName("id")]
        public string? ID { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("revision")]
        public int Revision { get; init; }

        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("fields")]
        public JsonElement Fields { get; init; }
    }
}
