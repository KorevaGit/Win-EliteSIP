namespace EliteSIP.SipCore;

/// <summary>
/// Транспорт для сигнализации.
///
/// В бою используется <see cref="Udp"/>: сверка 31 июля 2026 показала, что на
/// боевом Asterisk TLS выключен целиком (<c>TLS SIP Bindaddress: Disabled</c>),
/// а у пира рабочего места <c>Encryption: No</c>. Защита сейчас держится на
/// сети (офис, L2TP VPN для удалённых), а не на транспорте SIP.
///
/// <see cref="Tls"/> остаётся поддерживаемым профилем клиента и целью этапа
/// W11, а не мёртвым кодом: включить его на сервере — это правка конфигурации,
/// а не разработка.
/// </summary>
public enum SipTransport
{
    Udp,
    Tcp,
    Tls,
}

public static class SipTransportExtensions
{
    /// <summary>Порт по умолчанию по RFC 3261 §19.1.2.</summary>
    public static ushort DefaultPort(this SipTransport transport) => transport switch
    {
        SipTransport.Udp or SipTransport.Tcp => 5060,
        SipTransport.Tls => 5061,
        _ => 5060,
    };

    public static bool IsSecure(this SipTransport transport) => transport == SipTransport.Tls;

    /// <summary>Значение для <c>Via: SIP/2.0/&lt;protocol&gt;</c> и для параметра <c>transport=</c>.</summary>
    public static string ProtocolName(this SipTransport transport) => transport switch
    {
        SipTransport.Udp => "UDP",
        SipTransport.Tcp => "TCP",
        SipTransport.Tls => "TLS",
        _ => "UDP",
    };

    /// <summary>
    /// Работает ли транспорт поверх потока, а не датаграмм.
    ///
    /// От этого зависят таймеры транзакций: на надёжном транспорте retransmit
    /// не нужен, и таймеры A/E не запускаются.
    /// </summary>
    public static bool IsReliable(this SipTransport transport) => transport != SipTransport.Udp;

    /// <summary>Разбор имени без учёта регистра: в Via транспорт всегда в верхнем.</summary>
    public static SipTransport? Parse(ReadOnlySpan<char> name)
    {
        if (name.Equals("udp", StringComparison.OrdinalIgnoreCase))
        {
            return SipTransport.Udp;
        }
        if (name.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        {
            return SipTransport.Tcp;
        }
        if (name.Equals("tls", StringComparison.OrdinalIgnoreCase))
        {
            return SipTransport.Tls;
        }
        return null;
    }
}
