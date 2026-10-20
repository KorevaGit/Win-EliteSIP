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
/// Журнал машины для Spark: регистрация, звонки, конфигурация и критические
/// ошибки.
/// </summary>
///
/// <remarks>
/// <para>
/// Не путать с журналом приложения (<c>elitesip.log</c>): тот подробный и живёт
/// на машине, этот — десяток строк в час, которые администратор видит в
/// карточке места в Spark. События копятся в <c>client-events.json</c> рядом с
/// настройками и уходят пачкой на канал (<c>POST /logs</c>) помашинной парой,
/// откуда их забирает Spark.
/// </para>
/// <para>
/// <b>Номеров собеседников здесь нет намеренно:</b> журнал читает не только
/// тот, кому положено видеть историю звонков. Секреты маскирует
/// <see cref="LogRedaction"/> — так же, как в файле журнала.
/// </para>
/// <para>
/// Перенесено из macOS-версии (<c>EliteSIP/Services/ClientEventLog.swift</c>,
/// 0.1.66): формат события, сроки и правила отправки те же — сервер у обеих
/// версий один. Пишут сюда несколько потоков (сигнализация, окно), поэтому
/// состояние под замком, а не на главном акторе, как в оригинале.
/// </para>
/// </remarks>
internal sealed class ClientEventLog : IDisposable
{
    internal static ClientEventLog Shared { get; } = new(DefaultPath);

    /// <summary>Сколько событий держим, пока сервер недоступен. Старые вытесняются.</summary>
    internal const int Capacity = 1000;

    /// <summary>Больше событий в пачке сервер не примет (<c>maxLogEvents</c>).</summary>
    internal const int MaxBatchEvents = 500;

    /// <summary>
    /// Предел пачки по объёму — с запасом под сервер (<c>maxLogBatch</c>, 256 КБ).
    /// </summary>
    ///
    /// <remarks>
    /// Счёта событий мало: сообщение до 500 знаков кириллицы — около килобайта,
    /// и пятьсот таких дают пачку вдвое больше предела. На macOS до 17 октября
    /// 2026 сервер отвечал на неё 413, а клиент повторял ту же пачку без конца —
    /// журнал машины вставал навсегда.
    /// </remarks>
    internal const int MaxBatchBytes = 200 << 10;

    /// <summary>Сколько символов сообщения уходит на сервер.</summary>
    internal const int MessageLimit = 500;

    /// <summary>Общий такт: подбирает то, что не ушло само.</summary>
    internal static readonly TimeSpan SendInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Событие уходит через несколько секунд, а не с общим тактом: тот, кто
    /// смотрит журнал в Spark, проверяет то, что только что сделал. Пауза
    /// собирает в одну пачку то, что идёт подряд, — снятие и новую регистрацию.
    /// </summary>
    internal static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(10);

    /// <summary>Одинаковая ошибка чаще раза в минуту — шум, а не новое событие.</summary>
    internal static readonly TimeSpan ErrorRepeatWindow = TimeSpan.FromMinutes(1);

    internal const string FileName = "client-events.json";

