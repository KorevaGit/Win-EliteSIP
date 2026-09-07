using System.Globalization;
using System.Text;

namespace EliteSIP.SipCore;

/// <summary>
/// Заголовок Via: <c>SIP/2.0/UDP 10.0.0.5:5060;branch=z9hG4bK…;rport</c>.
///
/// Для нас он важнее, чем кажется: параметры <c>received</c> и <c>rport</c>,
/// которые Asterisk дописывает в верхний Via ответа, — единственный способ
/// узнать, каким адресом и портом нас видно снаружи NAT. Без этого Contact в
/// регистрации указывает на локальный адрес, и входящие звонки просто не
/// доходят.
/// </summary>
public sealed class SipVia : IEquatable<SipVia>
{
    public const string ProtocolPrefix = "SIP/2.0";

    public SipVia(SipTransport transport, string host, ushort? port = null, IEnumerable<SipParameter>? parameters = null)
    {
        Transport = transport;
        Host = host;
        Port = port;
        Parameters = parameters is null ? [] : [.. parameters];
    }

    public SipTransport Transport { get; set; }
    public string Host { get; set; }
    public ushort? Port { get; set; }
    public IList<SipParameter> Parameters { get; }

    public SipVia Clone() => new(Transport, Host, Port, Parameters);

    public static SipVia? Parse(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> body = text.TrimSip();

        // sent-protocol и sent-by разделены пробелом (или несколькими).
        int space = body.IndexOfAny(' ', '\t');
        if (space < 0)
        {
            return null;
        }

        List<string> protocolParts = [.. body[..space].ToString().Split('/')];
        if (protocolParts.Count != 3
            || !string.Equals(protocolParts[0], "SIP", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(protocolParts[1], "2.0", StringComparison.Ordinal))
        {
            return null;
        }

        SipTransport? transport = SipTransportExtensions.Parse(protocolParts[2]);
        if (transport is null)
        {
            return null;
        }

        ReadOnlySpan<char> sentBy = body[(space + 1)..].TrimSip();
        List<SipParameter> parameters = [];
        int semicolon = sentBy.IndexOf(';');
        if (semicolon >= 0)
        {
            parameters = SipLexer.ParseParameters(sentBy[semicolon..]);
            sentBy = sentBy[..semicolon].TrimSip();
        }

        if (sentBy.IsEmpty)
        {
            return null;
        }

        string host;
        ushort? port;
        if (sentBy[0] == '[')
        {
            int close = sentBy.IndexOf(']');
            if (close < 0)
            {
                return null;
            }
            host = sentBy[1..close].ToString();
            ReadOnlySpan<char> tail = sentBy[(close + 1)..];
            if (tail.IsEmpty)
            {
                port = null;
            }
            else if (tail[0] == ':')
            {
                if (!SipUri.TryParsePort(tail[1..], out ushort value))
                {
                    return null;
                }
                port = value;
            }
            else
            {
                return null;
            }
        }
        else
        {
            int colon = sentBy.LastIndexOf(':');
            if (colon >= 0)
            {
                if (!SipUri.TryParsePort(sentBy[(colon + 1)..], out ushort value))
                {
                    return null;
                }
                host = sentBy[..colon].ToString();
                port = value;
            }
            else
            {
                host = sentBy.ToString();
                port = null;
            }
        }

        return host.Length == 0 ? null : new SipVia(transport.Value, host, port, parameters);
    }

    // Параметры

    public string? GetParameter(string name)
    {
        foreach (SipParameter parameter in Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Value;
            }
        }
        return null;
    }

    public void SetParameter(string name, string? value)
    {
        for (int index = 0; index < Parameters.Count; index++)
        {
            if (!string.Equals(Parameters[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (value is null)
            {
                Parameters.RemoveAt(index);
            }
            else
            {
                Parameters[index] = Parameters[index] with { Value = value };
            }
            return;
        }

        if (value is not null)
        {
            Parameters.Add(new SipParameter(name, value));
        }
    }

    public bool HasParameter(string name)
    {
        foreach (SipParameter parameter in Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public string? Branch
    {
        get => GetParameter("branch");
        set => SetParameter("branch", value);
    }

    /// <summary>Адрес, с которого сервер реально увидел запрос.</summary>
    public string? Received => GetParameter("received");

    /// <summary>
    /// Порт, с которого сервер реально увидел запрос.
    ///
    /// В запросе <c>rport</c> присутствует без значения — это просьба сервер
    /// его заполнить. В ответе значение появляется. Поэтому «параметр есть» и
    /// «параметр со значением» здесь разные вещи.
    /// </summary>
    public ushort? Rport
    {
        get
        {
            string? value = GetParameter("rport");
            return value is not null && SipUri.TryParsePort(value, out ushort port) ? port : null;
        }
    }

    /// <summary>Просит сервер дописать received и rport (RFC 3581).</summary>
    public void RequestRport()
    {
        if (HasParameter("rport"))
        {
            return;
        }
        Parameters.Add(new SipParameter("rport"));
    }

    /// <summary>
    /// Адрес и порт, на которые надо отправлять ответы и куда сервер будет
    /// присылать входящие: то, что видно снаружи, если сервер это сообщил.
    /// </summary>
    public (string Host, ushort? Port)? ObservedAddress
    {
        get
        {
            string? received = Received;
            return received is null ? null : (received, Rport ?? Port);
        }
    }

    // Сериализация

    public override string ToString()
    {
        var result = new StringBuilder();
        result.Append(ProtocolPrefix).Append('/').Append(Transport.ProtocolName()).Append(' ');
        if (Host.Contains(':', StringComparison.Ordinal))
        {
            result.Append('[').Append(Host).Append(']');
        }
        else
        {
            result.Append(Host);
        }
        if (Port is ushort port)
        {
            result.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        }
        foreach (SipParameter parameter in Parameters)
        {
            result.Append(';').Append(parameter.Name);
            if (parameter.Value is not null)
            {
                result.Append('=').Append(parameter.Value);
            }
        }
        return result.ToString();
    }

    // Сравнение по значению

    public bool Equals(SipVia? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return Transport == other.Transport
            && string.Equals(Host, other.Host, StringComparison.Ordinal)
            && Port == other.Port
            && Parameters.SequenceEqual(other.Parameters);
    }

    public override bool Equals(object? obj) => Equals(obj as SipVia);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Transport);
        hash.Add(Host);
        hash.Add(Port);
        foreach (SipParameter parameter in Parameters)
        {
            hash.Add(parameter);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(SipVia? left, SipVia? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SipVia? left, SipVia? right) => !(left == right);
}
