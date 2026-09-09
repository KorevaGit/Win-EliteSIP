using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EliteSIP.SipCore;

/// <summary>
/// Стук по портам перед регистрацией.
/// </summary>
///
/// <remarks>
/// Шлюз заказчика (MikroTik) держит порты закрытыми и открывает их адресу, от
/// которого пришла условленная последовательность ICMP-пакетов определённой
/// длины. До сих пор её отправлял shell-скрипт, который сотрудник запускал
/// руками один раз за смену. Здесь то же самое делает клиент — вовремя, повторно
/// и с проверяемым результатом.
///
/// <b>Это механизм связности, а не защиты.</b> Стук решает, кого пускают, и
/// ничего не говорит о том, кто может прочитать: SIP и RTP как шли открытым
/// UDP, так и идут. Считать эту функцию закрытием риска перехвата нельзя, и в
/// журнале она поэтому называется стуком, а не «защищённым подключением».
///
/// <b>Чем это отличается от оригинала.</b> На macOS пакет собирался руками и
/// уходил через <c>SOCK_DGRAM</c> с <c>IPPROTO_ICMP</c> — Darwin разрешает такой
/// сокет без привилегий. В Windows непривилегированного ICMP-сокета нет вовсе:
/// raw-сокет требует прав администратора, а требовать их от софтфона нельзя.
/// Поэтому пакет отправляет система через <c>IcmpSendEcho2</c> из
/// <c>iphlpapi.dll</c> — то, что <see cref="Ping"/> и есть. Заголовок собирает
/// она, а мы задаём то единственное, на что смотрит правило на шлюзе, — длину
/// данных.
/// </remarks>
public sealed class PortKnocker : ISipPathOpener, IDisposable
{
    private readonly string _serverHost;
    private readonly PortKnockSequence _sequence;
    private readonly Action<string> _log;
    private readonly PortKnockThrottle _throttle;
    private readonly Ping _ping = new();

    /// <summary>
    /// Отказ, о котором уже сказали в журнал.
    /// </summary>
    ///
    /// <remarks>
    /// Повторять «ICMP недоступен» каждые десять минут бессмысленно: если он
    /// запрещён правилом машины, он запрещён навсегда, а журнал нужен читаемым.
    /// </remarks>
    private bool _reportedFailure;

    private PortKnocker(string serverHost, PortKnockSequence sequence, Action<string> log)
    {
        _serverHost = serverHost;
        _sequence = sequence;
        _log = log;
        _throttle = new PortKnockThrottle(TimeSpan.FromSeconds(sequence.RepeatIntervalSeconds));
    }

    /// <summary>
    /// Создаёт стучащего, только если стучать надо.
    /// </summary>
    ///
    /// <remarks>
    /// <see langword="null"/> для офисного рабочего места — это и есть
    /// требование «стучать, если сотрудник работает не во внутренней сети».
    /// Ветка живёт в одном месте, а не расползается проверками по коду
    /// регистрации.
    /// </remarks>
    public static PortKnocker? ForServer(
        string serverHost,
        WorkplaceSite site,
        PortKnockSequence sequence,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        if (!PortKnockPolicy.NeedsKnocking(serverHost, site) || sequence.IsEmpty)
        {
            return null;
        }

        return new PortKnocker(serverHost, sequence, log);
    }

    /// <summary>Сеть сменилась — прошлый стук больше ничего не значит.</summary>
    public void Invalidate() => _throttle.Invalidate();

    public async Task OpenPathAsync(SipPathOpenReason reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_throttle.ShouldKnock(reason, now))
        {
            return;
        }

        // Отметка ставится до отправки, а не после: иначе повтор регистрации,
        // пришедший в середине семисекундной последовательности, начал бы вторую
        // поверх первой и перемешал порядок пакетов.
        _throttle.RecordKnock(now);

        _log($"стук: {_sequence.PacketCount} пакетов, повод {reason}");

        var sent = 0;
        foreach (var step in _sequence.Steps)
        {
            var host = step.ResolvedHost(_serverHost);

            for (var packet = 0; packet < Math.Max(0, step.Count); packet++)
            {
                if (sent > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_sequence.SpacingSeconds)).ConfigureAwait(false);
                }

                if (!await SendAsync(host, step.PayloadBytes).ConfigureAwait(false))
                {
                    return;
                }

                sent++;
            }
        }

        // Ответы не проверяются, и это не небрежность: правило на шлюзе
        // срабатывает на входящий к нему пакет, а эхо-ответ может быть и
        // запрещён. Единственная настоящая проверка успеха — прошедший следом
        // REGISTER, и её делает регистрация, а не стук. Именно этим он
        // отличается от скрипта, который печатал «Successfully connected!»
        // безусловно.
        _log(sent == _sequence.PacketCount
            ? "стук отправлен"
            : $"стук отправлен частично: {sent} из {_sequence.PacketCount}");
    }

    public void Dispose() => _ping.Dispose();

    /// <summary>
    /// Один пакет. <see langword="false"/> — дальше стучать нечем.
    /// </summary>
    ///
    /// <remarks>
    /// Отсутствие ответа успехом не считается и неудачей тоже: пакет ушёл, а
    /// вернётся ли эхо — не наше дело. Неудача здесь ровно одна — когда система
    /// не дала отправить: нет разрешения на ICMP, не разрешилось имя, нет
    /// маршрута.
    ///
    /// Ожидание ответа при этом задаёт темп: тайм-аут равен паузе между
    /// пакетами, то есть молчащий шлюз растягивает стук ровно на ту же семёрку
    /// секунд, что и отвечающий.
    /// </remarks>
    private async Task<bool> SendAsync(string host, int payloadBytes)
    {
        try
        {
            var timeout = (int)Math.Max(1, _sequence.SpacingSeconds * 1000);
            await _ping.SendPingAsync(host, timeout, PortKnockPayload.Make(payloadBytes))
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception error) when (error is PingException or SocketException
                                          or InvalidOperationException)
        {
            if (!_reportedFailure)
            {
                _reportedFailure = true;
                _log($"стук не отправлен: {error.Message}");
            }

            return false;
        }
    }
}

/// <summary>Данные ICMP-пакета — то единственное, чем стук отличается от стука.</summary>
public static class PortKnockPayload
{
    /// <summary>
    /// Ровно столько байт, сколько просили, заполненных как у <c>ping</c>.
    /// </summary>
    ///
    /// <remarks>
    /// Длина здесь и есть подпись: правило на шлюзе смотрит на размер пакета, а
    /// не на содержимое. Восьмибайтовый заголовок эха к этому числу добавляет
    /// система — так же, как <c>ping -s N</c> шлёт N байт данных и N+8 байт
    /// ICMP.
    ///
    /// Заполнение повторяет <c>ping</c>: байт равен своему номеру по модулю 256.
    /// Содержимое правилу безразлично, но пакет, неотличимый от привычного
    /// шлюзу, дешевле объяснять, чем пакет из нулей.
    /// </remarks>
    public static byte[] Make(int payloadBytes)
    {
        var count = Math.Max(0, payloadBytes);
        var payload = new byte[count];

        for (var index = 0; index < count; index++)
        {
            payload[index] = (byte)index;
        }

        return payload;
    }
}
