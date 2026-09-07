using System.Globalization;
using System.Text;

namespace EliteSIP.SipCore;

public enum SipScheme
{
    Sip,
    Sips,
}

/// <summary>
/// SIP- или SIPS-URI: <c>sip:100@pbx.example.com:5060;transport=tls</c>.
///
/// Разбор осознанно неполный относительно RFC 3261 §19.1 — не поддерживаются
/// пароль в userinfo (он депрекейтед) и header-часть после <c>?</c>. Обе штуки
/// Asterisk не присылает, а недостающее лучше добавить по факту, чем нести
/// мёртвый код.
///
/// В оригинале это структура со значимой семантикой: присваивание копировало,
/// и мутация копии не задевала исходник. В C# структура с изменяемым списком
/// параметров такой гарантии не даёт — копия разделила бы список с оригиналом,
/// то есть вела бы себя хуже, чем ссылочный тип, и незаметнее. Поэтому здесь
/// класс со сравнением по значению и явным <see cref="Clone"/>: копия делается
/// там, где она действительно нужна, и это видно в коде.
/// </summary>
public sealed class SipUri : IEquatable<SipUri>
{
    public SipUri(
        string host,
        SipScheme scheme = SipScheme.Sip,
        string? user = null,
        ushort? port = null,
        IEnumerable<SipParameter>? parameters = null)
    {
        Host = host;
        Scheme = scheme;
        User = user;
        Port = port;
        Parameters = parameters is null ? [] : [.. parameters];
    }

    public SipScheme Scheme { get; set; }
    public string? User { get; set; }
    public string Host { get; set; }
    public ushort? Port { get; set; }

    /// <summary>Параметры в порядке, в каком они пришли. Почему списком — см. <see cref="SipParameter"/>.</summary>
    public IList<SipParameter> Parameters { get; }

    public SipUri Clone() => new(Host, Scheme, User, Port, Parameters);

    // Разбор

    /// <summary>
    /// Разбирает URI. Возвращает <see langword="null"/> на любом мусоре:
    /// битый URI в чужом сообщении — это повод ответить 400, а не упасть.
    /// </summary>
    public static SipUri? Parse(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> rest = text.Trim();

        int schemeEnd = rest.IndexOf(':');
        if (schemeEnd < 0)
        {
            return null;
        }

        SipScheme scheme;
        ReadOnlySpan<char> schemeText = rest[..schemeEnd];
        if (schemeText.Equals("sip", StringComparison.OrdinalIgnoreCase))
        {
            scheme = SipScheme.Sip;
        }
        else if (schemeText.Equals("sips", StringComparison.OrdinalIgnoreCase))
        {
            scheme = SipScheme.Sips;
        }
        else
        {
            return null;
        }
        rest = rest[(schemeEnd + 1)..];

        // Header-часть отбрасываем, но её наличие не должно ронять разбор URI.
        int headersStart = rest.IndexOf('?');
        if (headersStart >= 0)
        {
            rest = rest[..headersStart];
        }

        // userinfo@ — берём последнюю собаку, потому что она может встречаться
        // в экранированном виде внутри user-части.
        string? user = null;
        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            ReadOnlySpan<char> userinfo = rest[..at];
            if (userinfo.IsEmpty)
            {
                return null;
            }

            // Пароль после двоеточия игнорируем: он депрекейтед и в журнал ему не место.
            int password = userinfo.IndexOf(':');
            ReadOnlySpan<char> userPart = password < 0 ? userinfo : userinfo[..password];
            if (userPart.IsEmpty)
            {
                return null;
            }
            user = userPart.ToString();
            rest = rest[(at + 1)..];
        }

        List<SipParameter> parameters = [];
        int paramsStart = rest.IndexOf(';');
        if (paramsStart >= 0)
        {
            ReadOnlySpan<char> rawParameters = rest[(paramsStart + 1)..];
            rest = rest[..paramsStart];
            foreach (string raw in SipLexer.SplitTopLevel(rawParameters, ';'))
            {
                int equals = raw.IndexOf('=', StringComparison.Ordinal);
                parameters.Add(equals < 0
                    ? new SipParameter(raw)
                    : new SipParameter(raw[..equals], raw[(equals + 1)..]));
            }
        }

        if (rest.IsEmpty)
        {
            return null;
        }

        string host;
        ushort? port;
        if (rest[0] == '[')
        {
            // IPv6-литерал: [2001:db8::1]:5061. Двоеточий внутри много,
            // поэтому его надо снять до разбора порта.
            int close = rest.IndexOf(']');
            if (close < 0)
            {
                return null;
            }
            host = rest[1..close].ToString();
            ReadOnlySpan<char> tail = rest[(close + 1)..];
            if (tail.IsEmpty)
            {
                port = null;
            }
            else if (tail[0] == ':')
            {
                if (!TryParsePort(tail[1..], out ushort value))
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
            int portSeparator = rest.LastIndexOf(':');
            if (portSeparator >= 0)
            {
                if (!TryParsePort(rest[(portSeparator + 1)..], out ushort value))
                {
                    return null;
                }
                host = rest[..portSeparator].ToString();
                port = value;
            }
            else
            {
                host = rest.ToString();
                port = null;
            }
        }

        return host.Length == 0 ? null : new SipUri(host, scheme, user, port, parameters);
    }

    /// <summary>
    /// Порт — это только цифры. <see cref="NumberStyles.None"/> здесь не
    /// украшение: по умолчанию .NET принял бы «+5060» и « 5060», а на длинных
    /// значениях вроде 99999 обязан быть отказ, а не усечение.
    /// </summary>
    internal static bool TryParsePort(ReadOnlySpan<char> text, out ushort port) =>
        ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port);

    // Доступ к параметрам

    /// <summary>Значение параметра. Имена параметров по RFC регистронезависимы.</summary>
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

    /// <summary>
    /// Задаёт значение параметра; <see langword="null"/> удаляет параметр
    /// целиком — как присваивание nil в оригинале.
    /// </summary>
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

    /// <summary>Транспорт из параметра <c>transport</c>, если он указан явно.</summary>
    public SipTransport? Transport
    {
        get
        {
            string? value = GetParameter("transport");
            return value is null ? null : SipTransportExtensions.Parse(value);
        }
    }

    /// <summary>Порт, на который реально надо отправлять, с учётом схемы и транспорта.</summary>
    public ushort ResolvedPort(SipTransport defaultTransport)
    {
        if (Port is ushort port)
        {
            return port;
        }
        if (Scheme == SipScheme.Sips)
        {
            return SipTransport.Tls.DefaultPort();
        }
        return (Transport ?? defaultTransport).DefaultPort();
    }

    // Сериализация

    public override string ToString()
    {
        var result = new StringBuilder();
        result.Append(Scheme == SipScheme.Sips ? "sips:" : "sip:");
        if (User is not null)
        {
            result.Append(User).Append('@');
        }

        // Хост с двоеточиями — это IPv6, его надо вернуть в скобках.
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

    public bool Equals(SipUri? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return Scheme == other.Scheme
            && User == other.User
            && string.Equals(Host, other.Host, StringComparison.Ordinal)
            && Port == other.Port
            && Parameters.SequenceEqual(other.Parameters);
    }

    public override bool Equals(object? obj) => Equals(obj as SipUri);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Scheme);
        hash.Add(User);
        hash.Add(Host);
        hash.Add(Port);
        foreach (SipParameter parameter in Parameters)
        {
            hash.Add(parameter);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(SipUri? left, SipUri? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SipUri? left, SipUri? right) => !(left == right);
}