    internal static string DefaultPath
        => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, FileName);

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
    private Action<string>? _log;
    private Timer? _timer;
    private IDisposable? _flush;
    private bool _sending;
    private bool _wasRegistered;
    private bool _forgotten;

    /// <summary>Пауза перед повтором после неудачной отправки; растёт до такта.</summary>
    private TimeSpan _retryDelay = TimeSpan.Zero;

    /// <summary>
    /// Сколько событий пробовать в следующей пачке. Делится пополам на 413 и
    /// возвращается к полному после удачной отправки.
    /// </summary>
    private int _batchLimit = MaxBatchEvents;

    /// <summary>
    /// Почему приложение само снимает регистрацию: обновление или выход. Без
    /// подписи такое снятие выглядело в Spark как сбой.
    /// </summary>
    private string? _shutdownReason;

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
        Config,
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

    /// <summary>Чем кончилась отправка: код ответа (<c>null</c> — ответа нет) и просьба сервера подождать.</summary>
    internal readonly record struct SendResult(HttpStatusCode? Status, TimeSpan? RetryAfter);

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

        /// <param name="ByOperator">Оператор сам ушёл с линии, а не АТС или сеть его сняли.</param>
        internal sealed record Unregistered(bool ByOperator) : RegistrationSummary;
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
    internal Func<HttpRequestMessage, CancellationToken, Task<SendResult>> Transport { get; init; } = SendOverChannelAsync;

    /// <summary>
    /// Откладывает отправку на срок. Подменяется в проверках, чтобы не ждать
    /// настоящих секунд; по умолчанию одноразовый таймер.
    /// </summary>
    internal Func<TimeSpan, Action, IDisposable> Schedule { get; init; } =
        (delay, action) => new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan);

    /// <summary>Запустить такт отправки. Зовётся один раз при запуске приложения.</summary>
    /// <param name="settings">настройки машины: ключ канала и номер.</param>
    /// <param name="log">куда писать исход отправки — в журнал приложения.</param>
    internal void Start(Func<AppSettings> settings, Action<string> log)
    {
        lock (_gate)
        {
            _settings = settings;
            _log = log;
            _timer?.Dispose();
            _timer = new Timer(_ => _ = SendAsync(), null, SendInterval, SendInterval);

            // Накопленное до запуска (или пока сервер был недоступен) — не ждать такта.
            if (_pending.Count > 0)
            {
                ScheduleFlush(FlushDelay);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _flush?.Dispose();
            _flush = null;
        }
    }

    /// <summary>
    /// Сброс машины: забыть всё и больше не писать файл.
    /// </summary>
    ///
    /// <remarks>
    /// Файл стирает <see cref="MachineReset"/>, но буфер в памяти пережил бы
    /// чистку: отправка, вернувшаяся после неё, записала бы его обратно, и
    /// события прежней машины ушли бы от имени новой.
    /// </remarks>
    internal void Forget()
    {
        Dispose();

        lock (_gate)
        {
            _pending.Clear();
            _calls.Clear();
            _settings = null;
            _forgotten = true;
        }
    }

    // События

    /// <summary>Почему регистрацию сейчас снимет само приложение. Первая причина главнее.</summary>
    internal void NoteShutdown(string reason)
    {
        lock (_gate)
        {
            _shutdownReason ??= reason;
        }
    }

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
                    RecordLocked(Kind.Registration, Level.Info, "Регистрация на АТС прошла");
                    break;

                case RegistrationSummary.Failed failed:
                    _wasRegistered = false;
                    RecordLocked(Kind.Registration, Level.Error, $"Регистрация не прошла: {failed.Reason}", sipCode: failed.Code);
                    break;

                case RegistrationSummary.Unregistered unregistered:
                    if (!_wasRegistered)
                    {
                        return;
                    }

                    _wasRegistered = false;
                    if (_shutdownReason is { } reason)
                    {
                        RecordLocked(Kind.Registration, Level.Info, $"Регистрация снята: {reason}");
                    }
                    else if (unregistered.ByOperator)
                    {
                        RecordLocked(Kind.Registration, Level.Info, "Оператор отключился от АТС");
                    }
                    else
                    {
                        RecordLocked(Kind.Registration, Level.Warning, "Снята регистрация на АТС");
                    }

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
                RecordLocked(
                    Kind.Call,
                    Level.Info,
                    $"{title} завершён",
                    direction: call.Direction,
                    duration: Math.Max(0, (int)(_now() - answered).TotalSeconds));
            }
            else
            {
                RecordLocked(
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

            RecordLocked(Kind.Error, Level.Error, message);
        }
    }

    /// <summary>Записать событие. Для конфигурации и прочего, у чего своей формы нет.</summary>
    internal void Record(Kind kind, Level level, string message)
    {
        lock (_gate)
        {
            RecordLocked(kind, level, message);
        }
    }

    private void RecordLocked(
        Kind kind,
        Level level,
        string message,
        int? sipCode = null,
        string? direction = null,
        int? duration = null)
    {
        if (_forgotten)
        {
            return;
        }

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
        ScheduleFlush(FlushDelay);
    }

    // Отправка

    /// <summary>Под замком. Уже назначенная отправка не переназначается.</summary>
    private void ScheduleFlush(TimeSpan delay)
    {
        if (_flush is not null || _settings is null)
        {
            return;
        }

        _flush = Schedule(delay, () =>
        {
            lock (_gate)
            {
                _flush?.Dispose();
                _flush = null;
            }

            _ = SendAsync();
        });
    }

    /// <summary>
    /// Начало буфера, которое влезает в пачку и по числу, и по объёму.
    /// </summary>
    ///
    /// <remarks>
    /// Объём считается по тем же байтам, что уйдут в теле, плюс запятая на
    /// событие. Первое событие берётся всегда: пустая пачка не продвинула бы
    /// буфер, а одно событие предела не превысит — сообщение обрезано до 500
    /// знаков ещё при записи.
    /// </remarks>
    internal static List<Event> Batch(IReadOnlyList<Event> pending, int limit, int maxBytes)
    {
        var batch = new List<Event>();
        var bytes = 0;

        foreach (var item in pending.Take(Math.Max(1, limit)))
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(item, Format).Length + 1;
            if (batch.Count > 0 && bytes + size > maxBytes)
            {
                break;
            }

            batch.Add(item);
            bytes += size;
        }

        return batch;
    }

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

            batch = Batch(_pending, _batchLimit, MaxBatchBytes);
            url = address;
            installationID = panel.InstallationID;
            channelKey = key;
            appliedRevision = panel.AppliedRevision;
            appliedConfigRevision = panel.AppliedConfigRevision;
            _sending = true;
        }

        SendResult result = default;
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
            result = await Transport(request, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            DidSend(batch.Count, result);
        }
    }

    private void DidSend(int count, SendResult result)
    {
        string? note;

        lock (_gate)
        {
            _sending = false;
            note = Settle(count, result);
        }

        if (note is not null)
        {
            _log?.Invoke(note);
        }
    }

    /// <summary>Под замком. Отдаёт строку для журнала приложения, если о чём сказать.</summary>
    private string? Settle(int count, SendResult result)
    {
        var status = result.Status;

        // 413 — пачка велика. Делим пополам и шлём сразу, без паузы: сеть жива,
        // сервер ответил. Одно событие, которое всё равно не влезло, держать
        // незачем — оно не пройдёт никогда.
        if (status is HttpStatusCode.RequestEntityTooLarge)
        {
            if (count > 1)
            {
                _batchLimit = Math.Max(1, count / 2);
                ScheduleFlush(TimeSpan.FromSeconds(1));
                return $"журнал машины: пачка из {count} велика (413), следующая — {_batchLimit}";
            }

            Drop(count);
            if (_pending.Count > 0)
            {
                ScheduleFlush(TimeSpan.FromSeconds(1));
            }

            return "журнал машины: событие не влезает в пачку (413) и выброшено";
        }

        // 204 — пачка принята. 400 — её не примут никогда, держать незачем.
        if (status is HttpStatusCode.NoContent or HttpStatusCode.BadRequest)
        {
            Drop(count);
            _retryDelay = TimeSpan.Zero;
            _batchLimit = MaxBatchEvents;

            // Пока шла отправка, могли прийти новые.
            if (_pending.Count > 0)
            {
                ScheduleFlush(FlushDelay);
            }

            return status is HttpStatusCode.BadRequest
                ? $"журнал машины: сервер отверг пачку (400), событий выброшено: {count}"
                : null;
        }

        // Сеть, 429, 5xx — повтор с нарастающей паузой, а не через пять минут.
        var doubled = _retryDelay + _retryDelay;
        _retryDelay = doubled < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : doubled;
        if (_retryDelay > SendInterval)
        {
            _retryDelay = SendInterval;
        }

        // Сервер сам говорит, когда приходить (429 при переполненной очереди
        // сайта — 900 с). Раньше этого срока повтор только тратит трафик.
        if (result.RetryAfter is { } wait && wait > _retryDelay)
        {
            _retryDelay = wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
        }

        ScheduleFlush(_retryDelay);

        var code = status is { } known ? ((int)known).ToString(CultureInfo.InvariantCulture) : "нет ответа";
        return $"журнал машины: отправка не прошла ({code}), повтор через {(int)_retryDelay.TotalSeconds} с, в буфере {_pending.Count}";
    }

    /// <summary>Под замком.</summary>
    private void Drop(int count)
    {
        _pending.RemoveRange(0, Math.Min(count, _pending.Count));
        Persist();
    }

    private static async Task<SendResult> SendOverChannelAsync(HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            using var response = await ChannelRequest.Client.SendAsync(request, token).ConfigureAwait(false);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            return new SendResult(response.StatusCode, retryAfter);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // Нет связи — обычное состояние: события подождут в буфере.
            return new SendResult(null, null);
        }
    }

    /// <summary>Под замком.</summary>
    private void Persist()
    {
        if (_path is null || _forgotten)
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
