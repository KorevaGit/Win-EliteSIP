using System.Net.NetworkInformation;

namespace EliteSIP.SipCore;

/// <summary>
/// Отправка одного эхо-запроса ICMP заданной длины — то, чем стучит
/// <see cref="PortKnocker"/>.
/// </summary>
///
/// <remarks>
/// <para>
/// Отправка зависит от ОС сильнее, чем кажется по <see cref="Ping"/>. На
/// Windows он зовёт <c>IcmpSendEcho2</c>, и прав не нужно. На Linux у
/// <see cref="Ping"/> три пути: raw-сокет (нужен <c>CAP_NET_RAW</c>),
/// ICMP-сокет без прав (только если группа пользователя входит в
/// <c>net.ipv4.ping_group_range</c>) и запуск системного <c>ping</c>. На
/// Linux Mint 22 диапазон пустой (<c>1 0</c>), остаётся третий путь, а на нём
/// .NET 10 данные своего содержимого не принимает вовсе:
/// <see cref="PlatformNotSupportedException"/> «Unable to send custom ping
/// payload» (замер 21 октября 2026). Стук на Linux через <see cref="Ping"/>
/// не уходил бы ни разу.
/// </para>
/// <para>
/// Поэтому отправка — за интерфейсом, а Linux-реализация живёт в своём
/// проекте (правило 7 плана переноса). Шлюзу важна только длина данных, и
/// любой путь, который её соблюдает, годится.
/// </para>
/// </remarks>
public interface IIcmpEcho : IDisposable
{
    /// <summary>
    /// Отправить эхо-запрос с <paramref name="payloadBytes"/> байтами данных и
    /// дождаться ответа не дольше <paramref name="timeout"/>.
    /// </summary>
    ///
    /// <remarks>
    /// Ответ не возвращается: стуку он безразличен (<see cref="PortKnocker"/>).
    /// Исключение — только когда система не дала отправить: нет прав, не
    /// разрешилось имя, нет маршрута, нечем отправлять. Его вид —
    /// <see cref="PingException"/>, как у <see cref="Ping"/>.
    /// </remarks>
    public Task SendAsync(string host, int payloadBytes, TimeSpan timeout);
}

/// <summary>Эхо через <see cref="Ping"/> — путь Windows и прежнее поведение стука.</summary>
public sealed class SystemIcmpEcho : IIcmpEcho
{
    private readonly Ping _ping = new();

    public async Task SendAsync(string host, int payloadBytes, TimeSpan timeout) =>
        await _ping.SendPingAsync(
                host,
                (int)Math.Max(1, timeout.TotalMilliseconds),
                PortKnockPayload.Make(payloadBytes))
            .ConfigureAwait(false);

    public void Dispose() => _ping.Dispose();
}
