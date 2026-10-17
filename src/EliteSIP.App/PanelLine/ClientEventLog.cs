using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteSIP.App.Settings;
using EliteSIP.Diagnostics;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Журнал машины для Spark: регистрация, звонки и критические ошибки.
/// </summary>
///
/// <remarks>
/// <para>
/// Не путать с журналом приложения (<c>elitesip.log</c>): тот подробный и живёт
/// на машине, этот — десяток строк в час, которые администратор видит в
/// карточке места в Spark. События копятся в <c>client-events.json</c> рядом с
/// настройками и раз в пять минут уходят пачкой на канал (<c>POST /logs</c>)
/// помашинной парой, откуда их забирает Spark.
/// </para>
/// <para>
/// <b>Номеров телефонов здесь нет намеренно:</b> журнал читает не только тот,
/// кому положено видеть историю звонков. Секреты маскирует
/// <see cref="LogRedaction"/> — так же, как в файле журнала.
/// </para>
/// <para>
/// Перенесено из macOS-версии (<c>EliteSIP/Services/ClientEventLog.swift</c>,
/// 0.1.60): формат события, сроки и правила отправки те же — сервер у обеих
/// версий один. Пишут сюда несколько потоков (сигнализация, окно), поэтому
/// состояние под замком, а не на главном акторе, как в оригинале.
/// </para>
/// </remarks>
internal sealed class ClientEventLog : IDisposable
{
    internal static ClientEventLog Shared { get; } = new(DefaultPath);

    /// <summary>Сколько событий держим, пока сервер недоступен. Старые вытесняются.</summary>
    internal const int Capacity = 1000;

    /// <summary>Сколько событий в одной пачке — потолок сервера.</summary>
    internal const int BatchSize = 500;

    /// <summary>Сколько символов сообщения уходит на сервер.</summary>
    internal const int MessageLimit = 500;

    internal static readonly TimeSpan SendInterval = TimeSpan.FromMinutes(5);

    /// <summary>Одинаковая ошибка чаще раза в минуту — шум, а не новое событие.</summary>
    internal static readonly TimeSpan ErrorRepeatWindow = TimeSpan.FromMinutes(1);

