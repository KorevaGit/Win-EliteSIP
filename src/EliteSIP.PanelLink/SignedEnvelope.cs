using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace EliteSIP.PanelLink;

/// <summary>
/// Открытый ключ панели: тем, чем она подписывает, и только им.
/// </summary>
///
/// <remarks>
/// Своим типом, а не голыми байтами, по единственной причине: длину ключа надо
/// проверить один раз в одном месте. Ed25519 — ровно тридцать два байта, и
/// тридцать первый превратил бы проверку подписи в исключение изнутри
/// библиотеки, то есть в отказ без внятной причины.
///
/// В оригинале эту роль играл <c>Curve25519.Signing.PublicKey</c> из CryptoKit.
/// </remarks>
public sealed class PanelPublicKey
{
    /// <summary>Длина открытого ключа Ed25519.</summary>
    public const int Length = 32;

    /// <summary>Из байтов.</summary>
    public PanelPublicKey(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length != Length)
        {
            throw new PanelLinkException(PanelLinkFailure.SignatureDidNotMatch);
        }

        Parameters = new Ed25519PublicKeyParameters(bytes, 0);
    }

    /// <summary>
    /// Из base64 — так ключ лежит в настройках сборки.
    ///
    /// Испорченная строка отвечает тем же, чем несошедшаяся подпись: с ключом,
    /// который не прочитался, проверить нельзя ничего, и притворяться, что файл
    /// в порядке, нельзя тем более.
    /// </summary>
    public static PanelPublicKey FromBase64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Span<byte> bytes = stackalloc byte[Length];
        if (!Convert.TryFromBase64String(value, bytes, out var written) || written != Length)
        {
            throw new PanelLinkException(PanelLinkFailure.SignatureDidNotMatch);
        }

        return new PanelPublicKey(bytes.ToArray());
    }

    internal Ed25519PublicKeyParameters Parameters { get; }
}

/// <summary>
/// Конверт, в котором панель выкладывает всё подписанное.
/// </summary>
///
/// <remarks>
/// Один и тот же у файла предустановок, у помашинного доступа и у отзыва — и
/// это решение, а не совпадение. Второй конверт означал бы вторую проверку
/// подписи в приложении, то есть второе место, где её можно однажды не сделать.
///
/// Подпись считается по <b>байтам</b> <c>payload</c>, а сам он лежит рядом как
/// есть. Так проверка не зависит ни от канонизации JSON, ни от того, как
/// посредник переставит ключи: подписано ровно то, что прочитано.
/// </remarks>
internal static class SignedEnvelope
{
    /// <summary>Достаёт содержимое, если подпись сошлась.</summary>
    internal static byte[] Open(byte[] data, PanelPublicKey publicKey)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(publicKey);

        Wire? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Wire>(data, WireJson.Options);
        }
        catch (JsonException)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (envelope?.Payload is null || envelope.Signature is null)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, publicKey.Parameters);
        verifier.BlockUpdate(payload, 0, payload.Length);

        // Подпись не той длины BouncyCastle отвергает сам, возвращая false, — но
        // не молча: проверять её длину здесь значило бы держать второе мнение о
        // том, что такое подпись Ed25519.
        if (!verifier.VerifySignature(signature))
        {
            throw new PanelLinkException(PanelLinkFailure.SignatureDidNotMatch);
        }

        return payload;
    }

    private sealed class Wire
    {
        [JsonPropertyName("payload")]
        public string? Payload { get; init; }

        [JsonPropertyName("signature")]
        public string? Signature { get; init; }
    }
}

