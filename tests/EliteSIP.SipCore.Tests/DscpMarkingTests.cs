using System.Net.Sockets;
using System.Runtime.Versioning;
using EliteSIP.SipCore.Udp;
using EliteSIP.Tests;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Метка сигнализации в сети вне Windows: CS3 (24). Пара к проверке голоса в
/// <c>EliteSIP.MediaCore.Tests</c>.
/// </summary>
public sealed class DscpMarkingTests
{
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Сигнализация_помечена_CS3_и_по_UDP_и_по_TCP()
    {
        using Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using Socket tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        SipSocketOptions.MarkSignaling(udp);
        SipSocketOptions.MarkSignaling(tcp);

        Assert.Equal(24 << 2, (int)udp.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!);
        Assert.Equal(24 << 2, (int)tcp.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!);
    }
}
