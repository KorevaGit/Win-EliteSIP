using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteSIP.PanelLink;

/// <summary>
/// Конфигурация машины из Spark: <c>config/&lt;installation_id&gt;</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// Пришла на смену пакету активации. Номер, SIP-пароль, формат работы,
/// предустановка и пароль настроек теперь не приезжают один раз по ключу, а
/// лежат на сервере обновлений и обновляются при каждой правке сотрудника в
/// Spark. Машина забирает их раз в пятнадцать минут и применяет только ревизию
/// новее применённой.
/// </para>
/// <para>
/// <b>Две защиты, и обе обязательны.</b> Подпись Ed25519 (тот же ключ Spark,
/// что у предустановок) говорит, что объект выпустил Spark. Шифрование под
/// ключ машины говорит, что прочесть его может только она: объект лежит на
/// общем сервере, и SIP-пароль в нём не должен быть виден ни серверу, ни
/// соседней машине.
/// </para>
/// </remarks>
public sealed record MachineConfig
{
    /// <summary>Какой формат оболочки клиент понимает.</summary>
    public const int SupportedFormat = 1;

    /// <summary>Строка вывода ключа — та же, что у <c>pairSealInfo</c> в Spark.</summary>
    internal const string SealInfo = "elitesip.pair.v1";

    /// <summary>Дополнительные данные AES-GCM — <c>pairSealAAD</c> в Spark.</summary>
    internal const string SealAad = "ESIPP1";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    public required string InstallationID { get; init; }

    public required int Revision { get; init; }

    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>Имя сотрудника — подпись профиля. Пустое не затирает прежнее.</summary>
    public string Employee { get; init; } = string.Empty;

    /// <summary>Основной номер.</summary>
    public string Number { get; init; } = string.Empty;

    public string SipPassword { get; init; } = string.Empty;

    /// <summary><c>office</c>, <c>remote</c> или неизвестное — тогда площадку не трогать.</summary>
    public string WorkFormat { get; init; } = string.Empty;

    public string PresetID { get; init; } = string.Empty;

    public string PresetName { get; init; } = string.Empty;

    /// <summary>Пароль настроек. Пустой — у предустановки его нет, прежний снять.</summary>
    public string AdminPassword { get; init; } = string.Empty;

    /// <summary>
    /// Все номера сотрудника, основной первым. Пусто, когда номер один.
    /// </summary>
    public IReadOnlyList<Line> Lines { get; init; } = [];

    /// <summary>Номер из списка <c>lines</c>.</summary>
    public sealed record Line(string ID, string Number, string SipPassword, string Label);

    /// <summary>Идентификатор основного номера в <c>lines</c>.</summary>
    public const string MainLineID = "main";

    /// <summary>
    /// Номера, которые должны стать профилями: список <c>lines</c>, а если его
    /// нет — один основной номер с подписью сотрудника.
    /// </summary>
    ///
    /// <remarks>
    /// Так же, как <c>effectiveLines</c> на macOS: у сотрудника из Битрикса и у
    /// ручного профиля с одним номером <c>lines</c> в конфигурации нет вовсе, и
    /// машина не должна различать эти случаи дальше этой строки.
    /// </remarks>
    public IReadOnlyList<Line> EffectiveLines => Lines.Count > 0
        ? Lines
        : Number.Length > 0 ? [new Line(MainLineID, Number, SipPassword, Employee)] : [];

