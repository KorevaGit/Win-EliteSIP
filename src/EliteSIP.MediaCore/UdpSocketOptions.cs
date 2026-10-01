using System.Net.Sockets;

namespace EliteSIP.MediaCore;

/// <summary>
/// Настройки, без которых UDP-сокет медиа на Windows ведёт себя не как UDP.
/// </summary>
internal static class UdpSocketOptions
{
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
