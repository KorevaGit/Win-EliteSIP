using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EliteSIP.PanelLink;

/// <summary>
/// Просьба Spark собрать архив журналов для техподдержки.
/// </summary>
///
/// <remarks>
/// Подписанная, как отзыв: по неподписанной машина отправляла бы свои
/// журналы любому, кто сумел положить объект по её адресу. Идентификатор
/// запроса — 32 знака hex: он же имя архива на сервере, и сервер принимает
/// архив только на тот запрос, что лежит для этой машины сейчас.
/// </remarks>
public sealed partial record SupportRequest
{
    public const int SupportedFormat = 1;

    public required string InstallationID { get; init; }

    public required string RequestID { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public static SupportRequest Verified(byte[] data, PanelPublicKey publicKey, string installationID)
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

        if (wire?.InstallationID is null || wire.RequestID is null || !RequestIDFormat().IsMatch(wire.RequestID))
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (wire.Format > SupportedFormat)
        {
            throw new PanelLinkException(PanelLinkFailure.BundleTooNew);
        }

        // Запрос чужой машины — не наш: отправили бы свои журналы на чужой запрос.
        if (!string.Equals(wire.InstallationID, installationID, StringComparison.Ordinal))
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        return new SupportRequest
        {
            InstallationID = wire.InstallationID,
            RequestID = wire.RequestID,
            RequestedAt = WireJson.Timestamp(wire.RequestedAt),
            ExpiresAt = WireJson.Timestamp(wire.ExpiresAt),
        };
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex RequestIDFormat();

    private sealed class Wire
    {
        [JsonPropertyName("format")]
        public int Format { get; init; }

        [JsonPropertyName("installation_id")]
        public string? InstallationID { get; init; }

        [JsonPropertyName("request_id")]
        public string? RequestID { get; init; }

        [JsonPropertyName("requested_at")]
        public string? RequestedAt { get; init; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; init; }
    }
}
