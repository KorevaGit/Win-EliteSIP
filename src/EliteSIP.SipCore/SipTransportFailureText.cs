using System.Globalization;
using System.Net.Sockets;

namespace EliteSIP.SipCore;

/// <summary>
/// Человеческая причина отказа вместо кода ошибки.
///
/// Тексты здесь — не педантизм. Настоящий случай из оригинала: боевой Asterisk
/// слушает незашифрованный SIP на 5060, в настройках выбрали TLS и тот же порт
/// 5060, а приложение написало «ожидание сети» и код ошибки. Человек полчаса
/// проверял сеть, хотя чинилась одна строка в настройках.
///
/// Живёт в SipCore, а не в транспорте, по той же причине, по какой там живёт
/// разбор номера: это чистая функция, и здесь её видно тестам. Транспорту
/// незачем быть покрытым модульными тестами, а тексту — есть зачем.
/// </summary>
public static class SipTransportFailureText
{
    /// <summary>Объясняет код ошибки сокета словами, которые что-то говорят оператору.</summary>
    public static string Describe(SocketError error, SipEndpoint remote, SipTransport transport)
    {
        string port = remote.Port.ToString(CultureInfo.InvariantCulture);

        // ConnectionReset на UDP — это не «сервер закрыл соединение», которого на
        // датаграммах не бывает, а доехавший ICMP «порт недостижим». Windows
        // отдаёт его именно этим кодом, и назвать его закрытием соединения
        // значит отправить человека проверять сеть вместо порта в настройках.
        if (error == SocketError.ConnectionReset && transport == SipTransport.Udp)
        {
            return $"порт {port} закрыт: на нём никто не слушает";
        }

        return error switch
        {
            SocketError.ConnectionRefused => $"порт {port} закрыт: на нём никто не слушает",
            SocketError.TimedOut => $"{remote.Host} не отвечает",
            SocketError.HostUnreachable or SocketError.NetworkUnreachable => $"нет маршрута до {remote.Host}",
            SocketError.NetworkDown => "сеть выключена",
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                $"имя {remote.Host} не разрешается",
            SocketError.ConnectionReset or SocketError.ConnectionAborted => "сервер закрыл соединение",
            SocketError.AccessDenied => $"порт {port} закрыт правилом межсетевого экрана",
            _ => $"сеть: {error}",
        };
    }

    /// <summary>
    /// Отказ TLS-рукопожатия.
    ///
    /// Отдельно от кодов сокета, потому что лечится совсем другим. На штатном
    /// порту 5061 это «TLS выключен на сервере или сертификат не подходит», а на
    /// любом другом почти всегда означает, что выбран порт незашифрованного SIP,
    /// а транспорт остался TLS. Подсказка про порт на 5061 была бы враньём,
    /// поэтому она зависит от порта.
    ///
    /// Сам TLS-транспорт приезжает на этапе W11; текст стоит здесь заранее,
    /// потому что выбор транспорта в настройках появится раньше.
    /// </summary>
    public static string DescribeTlsFailure(SipEndpoint remote, string detail)
    {
        string port = remote.Port.ToString(CultureInfo.InvariantCulture);
        string @base = $"сервер не принял TLS ({detail})";

        string hint = remote.Port == SipTransport.Tls.DefaultPort()
            ? "проверьте, включён ли TLS на сервере и подходит ли сертификат"
            : $"порт {port} — обычно это незашифрованный SIP; для TLS нужен "
                + SipTransport.Tls.DefaultPort().ToString(CultureInfo.InvariantCulture);

        return $"{@base}: {hint}";
    }
}
