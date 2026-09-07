using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace EliteSIP.SipCore;

/// <summary>Чем именно поток не устроил фреймер.</summary>
public enum SipFramingErrorKind
{
    /// <summary>Заголовки не закончились в пределах допустимого размера.</summary>
    MessageTooLarge,
    MalformedContentLength,
}

public sealed class SipFramingException : Exception
{
    public SipFramingException()
        : this(SipFramingErrorKind.MessageTooLarge, string.Empty)
    {
    }

    public SipFramingException(string message)
        : this(SipFramingErrorKind.MalformedContentLength, message)
    {
    }

    public SipFramingException(string message, Exception innerException)
        : base(message, innerException) => Detail = message;

    public SipFramingException(SipFramingErrorKind kind, string detail)
        : base($"{kind}: {detail}")
    {
        Kind = kind;
        Detail = detail;
    }

    public SipFramingErrorKind Kind { get; }

    public string Detail { get; } = string.Empty;
}

/// <summary>
/// Выделяет отдельные сообщения из потока байтов.
///
/// На UDP фреймер не нужен — там одна датаграмма равна одному сообщению. Он
/// существует ради TCP и TLS, где сообщения склеиваются и рвутся произвольно, а
/// единственный признак конца — Content-Length.
///
/// В оригинале это структура с mutating-методами; здесь класс, потому что
/// фреймер по смыслу принадлежит соединению и живёт столько же, сколько оно.
/// </summary>
public sealed class SipMessageFramer
{
    /// <summary>
    /// Предел на одно сообщение. Без него сломанный или враждебный peer,
    /// который никогда не пришлёт пустую строку, растит буфер до нехватки
    /// памяти.
    /// </summary>
    public const int DefaultMaximumMessageSize = 128 * 1024;

    private readonly List<byte> _buffer = [];
    private readonly int _maximumMessageSize;

    public SipMessageFramer(int maximumMessageSize = DefaultMaximumMessageSize) =>
        _maximumMessageSize = maximumMessageSize;

    public int BufferedByteCount => _buffer.Count;

    public void Append(ReadOnlySpan<byte> data) => _buffer.AddRange(data);

    /// <summary>
    /// Возвращает байты следующего целого сообщения или <see langword="null"/>,
    /// если данных пока мало.
    /// </summary>
    public byte[]? NextMessageData()
    {
        DiscardLeadingLineBreaks();

        if (_buffer.Count == 0)
        {
            return null;
        }

        Span<byte> buffered = CollectionsMarshal.AsSpan(_buffer);
        (int Start, int End)? boundary = SipParser.FindHeaderTerminator(buffered);
        if (boundary is null)
        {
            // Заголовки ещё не закончились. Это нормально, пока буфер разумного
            // размера.
            if (_buffer.Count > _maximumMessageSize)
            {
                throw new SipFramingException(
                    SipFramingErrorKind.MessageTooLarge,
                    $"буфер {_buffer.Count} байт без конца заголовков");
            }
            return null;
        }

        long contentLength = DeclaredContentLength(buffered[..boundary.Value.Start]);
        int headerByteCount = boundary.Value.End;

        // Предел проверяется вычитанием, а не сложением, и это не стилистика.
        // Content-Length приходит из сети, и long.MaxValue разбирается успешно:
        // сложение с длиной заголовков переполнилось бы молча и отдало
        // отрицательную длину — одним пакетом, ещё до всякого разбора.
        // Вычитание из предела переполниться не может: обе части заведомо в
        // границах int.
        if (contentLength > _maximumMessageSize - headerByteCount)
        {
            throw new SipFramingException(
                SipFramingErrorKind.MessageTooLarge,
                $"тело {contentLength} байт при пределе {_maximumMessageSize}");
        }

        int size = headerByteCount + (int)contentLength;
        if (_buffer.Count < size)
        {
            return null;
        }

        byte[] message = new byte[size];
        _buffer.CopyTo(0, message, 0, size);
        _buffer.RemoveRange(0, size);
        return message;
    }

    /// <summary>Разбирает все накопленные целые сообщения.</summary>
    public IReadOnlyList<SipMessage> DrainMessages()
    {
        List<SipMessage> result = [];
        while (NextMessageData() is byte[] data)
        {
            result.Add(SipParser.Parse(data));
        }
        return result;
    }

    // Внутреннее

    /// <summary>
    /// Убирает ведущие CRLF.
    ///
    /// На надёжном транспорте одинокий CRLF — это keep-alive «ping»
    /// (RFC 5626), а не сообщение. Если не выбросить его здесь, парсер получит
    /// мусор и решит, что соединение сломано.
    /// </summary>
    private void DiscardLeadingLineBreaks()
    {
        int skip = 0;
        while (skip < _buffer.Count && (_buffer[skip] == 0x0D || _buffer[skip] == 0x0A))
        {
            skip++;
        }
        if (skip > 0)
        {
            _buffer.RemoveRange(0, skip);
        }
    }

    private static long DeclaredContentLength(ReadOnlySpan<byte> headerData)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(headerData);
        }
        catch (DecoderFallbackException)
        {
            // Заголовки не в UTF-8 — пусть с этим разбирается парсер, фреймер
            // отдаёт сообщение как есть, считая тело пустым.
            return 0;
        }

        List<string> lines = SipParser.Unfold(text);
        for (int index = 1; index < lines.Count; index++)
        {
            string line = lines[index];
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            if (!string.Equals(SipHeaderName.Canonical(line.AsSpan(0, colon)), SipHeaderName.ContentLength, StringComparison.Ordinal))
            {
                continue;
            }

            string raw = line[(colon + 1)..].TrimSip();
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                throw new SipFramingException(SipFramingErrorKind.MalformedContentLength, raw);
            }
            return value;
        }

        // Content-Length обязателен на потоковом транспорте, но его отсутствие
        // трактуем как пустое тело: это не повод рвать соединение.
        return 0;
    }
}
