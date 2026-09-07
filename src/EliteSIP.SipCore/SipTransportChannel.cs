namespace EliteSIP.SipCore;

/// <summary>Что случилось с каналом доставки.</summary>
public abstract record SipTransportEvent
{
    private SipTransportEvent()
    {
    }

    /// <summary>Канал готов, известен локальный адрес. Он нужен для Via и Contact.</summary>
    public sealed record Ready(SipEndpoint Local) : SipTransportEvent;

    /// <summary>
    /// Одно целое сообщение. Транспорт сам решает задачу нарезки: на UDP это
    /// датаграмма, на TLS — работа фреймера. Выше уровнем разницы не видно.
    /// </summary>
    public sealed record Received(ReadOnlyMemory<byte> Data) : SipTransportEvent;

    /// <summary>
    /// Канал отказал, но жив: реализация повторит попытку сама.
    ///
    /// Так выглядит пропавшая сеть, ещё не разрешившееся имя, недоступный
    /// маршрут. Ничего закрывать не надо и ничего пересобирать тоже — надо
    /// сказать вслух и ждать. Поток событий после этого продолжается, и за
    /// отказом вполне может прийти <see cref="Ready"/>.
    /// </summary>
    public sealed record Failed(string Reason) : SipTransportEvent;

    /// <summary>
    /// Канал закрылся насовсем и сам не оживёт.
    ///
    /// Отличать это от <see cref="Failed"/> пришлось не ради стройности. В
    /// оригинале соединение в состоянии «failed» не поднималось никаким
    /// перезапуском, а поток событий после него заканчивался — то есть канал
    /// мёртв, и об этом никто не узнавал: регистрация продолжала ходить по
    /// кругу с backoff, каждая попытка падала мгновенно, и рабочее место молча
    /// выпадало из раздачи до перезапуска приложения. Единственное лекарство —
    /// пересобрать транспорт целиком, а сделать это может только тот, кто его
    /// создавал.
    ///
    /// Своим <c>Stop</c> это событие не порождается: там <see cref="Cancelled"/>.
    /// </summary>
    public sealed record Closed(string Reason) : SipTransportEvent;

    public sealed record Cancelled : SipTransportEvent;
}

/// <summary>
/// Канал доставки SIP-сообщений.
///
/// Интерфейс существует ради двух вещей: подменить сеть в тестах и заменить
/// реализацию транспорта, не трогая ни транзакции, ни регистрацию.
/// </summary>
public interface ISipTransportChannel
{
    public SipTransport Transport { get; }

    public SipEndpoint Remote { get; }

    public IAsyncEnumerable<SipTransportEvent> Events { get; }

    public Task StartAsync();

    public Task SendAsync(ReadOnlyMemory<byte> data);

    public Task StopAsync();
}

/// <summary>Как проверять сертификат сервера на TLS.</summary>
public abstract record SipTlsTrust
{
    private SipTlsTrust()
    {
    }

    /// <summary>Обычная системная проверка. Единственный правильный режим для боя.</summary>
    public sealed record System : SipTlsTrust;

    /// <summary>
    /// Доверять сертификату с указанным отпечатком SHA-256 (DER сертификата).
    /// Нужен для самоподписанного сертификата лаборатории.
    /// </summary>
    public sealed record PinnedCertificateSha256(IReadOnlySet<string> Fingerprints) : SipTlsTrust;

    /// <summary>
    /// Принимать любой сертификат.
    ///
    /// Это отключение защиты от MITM: перехватчик сможет прочитать пароль от SIP
    /// и разговор целиком. Допустимо только против localhost в лаборатории,
    /// поэтому режим назван так, чтобы его нельзя было включить случайно, и в
    /// интерфейсе он живёт под отдельным предупреждением.
    /// </summary>
    public sealed record AcceptAnyCertificateInsecurely : SipTlsTrust;
}
