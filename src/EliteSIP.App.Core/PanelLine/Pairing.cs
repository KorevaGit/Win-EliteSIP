using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteSIP.AdminAccess;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Что машина хранит до привязки: свои ключи и открытую сессию.
/// </summary>
///
/// <remarks>
/// <para>
/// Отдельным файлом (<c>pairing.json</c>), а не в настройках: мастер идёт до
/// того, как машина «настроена», и сессия должна пережить перезапуск —
/// иначе код на экране менялся бы при каждом запуске, а администратор вводил
/// бы в Spark уже мёртвый. Ключи и секрет опроса лежат под DPAPI.
/// </para>
/// <para>
/// После привязки ключи переезжают в настройки панели, а файл стирается.
/// Сброс машины стирает его в любом случае.
/// </para>
/// </remarks>
internal sealed class PairingState
{
    public string? ProtectedMachineKey { get; set; }

    public string? ProtectedChannelKey { get; set; }

    public string? SessionID { get; set; }

    public string? ProtectedPollSecret { get; set; }

    public string? Code { get; set; }

    public string? Qr { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonIgnore]
    public bool HasSession => !string.IsNullOrEmpty(SessionID) && !string.IsNullOrEmpty(ProtectedPollSecret);

    public MachineKeyPair? MachineKey()
        => Unprotect(ProtectedMachineKey) is { } key ? MachineKeyPair.FromBase64(key) : null;

    public string? ChannelKey() => Unprotect(ProtectedChannelKey);

    public string? PollSecret() => Unprotect(ProtectedPollSecret);

    public void Forget()
    {
        SessionID = null;
        ProtectedPollSecret = null;
        Code = null;
        Qr = null;
        ExpiresAt = null;
    }

    public void Remember(PairSession session)
    {
        SessionID = session.SessionID;
        ProtectedPollSecret = ProtectedSecret.Protect(session.PollSecret);
        Code = session.Code;
        Qr = session.Qr;
        ExpiresAt = session.ExpiresAt;
    }

    /// <summary>
    /// Ключи машины: сохранённые или новые. Новые сохраняются сразу — до
    /// первого запроса, чтобы повтор после потерянного ответа предъявил тот же
    /// ключ, что уже знает Spark.
    /// </summary>
    public (MachineKeyPair Machine, string Channel) EnsureKeys()
    {
        var machine = MachineKey();
        var channel = ChannelKey();

        if (machine is null || string.IsNullOrEmpty(channel))
        {
            machine = MachineKeyPair.Generate();
            channel = PanelLink.ChannelKey.Generate();

            ProtectedMachineKey = ProtectedSecret.Protect(machine.PrivateKeyBase64);
            ProtectedChannelKey = ProtectedSecret.Protect(channel);

            // Сессия, открытая под прежними ключами, с новыми не сходится.
            Forget();
            PairingStore.Save(this);
        }

        return (machine, channel);
    }

    private static string? Unprotect(string? value)
        => string.IsNullOrEmpty(value) ? null : ProtectedSecret.Unprotect(value);
}

/// <summary>Файл <c>pairing.json</c> рядом с настройками.</summary>
internal static class PairingStore
{
    internal const string FileName = "pairing.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    internal static string Path => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Settings.AppSettings.DefaultPath)!, FileName);

    internal static PairingState Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<PairingState>(File.ReadAllText(Path)) ?? new PairingState()
                : new PairingState();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // Испорченный файл — новые ключи и новый код. Хуже было бы не
            // показать код вовсе.
            return new PairingState();
        }
    }

    internal static void Save(PairingState state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Format));
        File.Move(temporary, Path, overwrite: true);
    }

    internal static void Clear()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Останется до сброса машины: ключи в нём под DPAPI этой учётки.
        }
    }
}

/// <summary>Открытая сессия привязки.</summary>
internal sealed record PairSession(string SessionID, string PollSecret, string Code, string Qr, DateTimeOffset ExpiresAt);

/// <summary>Что ответил опрос.</summary>
internal sealed record PairStatus(
    PairState State,
    string? InstallationID,
    string? Label,
    string? Extension,
    string? Code,
    DateTimeOffset? ExpiresAt);

internal enum PairState
{
    Waiting,
    Claimed,
    Delivered,

    /// <summary>Истекла, или сессии нет, или секрет не тот (404). Открыть новую.</summary>
    Expired,
}

/// <summary>Чем кончилась регистрация ключа машины, поднятой ключом активации.</summary>
internal enum MachineRegistration
{
    Registered,

    /// <summary>401: ключ канала не тот или машину отвязали.</summary>
    Rejected,

    /// <summary>Сеть или сервер. Повторить на следующем запуске тем же ключом.</summary>
    Failed,
}

/// <summary>Spark не принял запрос — со словами, которые он сам прислал.</summary>
internal sealed class PairingException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Запросы привязки к Spark: <c>/api/pair/…</c>. Контракт —
/// <c>docs/elitesip-qr-pair.md</c> в репозитории Spark.
/// </summary>
internal static class SparkPairing
{
    /// <summary>
    /// Сколько ждать длинный опрос. Spark отвечает не позже чем через 25 с;
    /// срок запроса больше, иначе обычное «ещё ждём» выглядело бы обрывом.
    /// </summary>
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(40);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static Uri Base => Provisioning.Current?.PairBase ?? new Uri(Provisioning.DefaultPairUrl);

