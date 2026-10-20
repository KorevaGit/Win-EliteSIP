using System.Net.Sockets;

namespace EliteSIP.MediaCore;

/// <summary>
/// Настройки UDP-сокета медиа, которые на разных ОС делаются разными вызовами.
/// </summary>
internal static class UdpSocketOptions
{
    /// <summary>
    /// Байт TOS для голоса: DSCP EF (46), сдвинутый на два бита ECN.
    /// </summary>
    internal const int VoiceTypeOfService = 46 << 2;

    /// <summary><c>IPPROTO_IPV6</c> и <c>IPV6_TCLASS</c> из Linux <c>in.h</c>.</summary>
    private const int Ipv6Level = 41;
    private const int Ipv6TrafficClass = 67;

    /// <summary><c>SIO_UDP_CONNRESET</c> из <c>mstcpip.h</c>.</summary>
    private const int SioUdpConnectionReset = unchecked((int)0x9800000C);

    /// <summary>
    /// Выключает доставку ICMP «порт недоступен» ошибкой приёма.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// По умолчанию Windows превращает каждый ICMP-отказ на нашу датаграмму в
    /// <c>WSAECONNRESET</c> на следующем приёме — у неподключённого сокета это
    /// бессмыслица: приём ни с каким адресом не связан. Для разговора это
    /// опасно. Перезапущенный Asterisk или переехавший на другой порт мост
    /// отвечают ICMP на каждый наш пакет, полсотни в секунду, а собеседник в
    /// это время молчит — и шестнадцать отказов подряд (см.
    /// <see cref="RtpSession.MaximumConsecutiveReceiveFailures"/>) набирались
    /// за треть секунды. Приём останавливался до конца звонка, и оператор
    /// дальше не слышал собеседника, хотя звук снова шёл.
    /// </para>
    /// <para>
    /// Аудит 12 сентября 2026 советовал именно это; аудит 1 октября нашёл, что
    /// совет так и не был выполнен. Счётчик отказов остаётся страховкой на
    /// случай настоящей поломки сокета.
    /// </para>
    /// <para>
    /// Вне Windows такого поведения нет, и вызов ничего не делает. Отказ самого
    /// вызова тоже не повод отказаться от звонка: без настройки сокет работает,
    /// как работал до неё.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Помечает голос в сети: DSCP EF (46), то же, что политика QoS на Windows.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// На Windows метку ставит политика из установщика (с 0.1.73,
    /// <c>installer.iss</c>, «Приоритет голоса в сети»): на сокете Windows
    /// её принимает только у администратора, а оператор прав не имеет.
    /// Поэтому там вызов ничего не делает — политика работает и без него.
    /// </para>
    /// <para>
    /// На Linux наоборот: политики нет, а метку на своём сокете ставит любой
    /// процесс, без прав (<c>IP_TOS</c>; запрет есть только на приоритеты
    /// выше шестого, а это не они). Отказ — не повод отказаться от звонка: без
    /// метки голос идёт, как шёл до 0.1.73.
    /// </para>
    /// </remarks>
    internal static void MarkVoice(Socket socket) => MarkTypeOfService(socket, VoiceTypeOfService);

    /// <summary>
    /// Ставит байт TOS (IPv4) или класс трафика (IPv6) вне Windows. Общий для
    /// RTP и RTCP; сигнализация ставит свой, CS3, в <c>EliteSIP.SipCore.Udp</c>.
    /// </summary>
    internal static void MarkTypeOfService(Socket socket, int typeOfService)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.SetRawSocketOption(Ipv6Level, Ipv6TrafficClass, BitConverter.GetBytes(typeOfService));
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, typeOfService);
            }
        }
        catch (SocketException)
        {
        }
    }

    internal static void IgnoreIcmpResets(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            socket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }
    }
}