    /// <summary>
    /// Идентификатор профиля дополнительного номера — стабильный.
    /// </summary>
    ///
    /// <remarks>
    /// Тот же номер после любой правки в Spark обязан попасть в тот же профиль:
    /// на нём держится история звонков. Как на macOS — UUID версии 5 из первых
    /// 16 байт <c>SHA-256("elitesip.line:" + installation_id + ":" + id)</c>.
    /// </remarks>
    public static Guid LineProfileID(string installationID, string lineID)
    {
        ArgumentNullException.ThrowIfNull(installationID);
        ArgumentNullException.ThrowIfNull(lineID);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"elitesip.line:{installationID}:{lineID}"));
        var bytes = hash.AsSpan(0, 16).ToArray();

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// Проверяет подпись, сверяет машину и открывает конфигурацию.
    /// </summary>
    ///
    /// <param name="data">ответ сервера — подписанная оболочка.</param>
    /// <param name="signer">открытый ключ подписи Spark.</param>
    /// <param name="installationID">свой идентификатор.</param>
    /// <param name="machine">свой ключ машины.</param>
    ///
    /// <exception cref="PanelLinkException">
    /// Подпись не сошлась, формат новее, объект чужой машины или не открылся
    /// своим ключом. Во всех случаях конфигурация отбрасывается целиком.
    /// </exception>
    public static MachineConfig Open(byte[] data, PanelPublicKey signer, string installationID, MachineKeyPair machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var payload = SignedEnvelope.Open(data, signer);

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(payload, WireJson.Options);
        }
        catch (JsonException)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (envelope?.InstallationID is null || envelope.Sealed is null)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (envelope.Format > SupportedFormat)
        {
            throw new PanelLinkException(PanelLinkFailure.BundleTooNew);
        }

        // Подпись верна, а машина чужая — отбросить. Иначе подсунутый объект
        // соседней машины переписал бы этой номер и пароль.
        if (!string.Equals(envelope.InstallationID, installationID, StringComparison.Ordinal))
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        byte[] sealedBytes;
        try
        {
            sealedBytes = Convert.FromBase64String(envelope.Sealed);
        }
        catch (FormatException)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        Sealed? content;
        try
        {
            content = JsonSerializer.Deserialize<Sealed>(Unseal(sealedBytes, machine), WireJson.Options);
        }
        catch (JsonException)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        if (content is null)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        return new MachineConfig
        {
            InstallationID = envelope.InstallationID,
            Revision = envelope.Revision,
            IssuedAt = WireJson.Timestamp(envelope.IssuedAt),
            Employee = content.Employee?.Trim() ?? string.Empty,
            Number = content.Number?.Trim() ?? string.Empty,
            SipPassword = content.SipPassword ?? string.Empty,
            WorkFormat = content.WorkFormat?.Trim() ?? string.Empty,
            PresetID = content.PresetID?.Trim() ?? string.Empty,
            PresetName = content.PresetName?.Trim() ?? string.Empty,
            AdminPassword = content.AdminPassword ?? string.Empty,
            Lines = content.Lines is null
                ? []
                : [.. content.Lines
                    .Where(line => !string.IsNullOrWhiteSpace(line.Number))
                    .Select(line => new Line(
                        line.ID?.Trim() ?? string.Empty,
                        line.Number!.Trim(),
                        line.SipPassword ?? string.Empty,
                        line.Label?.Trim() ?? string.Empty))],
        };
    }

    /// <summary>
    /// Открывает запечатанное Spark'ом (<c>SealForMachine</c>).
    /// </summary>
    ///
    /// <remarks>
    /// <code>
    /// sealed   = эфемерный X25519 (32) ‖ nonce (12) ‖ шифротекст ‖ тег (16)
    /// общий    = X25519(свой закрытый, эфемерный)
    /// ключ AES = HKDF-SHA256(общий, salt = эфемерный ‖ свой открытый, info = "elitesip.pair.v1", 32)
    /// aad      = "ESIPP1"
    /// </code>
    /// </remarks>
    internal static byte[] Unseal(byte[] sealedBytes, MachineKeyPair machine)
    {
        const int header = MachineKeyPair.KeySize + NonceSize;

        if (sealedBytes.Length < header + TagSize)
        {
            throw new PanelLinkException(PanelLinkFailure.KeyDidNotOpen);
        }

        var ephemeral = sealedBytes.AsSpan(0, MachineKeyPair.KeySize);
        var nonce = sealedBytes.AsSpan(MachineKeyPair.KeySize, NonceSize);
        var cipher = sealedBytes.AsSpan(header, sealedBytes.Length - header - TagSize);
        var tag = sealedBytes.AsSpan(sealedBytes.Length - TagSize, TagSize);

        var shared = machine.Agree(ephemeral);
        try
        {
            var salt = new byte[MachineKeyPair.KeySize * 2];
            ephemeral.CopyTo(salt);
            machine.PublicKey.CopyTo(salt, MachineKeyPair.KeySize);

            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Encoding.ASCII.GetBytes(SealInfo));

            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.ASCII.GetBytes(SealAad));
            return plain;
        }
        catch (AuthenticationTagMismatchException error)
        {
            // Не тот ключ машины или испорченный объект. Для машины это одно и
            // то же: прочесть нечего, применять нечего.
            throw new PanelLinkException(PanelLinkFailure.KeyDidNotOpen, error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    private sealed class Envelope
    {
        [JsonPropertyName("format")]
        public int Format { get; init; }

        [JsonPropertyName("installation_id")]
        public string? InstallationID { get; init; }

        [JsonPropertyName("revision")]
        public int Revision { get; init; }

        [JsonPropertyName("issued_at")]
        public string? IssuedAt { get; init; }

        [JsonPropertyName("sealed")]
        public string? Sealed { get; init; }
    }

    private sealed class Sealed
    {
        [JsonPropertyName("employee")]
        public string? Employee { get; init; }

        [JsonPropertyName("number")]
        public string? Number { get; init; }

        [JsonPropertyName("sip_password")]
        public string? SipPassword { get; init; }

        [JsonPropertyName("work_format")]
        public string? WorkFormat { get; init; }

        [JsonPropertyName("preset_id")]
        public string? PresetID { get; init; }

        [JsonPropertyName("preset_name")]
        public string? PresetName { get; init; }

        [JsonPropertyName("admin_password")]
        public string? AdminPassword { get; init; }

        [JsonPropertyName("lines")]
        public List<WireLine>? Lines { get; init; }
    }

    private sealed class WireLine
    {
        [JsonPropertyName("id")]
        public string? ID { get; init; }

        [JsonPropertyName("number")]
        public string? Number { get; init; }

        [JsonPropertyName("sip_password")]
        public string? SipPassword { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }
    }
}