    /// <summary>Открывает сессию: код и QR для экрана.</summary>
    internal static async Task<PairSession> StartAsync(
        MachineKeyPair machine, string channelKey, CancellationToken cancellation)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(Base, "api/pair/sessions"))
        {
            Content = JsonContent.Create(new
            {
                public_key = machine.PublicKeyBase64,
                channel_key_hash = PanelLink.ChannelKey.Hash(channelKey),
                device_name = Environment.MachineName,
                app_version = ChannelRequest.AppVersion,
            }),
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ChannelRequest.Timeout);

        using var response = await ChannelRequest.Client.SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw await RefusalAsync(response, deadline.Token).ConfigureAwait(false);
        }

        var wire = await response.Content.ReadFromJsonAsync<StartWire>(Json, deadline.Token).ConfigureAwait(false);
        if (wire?.SessionID is null || wire.PollSecret is null || wire.Code is null)
        {
            throw new PairingException("Spark прислал сессию без кода");
        }

        return new PairSession(wire.SessionID, wire.PollSecret, wire.Code, wire.Qr ?? string.Empty, wire.ExpiresAt);
    }

    /// <summary>Длинный опрос: ответ приходит не позже чем через 25 с.</summary>
    internal static async Task<PairStatus> PollAsync(PairSession session, CancellationToken cancellation)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(Base, $"api/pair/sessions/{Uri.EscapeDataString(session.SessionID)}?wait=1"));
        request.Headers.TryAddWithoutValidation("Authorization", "Pair " + session.PollSecret);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(PollTimeout);

        using var response = await ChannelRequest.Client.SendAsync(request, deadline.Token).ConfigureAwait(false);

        // Сессии нет или секрет не тот — для машины одно и то же: открыть новую
        // теми же ключами.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new PairStatus(PairState.Expired, null, null, null, null, null);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await RefusalAsync(response, deadline.Token).ConfigureAwait(false);
        }

        var wire = await response.Content.ReadFromJsonAsync<PollWire>(Json, deadline.Token).ConfigureAwait(false);

        var state = wire?.State switch
        {
            "claimed" => PairState.Claimed,
            "delivered" => PairState.Delivered,
            "expired" => PairState.Expired,
            _ => PairState.Waiting,
        };

        return new PairStatus(state, wire?.InstallationID, wire?.Label, wire?.Extension, wire?.Code, wire?.ExpiresAt);
    }

    /// <summary>Подтверждает, что конфигурация забрана.</summary>
    internal static async Task AckAsync(PairSession session, CancellationToken cancellation)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(Base, $"api/pair/sessions/{Uri.EscapeDataString(session.SessionID)}/ack"));
        request.Headers.TryAddWithoutValidation("Authorization", "Pair " + session.PollSecret);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ChannelRequest.Timeout);

        using var response = await ChannelRequest.Client.SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            throw await RefusalAsync(response, deadline.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Машина, поднятая ключом активации, регистрирует свой ключ машины.
    /// </summary>
    ///
    /// <remarks>
    /// Ключ сохраняется <b>до</b> вызова, и на повторе предъявляется тот же:
    /// Spark принимает его один раз, и машина, приславшая после потерянного
    /// ответа новый, осталась бы без конфигурации.
    /// </remarks>
    internal static async Task<MachineRegistration> RegisterMachineAsync(
        string installationID, string channelKey, MachineKeyPair machine, CancellationToken cancellation)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(Base, "api/pair/machine"))
        {
            Content = JsonContent.Create(new
            {
                public_key = machine.PublicKeyBase64,
                device_name = Environment.MachineName,
            }),
        };
        ChannelRequest.Authorize(request, installationID, channelKey);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ChannelRequest.Timeout);

        try
        {
            using var response = await ChannelRequest.Client.SendAsync(request, deadline.Token).ConfigureAwait(false);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => MachineRegistration.Registered,
                HttpStatusCode.Unauthorized => MachineRegistration.Rejected,
                _ => MachineRegistration.Failed,
            };
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return MachineRegistration.Failed;
        }
    }

    private static async Task<PairingException> RefusalAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        string? message = null;
        try
        {
            message = (await response.Content.ReadFromJsonAsync<ErrorWire>(Json, cancellation).ConfigureAwait(false))?.Error;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or HttpRequestException)
        {
            // Тело не JSON — хватит кода ответа.
        }

        return new PairingException(
            message ?? $"Spark ответил {(int)response.StatusCode}",
            response.StatusCode);
    }

    private sealed class StartWire
    {
        [JsonPropertyName("session_id")]
        public string? SessionID { get; init; }

        [JsonPropertyName("poll_secret")]
        public string? PollSecret { get; init; }

        [JsonPropertyName("code")]
        public string? Code { get; init; }

        [JsonPropertyName("qr")]
        public string? Qr { get; init; }

        [JsonPropertyName("expires_at")]
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private sealed class PollWire
    {
        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("code")]
        public string? Code { get; init; }

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; init; }

        [JsonPropertyName("installation_id")]
        public string? InstallationID { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("extension")]
        public string? Extension { get; init; }
    }

    private sealed class ErrorWire
    {
        [JsonPropertyName("detail")]
        public string? Error { get; init; }
    }
}
