using System.IO;
using System.Net;
using System.Text.Json;
using EliteSIP.App.PanelLine;

namespace EliteSIP.App.Tests;

/// <summary>
/// Журнал машины для Spark: что попадает в события и что из них уходит на
/// сервер. Отправку саму не проверяем — у неё нет канала без заводской
/// настройки, — проверяем буфер и формат, которые и есть контракт с сервером.
/// </summary>
public sealed class ClientEventLogTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("elitesip-events-").FullName;
    private DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private string FilePath => Path.Combine(_directory, ClientEventLog.FileName);

    private ClientEventLog Make() => new(FilePath, () => _now);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AnsweredCallCarriesDurationAndNoNumber()
    {
        var log = Make();

        log.CallStarted("c1", incoming: true);
        log.CallAnswered("c1");
        _now = _now.AddSeconds(42);
        log.CallFinished("c1", "собеседник положил трубку", failed: false);

        var item = Assert.Single(log.Pending);
        Assert.Equal(ClientEventLog.Kind.Call, item.Kind);
        Assert.Equal("in", item.Direction);
        Assert.Equal(42, item.DurationSeconds);
        Assert.Equal("Входящий звонок завершён", item.Message);
    }

    [Fact]
    public void UnansweredFailedCallIsWarning()
    {
        var log = Make();

        log.CallStarted("c1", incoming: false);
        log.CallFinished("c1", "занято", failed: true);

        var item = Assert.Single(log.Pending);
        Assert.Equal(ClientEventLog.Level.Warning, item.Level);
        Assert.Null(item.DurationSeconds);
        Assert.Equal("Исходящий звонок без ответа: занято", item.Message);
    }

    [Fact]
    public void UnknownCallFinishIsIgnored()
    {
        var log = Make();

        log.CallFinished("never-started", "отбой", failed: false);

        Assert.Empty(log.Pending);
    }

    [Fact]
    public void RegistrationIsLoggedOnTransitionsOnly()
    {
        var log = Make();

        // Снятие без регистрации — не событие.
        log.NoteRegistration(new ClientEventLog.RegistrationSummary.Unregistered(ByOperator: false));
        log.NoteRegistration(new ClientEventLog.RegistrationSummary.Registered());

        // Продление идущей регистрации — тоже.
        log.NoteRegistration(new ClientEventLog.RegistrationSummary.Registered());
        log.NoteRegistration(new ClientEventLog.RegistrationSummary.Unregistered(ByOperator: false));
        log.NoteRegistration(new ClientEventLog.RegistrationSummary.Failed("неверный пароль", 403));

        Assert.Collection(
            log.Pending,
            item => Assert.Equal(ClientEventLog.Level.Info, item.Level),
            item => Assert.Equal(ClientEventLog.Level.Warning, item.Level),
            item =>
            {
                Assert.Equal(ClientEventLog.Level.Error, item.Level);
                Assert.Equal(403, item.SipCode);
            });
    }

    [Fact]
    public void RepeatedErrorWithinMinuteIsDropped()
    {
        var log = Make();

        log.NoteError("сокет закрыт");
        _now = _now.AddSeconds(30);
        log.NoteError("сокет закрыт");
        _now = _now.AddSeconds(31);
        log.NoteError("сокет закрыт");

        Assert.Equal(2, log.Pending.Count);
    }

    [Fact]
    public void SecretsAreRedacted()
    {
        var log = Make();

        log.NoteError("Authorization: Digest username=\"205\", response=\"abc123\"");

        var item = Assert.Single(log.Pending);
        Assert.DoesNotContain("abc123", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BufferIsCappedOldestFirst()
    {
        var log = Make();

        for (var index = 0; index < ClientEventLog.Capacity + 5; index++)
        {
            _now = _now.AddMinutes(2);
            log.NoteError($"ошибка {index}");
        }

        Assert.Equal(ClientEventLog.Capacity, log.Pending.Count);
        Assert.Equal("ошибка 5", log.Pending[0].Message);
    }

    [Fact]
    public void EventsSurviveRestart()
    {
        Make().NoteError("до перезапуска");

        var item = Assert.Single(Make().Pending);
        Assert.Equal("до перезапуска", item.Message);
    }

    [Fact]
    public void FileUsesServerFieldNames()
    {
        var log = Make();
        log.CallStarted("c1", incoming: false);
        log.CallAnswered("c1");
        _now = _now.AddSeconds(5);
        log.CallFinished("c1", "отбой", failed: false);

        using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
        var item = document.RootElement[0];

        Assert.Equal("2026-10-17T12:00:05Z", item.GetProperty("ts").GetString());
        Assert.Equal("call", item.GetProperty("kind").GetString());
        Assert.Equal("info", item.GetProperty("level").GetString());
        Assert.Equal("out", item.GetProperty("direction").GetString());
        Assert.Equal(5, item.GetProperty("duration_s").GetInt32());

        // Пустые поля не пишутся: сервер отличает «нет кода» от нуля.
        Assert.False(item.TryGetProperty("sip_code", out _));
    }

    [Fact]
    public void OperatorGoingOfflineIsInfoNotWarning()
    {
        var log = Make();

        log.NoteRegistration(new ClientEventLog.RegistrationSummary.Registered());
        log.NoteRegistration(new ClientEventLog.RegistrationSummary.Unregistered(ByOperator: true));

        var item = log.Pending[^1];
        Assert.Equal(ClientEventLog.Level.Info, item.Level);
        Assert.Equal("Оператор отключился от АТС", item.Message);
    }

    [Fact]
    public void ShutdownReasonSignsTheUnregistration()
    {
        var log = Make();

        log.NoteRegistration(new ClientEventLog.RegistrationSummary.Registered());

        // Первая названная причина главнее: обновление закрывает приложение
        // тем же выходом, что и кнопка.
        log.NoteShutdown("установка обновления");
        log.NoteShutdown("выход из приложения");
        log.NoteRegistration(new ClientEventLog.RegistrationSummary.Unregistered(ByOperator: false));

        var item = log.Pending[^1];
        Assert.Equal(ClientEventLog.Level.Info, item.Level);
        Assert.Equal("Регистрация снята: установка обновления", item.Message);
    }

    [Fact]
    public void BatchRespectsCountAndSize()
    {
        var log = Make();
        var text = new string('ж', ClientEventLog.MessageLimit);
        for (var index = 0; index < ClientEventLog.MaxBatchEvents; index++)
        {
            _now = _now.AddMinutes(2);
            log.NoteError(text + index);
        }

        var pending = log.Pending;

        // Пятьсот сообщений по пятьсот знаков кириллицы — вдвое больше предела
        // сервера: пачка обязана остановиться по объёму, а не по счёту.
        var batch = ClientEventLog.Batch(pending, ClientEventLog.MaxBatchEvents, ClientEventLog.MaxBatchBytes);
        Assert.InRange(batch.Count, 1, ClientEventLog.MaxBatchEvents - 1);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(batch).Length <= 256 << 10);

        Assert.Equal(3, ClientEventLog.Batch(pending, 3, ClientEventLog.MaxBatchBytes).Count);

        // Первое событие берётся всегда, иначе буфер не продвинется.
        Assert.Single(ClientEventLog.Batch(pending, ClientEventLog.MaxBatchEvents, maxBytes: 1));
    }

    [Fact]
    public void ForgetStopsWritingTheFile()
    {
        var log = Make();
        log.NoteError("до сброса");

        log.Forget();
        File.Delete(FilePath);
        log.NoteError("после сброса");

        Assert.Empty(log.Pending);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task SendWithoutStartDoesNothing()
    {
        var calls = 0;
        var log = new ClientEventLog(FilePath, () => _now)
        {
            Transport = (_, _) =>
            {
                calls++;
                return Task.FromResult(new ClientEventLog.SendResult(HttpStatusCode.NoContent, null));
            },
        };

        log.NoteError("ошибка");
        await log.SendAsync();

        Assert.Equal(0, calls);
        Assert.Single(log.Pending);
    }
}
