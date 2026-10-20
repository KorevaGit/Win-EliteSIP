using System.Net.Sockets;

namespace EliteSIP.SipCore.Udp;

/// <summary>
/// Метка сигнализации в сети: DSCP CS3 (24).
/// </summary>
///
/// <remarks>
/// Пара к <c>UdpSocketOptions.MarkVoice</c> из <c>EliteSIP.MediaCore</c>, и
/// правило то же: на Windows метку ставит политика QoS установщика (с 0.1.73),
/// поэтому вызов там ничего не делает; на Linux её ставит само приложение,
/// права для этого не нужны. Своя копия, а не общий тип: транспорт SIP от
/// медиа не зависит и не должен — ради двух вызовов заводить общий проект
/// дороже, чем держать их рядом с сокетом.
///
/// Ставится до соединения: у TCP и TLS метка должна быть уже на SYN.
/// </remarks>
internal static class SipSocketOptions
{
    /// <summary>Байт TOS: DSCP CS3 (24), сдвинутый на два бита ECN.</summary>
    internal const int SignalingTypeOfService = 24 << 2;

    /// <summary><c>IPPROTO_IPV6</c> и <c>IPV6_TCLASS</c> из Linux <c>in.h</c>.</summary>
    private const int Ipv6Level = 41;
    private const int Ipv6TrafficClass = 67;

    internal static void MarkSignaling(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.SetRawSocketOption(Ipv6Level, Ipv6TrafficClass, BitConverter.GetBytes(SignalingTypeOfService));
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, SignalingTypeOfService);
            }
        }
        catch (SocketException)
        {
            // Без метки сигнализация идёт, как шла до 0.1.73.
        }
    }
}
