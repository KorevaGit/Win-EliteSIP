using System.Net.Sockets;
using System.Runtime.Versioning;
using EliteSIP.Tests;

namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Метка голоса в сети вне Windows.
///
/// На Windows метку ставит политика QoS установщика, и проверять её здесь
/// нечем; вне Windows её ставит сам сокет, и это проверяется чтением обратно.
/// </summary>
public sealed class DscpMarkingTests
{
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Голос_помечен_EF()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        UdpSocketOptions.MarkVoice(socket);

        Assert.Equal(46 << 2, (int)socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Сокет_резерва_RTP_уже_помечен()
    {
        // Резерв — тот сокет, который потом и несёт разговор: метка должна
        // быть на нём, а не появляться где-то позже.
        using RtpPortReservation reservation = RtpPortReservation.Reserve(localAddress: "127.0.0.1");
        using Socket rtp = reservation.TakeRtpSocket();
        using Socket rtcp = reservation.TakeRtcpSocket();

        Assert.Equal(46 << 2, (int)rtp.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!);
        Assert.Equal(46 << 2, (int)rtcp.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!);
    }
}
