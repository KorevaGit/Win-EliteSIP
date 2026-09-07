using System.Globalization;
using System.Text;

namespace EliteSIP.SipCore;

/// <summary>
/// Значение заголовков From, To, Contact, Refer-To:
/// <c>"Имя" &lt;sip:100@host&gt;;tag=x</c>.
///
/// Ссылочный тип со сравнением по значению — по той же причине, что и
/// <see cref="SipUri"/>.
/// </summary>
public sealed class NameAddress : IEquatable<NameAddress>
{
    public NameAddress(SipUri uri, string? displayName = null, IEnumerable<SipParameter>? parameters = null)
    {
        Uri = uri;
        DisplayName = displayName;
        Parameters = parameters is null ? [] : [.. parameters];
    }

    public string? DisplayName { get; set; }

    public SipUri Uri { get; set; }

    /// <summary>
    /// Параметры заголовка — это НЕ параметры URI.
    /// <c>&lt;sip:a@b;transport=tls&gt;;tag=1</c>: transport принадлежит URI,
    /// tag — заголовку.
    /// </summary>
    public IList<SipParameter> Parameters { get; }

    public NameAddress Clone() => new(Uri.Clone(), DisplayName, Parameters);

    public static NameAddress? Parse(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> body = text.TrimSip();
        if (body.IsEmpty)
        {
            return null;
        }

        int open = body.IndexOf('<');
        if (open >= 0)
        {
            int close = body[open..].IndexOf('>');
            if (close < 0)
            {
                return null;
            }
            close += open;

            ReadOnlySpan<char> namePart = body[..open].TrimSip();
            string? displayName = namePart.IsEmpty ? null : SipLexer.Unquoted(namePart);

            SipUri? uri = SipUri.Parse(body[(open + 1)..close]);
            if (uri is null)
            {
                return null;
            }

            ReadOnlySpan<char> tail = body[(close + 1)..].TrimSip();
            List<SipParameter> parameters = tail.IsEmpty ? [] : SipLexer.ParseParameters(tail);
            return new NameAddress(uri, displayName, parameters);
        }

        // Без угловых скобок display name невозможен, а параметры после точки
        // с запятой принадлежат заголовку, не URI (RFC 3261 §20.10).
        int semicolon = body.IndexOf(';');
        if (semicolon >= 0)
        {
            SipUri? uri = SipUri.Parse(body[..semicolon].TrimSip());
            return uri is null ? null : new NameAddress(uri, null, SipLexer.ParseParameters(body[semicolon..]));
        }

        SipUri? bare = SipUri.Parse(body);
        return bare is null ? null : new NameAddress(bare);
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

    /// <summary>
    /// Тег диалога. Его сравнение — основа маршрутизации ответов и запросов
    /// внутри диалога, поэтому он вынесен отдельным свойством.
    /// </summary>
    public string? Tag
    {
        get => GetParameter("tag");
        set => SetParameter("tag", value);
    }

    /// <summary>
    /// Значение <c>expires</c> у Contact в ответе на REGISTER: Asterisk может
    /// вернуть срок именно здесь, а не в заголовке Expires.
    /// </summary>
    public int? Expires
    {
        get
        {
            string? value = GetParameter("expires");
            return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : null;
        }
    }

    // Сериализация

    public override string ToString()
    {
        var result = new StringBuilder();
        if (!string.IsNullOrEmpty(DisplayName))
        {
            result.Append(SipLexer.QuotedIfNeeded(DisplayName)).Append(' ');
        }

        // URI всегда в угловых скобках: без них параметры URI и параметры
        // заголовка становятся неразличимы для принимающей стороны.
        result.Append('<').Append(Uri.ToString()).Append('>');

        foreach (SipParameter parameter in Parameters)
        {
            result.Append(';').Append(parameter.Name);
            if (parameter.Value is not null)
            {
                result.Append('=').Append(SipLexer.QuotedIfNeeded(parameter.Value));
            }
        }
        return result.ToString();
    }

    // Сравнение по значению

    public bool Equals(NameAddress? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return DisplayName == other.DisplayName
            && Uri.Equals(other.Uri)
            && Parameters.SequenceEqual(other.Parameters);
    }

    public override bool Equals(object? obj) => Equals(obj as NameAddress);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DisplayName);
        hash.Add(Uri);
        foreach (SipParameter parameter in Parameters)
        {
            hash.Add(parameter);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(NameAddress? left, NameAddress? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(NameAddress? left, NameAddress? right) => !(left == right);
}
