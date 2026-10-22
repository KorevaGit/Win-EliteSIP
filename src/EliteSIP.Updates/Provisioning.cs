using System.Text;
using System.Text.Json;
using EliteSIP.PanelLink;

namespace EliteSIP.Updater;

/// <summary>
/// Заводская настройка — <b>только</b> рядом с собой.
/// </summary>
///
/// <remarks>
/// Приложение ищет этот файл в двух местах: рядом с собой и в каталоге настроек
/// пользователя. Здесь второго места нет и быть не должно: каталог пользователя
/// оператору доступен, а из этого файла берётся ключ, которым проверяется
/// подпись выпуска. Позволить подменить его значило бы отдать установку под
/// правами SYSTEM тому, кто подменил.
/// </remarks>
internal sealed class Provisioning
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public UpdateChannel? Updates { get; init; }

    public string? PresetsPublicKey { get; init; }

    /// <summary>Ключ линии выпусков. Им и только им проверяется манифест.</summary>
    public string? ReleasesPublicKey { get; init; }

    internal static Provisioning? Read(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "provisioning.json");

            return File.Exists(path)
                ? JsonSerializer.Deserialize<Provisioning>(File.ReadAllText(path), Format)
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ключ, которым проверяется манифест выпуска.
    /// </summary>
    ///
    /// <remarks>
    /// Линии выпусков, а не панели. Обновляльщику ключ панели не нужен вовсе:
    /// он не читает ни предустановок, ни отзывов.
    /// </remarks>
    internal PanelPublicKey? PublicKey()
    {
        if (string.IsNullOrEmpty(ReleasesPublicKey))
        {
            return null;
        }

        try
        {
            return PanelPublicKey.FromBase64(ReleasesPublicKey);
        }
        catch (PanelLinkException)
        {
            return null;
        }
    }

    internal sealed class UpdateChannel
    {
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>Корень канала выпусков; не задан — <see cref="BaseUrl"/>. См. приложение, <c>Provisioning.UpdateChannel</c>.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("releasesURL")]
        public string? ReleasesBaseUrl { get; init; }

        public string User { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        internal Uri? ReleasesUrl()
        {
            var baseUrl = string.IsNullOrWhiteSpace(ReleasesBaseUrl) ? BaseUrl : ReleasesBaseUrl;
            var root = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

            return Uri.TryCreate(root + ReleaseLine.Current.Manifest, UriKind.Absolute, out var url)
                ? url
                : null;
        }

        internal string BasicHeader()
            => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
    }
}
