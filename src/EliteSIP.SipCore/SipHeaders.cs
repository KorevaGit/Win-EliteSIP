using System.Globalization;
using System.Text;

namespace EliteSIP.SipCore;

/// <summary>Одно поле заголовка. Имя всегда каноническое и уже развёрнуто из компактной формы.</summary>
public readonly record struct SipHeaderField
{
    public SipHeaderField(ReadOnlySpan<char> name, string value)
    {
        Name = SipHeaderName.Canonical(name);
        Value = value;
    }

    public string Name { get; }

    public string Value { get; init; }
}

/// <summary>
/// Набор заголовков сообщения.
///
/// Порядок сохраняется: RFC 3261 разрешает произвольный порядок разных
/// заголовков, но требует сохранять относительный порядок одноимённых (стек Via
/// — это буквально порядок), а ещё сохранённый порядок сильно упрощает
/// сравнение с дампом из Wireshark при отладке.
/// </summary>
public sealed class SipHeaders : IEquatable<SipHeaders>
{
    private readonly List<SipHeaderField> _fields;

    public SipHeaders() => _fields = [];

    public SipHeaders(IEnumerable<SipHeaderField> fields) => _fields = [.. fields];

    public IReadOnlyList<SipHeaderField> Fields => _fields;

    public bool IsEmpty => _fields.Count == 0;

    /// <summary>
    /// Копия набора. В оригинале это была структура, и копия получалась
    /// присваиванием; здесь копию надо просить явно — см. <see cref="SipUri"/>.
    /// </summary>
    public SipHeaders Clone() => new(_fields);

    // Чтение

    /// <summary>Первое значение заголовка целиком, как оно пришло.</summary>
    public string? First(string name)
    {
        string canonical = SipHeaderName.Canonical(name);
        foreach (SipHeaderField header in _fields)
        {
            if (string.Equals(header.Name, canonical, StringComparison.Ordinal))
            {
                return header.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Все значения заголовка.
    ///
    /// Для заголовков из белого списка (<c>Via</c>, <c>Route</c>,
    /// <c>Contact</c>, …) значения, перечисленные через запятую в одной строке,
    /// разворачиваются в отдельные элементы. Для остальных — нет, иначе
    /// <c>WWW-Authenticate</c> с его <c>realm="a", nonce="b"</c> развалился бы
    /// на куски.
    /// </summary>
    public IReadOnlyList<string> Values(string name)
    {
        string canonical = SipHeaderName.Canonical(name);
        List<string> raw = [];
        foreach (SipHeaderField header in _fields)
        {
            if (string.Equals(header.Name, canonical, StringComparison.Ordinal))
            {
                raw.Add(header.Value);
            }
        }

        if (!SipHeaderName.AllowsCommaSeparatedValues(canonical))
        {
            return raw;
        }

        List<string> split = [];
        foreach (string value in raw)
        {
            foreach (string piece in SipLexer.SplitTopLevel(value, ','))
            {
                string trimmed = piece.TrimSip();
                if (trimmed.Length > 0)
                {
                    split.Add(trimmed);
                }
            }
        }
        return split;
    }

    public bool Contains(string name)
    {
        string canonical = SipHeaderName.Canonical(name);
        foreach (SipHeaderField header in _fields)
        {
            if (string.Equals(header.Name, canonical, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public int? Number(string name)
    {
        string? value = First(name);
        return value is not null
            && int.TryParse(value.TrimSip(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;
    }

    // Изменение

    /// <summary>Добавляет значение, не затрагивая существующие одноимённые.</summary>
    public void Append(string name, string value) => _fields.Add(new SipHeaderField(name, value));

    /// <summary>Добавляет значение первым. Нужно для Via: свой Via всегда сверху стека.</summary>
    public void Prepend(string name, string value) => _fields.Insert(0, new SipHeaderField(name, value));

    /// <summary>Заменяет все значения заголовка одним.</summary>
    public void Set(string name, string value)
    {
        string canonical = SipHeaderName.Canonical(name);
        int index = _fields.FindIndex(header => string.Equals(header.Name, canonical, StringComparison.Ordinal));
        if (index < 0)
        {
            Append(canonical, value);
            return;
        }

        _fields[index] = _fields[index] with { Value = value };

        // Остальные одноимённые убираем: Set задаёт единственное значение и
        // должен быть идемпотентным, иначе повторный вызов размножит заголовок.
        for (int tail = _fields.Count - 1; tail > index; tail--)
        {
            if (string.Equals(_fields[tail].Name, canonical, StringComparison.Ordinal))
            {
                _fields.RemoveAt(tail);
            }
        }
    }

    public void Remove(string name)
    {
        string canonical = SipHeaderName.Canonical(name);
        _fields.RemoveAll(header => string.Equals(header.Name, canonical, StringComparison.Ordinal));
    }

    // Сериализация

    public string Encoded
    {
        get
        {
            var result = new StringBuilder();
            foreach (SipHeaderField header in _fields)
            {
                result.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
            }
            return result.ToString();
        }
    }

    // Сравнение по значению

    public bool Equals(SipHeaders? other) =>
        other is not null && (ReferenceEquals(this, other) || _fields.SequenceEqual(other._fields));

    public override bool Equals(object? obj) => Equals(obj as SipHeaders);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (SipHeaderField header in _fields)
        {
            hash.Add(header);
        }
        return hash.ToHashCode();
    }
}
