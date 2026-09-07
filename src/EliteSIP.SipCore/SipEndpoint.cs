using System.Globalization;

namespace EliteSIP.SipCore;

/// <summary>
/// Адрес и порт одной стороны сигнализации.
///
/// Отдельным типом, а не парой строк, потому что IPv6 в текстовом виде требует
/// скобок, и забыть их — значит отправить <c>2001:db8::1:5060</c>, где порт
/// неотличим от адреса.
/// </summary>
public readonly record struct SipEndpoint(string Host, ushort Port)
{
    public override string ToString()
    {
        string port = Port.ToString(CultureInfo.InvariantCulture);
        return Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{port}" : $"{Host}:{port}";
    }
}