/// <summary>
/// Помашинный доступ: то, что принадлежит одной машине и не может лежать в
/// общем файле предустановок.
/// </summary>
///
/// <remarks>
/// Административный пароль — поле предустановки, а у техподдержки предустановка
/// своя. Положи пароль в общий файл — и любой оператор прочитает пароль
/// поддержки в собственном скачанном файле, а разделение станет мнимым.
///
/// Тип сериализуемый — по той же причине, что и конверт пакета активации:
/// подписанный объект забирается тем же заходом, что и пакет, и обязан пережить
/// закрытый мастер вместе с ним. Подпись при этом на диск не едет: черновик
/// лежит в нашем же каталоге, защищённом правами, а проверена она была при
/// получении.
/// </remarks>
public sealed record MachineAccess
{
    public const int SupportedFormat = 1;

    public required string InstallationID { get; init; }

    public required string PresetID { get; init; }

    public required string AdminPassword { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Проверяет подпись и разбирает.</summary>
    ///
    /// <param name="data">то, что скачали с канала.</param>
    /// <param name="publicKey">открытый ключ линии предустановок.</param>
    /// <param name="installationID">
    /// чей доступ мы ожидали получить. Совпадение проверяется здесь, а не только
    /// на стороне Worker'а: подписанный объект чужой машины — это чужой
    /// административный пароль, и принимать его молча нельзя, даже если канал
    /// его почему-то отдал.
    /// </param>
    public static MachineAccess Verified(byte[] data, PanelPublicKey publicKey, string installationID)
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

        if (wire?.InstallationID is null || wire.PresetID is null || wire.AdminPassword is null)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (wire.Format > SupportedFormat)
        {
            throw new PanelLinkException(PanelLinkFailure.BundleTooNew);
        }

        if (!string.Equals(wire.InstallationID, installationID, StringComparison.Ordinal))
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        return new MachineAccess
        {
            InstallationID = wire.InstallationID,
            PresetID = wire.PresetID,
            AdminPassword = wire.AdminPassword,
            IssuedAt = WireJson.Timestamp(wire.IssuedAt),
        };
    }

    private sealed class Wire
    {
        [JsonPropertyName("format")]
        public int Format { get; init; }

        [JsonPropertyName("installation_id")]
        public string? InstallationID { get; init; }

        [JsonPropertyName("preset_id")]
        public string? PresetID { get; init; }

        [JsonPropertyName("admin_password")]
        public string? AdminPassword { get; init; }

        [JsonPropertyName("issued_at")]
        public string? IssuedAt { get; init; }
    }
}

/// <summary>
/// Отзыв: единственное, что запускает сброс машины.
/// </summary>
///
/// <remarks>
/// <b>Подписанный, и только подписанный.</b> Сброс по отказу в доступе означал
/// бы, что опечатка в правиле Cloudflare, протухший секрет Worker'а или
/// оборвавшаяся уборка стирают не одну машину, а все тридцать разом. Отсутствие
/// ответа никогда не означает отзыв: машина, не достучавшаяся до канала,
/// работает дальше.
/// </remarks>
public sealed record Revocation
{
    public const int SupportedFormat = 1;

    public required string InstallationID { get; init; }

    public required DateTimeOffset RevokedAt { get; init; }

    public static Revocation Verified(byte[] data, PanelPublicKey publicKey, string installationID)
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

        if (wire?.InstallationID is null)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (wire.Format > SupportedFormat)
        {
            throw new PanelLinkException(PanelLinkFailure.BundleTooNew);
        }

        // Отзыв чужой машины — не наше дело. Совпадение обязательно: иначе
        // подсунутый объект соседней машины сбрасывал бы эту.
        if (!string.Equals(wire.InstallationID, installationID, StringComparison.Ordinal))
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        return new Revocation
        {
            InstallationID = wire.InstallationID,
            RevokedAt = WireJson.Timestamp(wire.RevokedAt),
        };
    }

    private sealed class Wire
    {
        [JsonPropertyName("format")]
        public int Format { get; init; }

        [JsonPropertyName("installation_id")]
        public string? InstallationID { get; init; }

        [JsonPropertyName("revoked_at")]
        public string? RevokedAt { get; init; }
    }
}