    internal static string DefaultPath
        => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, FileName);

    internal const string FileName = "client-events.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<Event> _pending = [];
    private readonly Dictionary<string, DateTimeOffset> _lastErrors = [];
    private readonly Dictionary<string, Call> _calls = [];

    private Func<AppSettings>? _settings;
    private Timer? _timer;
    private bool _sending;
    private bool _wasRegistered;

    internal ClientEventLog(string? path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.UtcNow);

        if (path is not null)
        {
            try
            {
                if (File.Exists(path)
                    && JsonSerializer.Deserialize<List<Event>>(File.ReadAllText(path), Format) is { } saved)
                {
                    _pending.AddRange(saved);
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                // Испорченный буфер — то же, что пустой: потеряется несколько
                // событий, а запуск из-за них ронять нельзя.
            }
        }
    }

    internal enum Kind
    {
        Registration,
        Call,
        Error,
    }

    internal enum Level
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>Событие так, как его ждёт сервер: имена полей — его, а не наши.</summary>
    internal sealed record Event
    {
        [JsonPropertyName("ts")]
        public required string Timestamp { get; init; }

        [JsonPropertyName("kind")]
        public required Kind Kind { get; init; }

        [JsonPropertyName("level")]
        public required Level Level { get; init; }

        [JsonPropertyName("message")]
        public required string Message { get; init; }

        [JsonPropertyName("line")]
        public string? Line { get; init; }

        [JsonPropertyName("sip_code")]
        public int? SipCode { get; init; }

        [JsonPropertyName("direction")]
        public string? Direction { get; init; }

        [JsonPropertyName("duration_s")]
        public int? DurationSeconds { get; init; }
    }

    private sealed class Call(string direction)
    {
        public string Direction { get; } = direction;

        public DateTimeOffset? AnsweredAt { get; set; }
    }

    /// <summary>Что попадает в журнал машины от регистрации.</summary>
    internal abstract record RegistrationSummary
    {
        private RegistrationSummary()
        {
        }

        internal sealed record Registered : RegistrationSummary;

        internal sealed record Failed(string Reason, int? Code) : RegistrationSummary;

        internal sealed record Unregistered : RegistrationSummary;
    }

    /// <summary>Все события, ещё не принятые сервером. Для проверок.</summary>
    internal IReadOnlyList<Event> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending];
            }
        }
    }

    /// <summary>
    /// Отправка — через этот вызов. Подменяется в проверках; по умолчанию
    /// общий клиент канала.
    /// </summary>
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpStatusCode?>> Transport { get; init; } = SendOverChannelAsync;

    /// <summary>Запустить такт отправки. Зовётся один раз при запуске приложения.</summary>
    internal void Start(Func<AppSettings> settings)
    {
        lock (_gate)
        {
            _settings = settings;
            _timer?.Dispose();
            _timer = new Timer(_ => _ = SendAsync(), null, SendInterval, SendInterval);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    // События

    internal void NoteRegistration(RegistrationSummary state)
    {
        lock (_gate)
        {
            switch (state)
            {
                case RegistrationSummary.Registered:
                    if (_wasRegistered)
                    {
                        return;
                    }

                    _wasRegistered = true;
                    Record(Kind.Registration, Level.Info, "Регистрация на АТС прошла");
                    break;

                case RegistrationSummary.Failed failed:
                    _wasRegistered = false;
                    Record(Kind.Registration, Level.Error, $"Регистрация не прошла: {failed.Reason}", sipCode: failed.Code);
                    break;

                case RegistrationSummary.Unregistered:
                    if (!_wasRegistered)
                    {
                        return;
                    }

                    _wasRegistered = false;
                    Record(Kind.Registration, Level.Warning, "Снята регистрация на АТС");
                    break;
            }
        }
    }

    internal void CallStarted(string callId, bool incoming)
    {
        lock (_gate)
        {
            _calls[callId] = new Call(incoming ? "in" : "out");
        }
    }

    internal void CallAnswered(string callId)
    {
        lock (_gate)
        {
            if (_calls.TryGetValue(callId, out var call))
            {
                call.AnsweredAt ??= _now();
            }
        }
    }

    internal void CallFinished(string callId, string reason, bool failed)
    {
        lock (_gate)
        {
            if (!_calls.Remove(callId, out var call))
            {
                return;
            }

            var title = call.Direction == "in" ? "Входящий звонок" : "Исходящий звонок";
            if (call.AnsweredAt is { } answered)
            {
                Record(
                    Kind.Call,
                    Level.Info,
                    $"{title} завершён",
                    direction: call.Direction,
                    duration: Math.Max(0, (int)(_now() - answered).TotalSeconds));
            }
            else
            {
                Record(
                    Kind.Call,
                    failed ? Level.Warning : Level.Info,
                    $"{title} без ответа: {reason}",
                    direction: call.Direction);
            }
        }
    }

    internal void NoteError(string message)
    {
        lock (_gate)
        {
            var at = _now();
            if (_lastErrors.TryGetValue(message, out var last) && at - last < ErrorRepeatWindow)
            {
                return;
            }

            _lastErrors[message] = at;
            if (_lastErrors.Count > 100)
            {
                foreach (var stale in _lastErrors.Where(pair => at - pair.Value >= ErrorRepeatWindow).ToList())
                {
                    _lastErrors.Remove(stale.Key);
                }
            }

            Record(Kind.Error, Level.Error, message);
        }
    }

    /// <summary>Под замком.</summary>
    private void Record(
        Kind kind,
        Level level,
        string message,
        int? sipCode = null,
        string? direction = null,
        int? duration = null)
    {
        var text = LogRedaction.Redact(message);
        if (text.Length > MessageLimit)
        {
            text = text[..MessageLimit];
        }

        var line = _settings?.Invoke().Account.Username;

        _pending.Add(new Event
        {
            Timestamp = _now().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Kind = kind,
            Level = level,
            Message = text,
            Line = string.IsNullOrEmpty(line) ? null : line,
            SipCode = sipCode,
            Direction = direction,
            DurationSeconds = duration,
        });

        if (_pending.Count > Capacity)
        {
            _pending.RemoveRange(0, _pending.Count - Capacity);
        }

        Persist();
    }

    // Отправка

    /// <summary>Отправить накопленное одной пачкой. Повторный вызов во время отправки — пустой.</summary>
    internal async Task SendAsync()
    {
        List<Event> batch;
        Uri url;
        string installationID;
        string channelKey;
        int appliedRevision;
        int appliedConfigRevision;

        lock (_gate)
        {
            if (_sending || _pending.Count == 0 || _settings is null)
            {
                return;
            }

            var panel = _settings().Panel;
            if (!panel.HasChannelKey
                || panel.InstallationID.Length == 0
                || panel.ChannelKey() is not { } key
                || Provisioning.Current?.Updates?.LogsUrl() is not { } address)
            {
                return;
            }

            batch = _pending.Take(BatchSize).ToList();
            url = address;
            installationID = panel.InstallationID;
            channelKey = key;
            appliedRevision = panel.AppliedRevision;
            appliedConfigRevision = panel.AppliedConfigRevision;
            _sending = true;
        }

        HttpStatusCode? status = null;
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url);
            ChannelRequest.Authorize(request, installationID, channelKey);
            ChannelRequest.Describe(request, appliedRevision, appliedConfigRevision);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { app_version = ChannelRequest.AppVersion, events = batch }, Format),
                Encoding.UTF8,
                "application/json");

            using CancellationTokenSource deadline = new(ChannelRequest.Timeout);
            status = await Transport(request, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            DidSend(batch.Count, status);
        }
    }

    private void DidSend(int count, HttpStatusCode? status)
    {
        lock (_gate)
        {
            _sending = false;

            // 204 — пачка принята. 400 — её не примут никогда, держать незачем.
            // Остальное (сеть, 429, 5xx) — попробуем в следующий раз.
            if (status is HttpStatusCode.NoContent or HttpStatusCode.BadRequest)
            {
                _pending.RemoveRange(0, Math.Min(count, _pending.Count));
                Persist();
            }
        }
    }

    private static async Task<HttpStatusCode?> SendOverChannelAsync(HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            using var response = await ChannelRequest.Client.SendAsync(request, token).ConfigureAwait(false);
            return response.StatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // Нет связи — обычное состояние: события подождут в буфере.
            return null;
        }
    }

    /// <summary>Под замком.</summary>
    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_pending, Format));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Не записалось — события остаются в памяти и уйдут с ближайшей пачкой.
        }
    }
}
