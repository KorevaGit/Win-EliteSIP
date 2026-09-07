using System.Globalization;
using System.Text;

namespace EliteSIP.SipCore;

/// <summary>
/// Общая часть запроса и ответа.
///
/// В оригинале это протокол <c>SIPMessageProtocol</c> плюс перечисление
/// <c>SIPMessage</c> с двумя случаями — Swift не даёт наследовать структурам.
/// В C# оба нужны в одном лице: базовый класс с двумя наследниками закрывает и
/// «общая часть», и «одно из двух», и лишний слой обёртки исчезает.
/// Разбор возвращает <see cref="SipMessage"/>, а <see cref="AsRequest"/> и
/// <see cref="AsResponse"/> заменяют сопоставление с образцом.
/// </summary>
public abstract class SipMessage
{
    protected SipMessage(SipHeaders? headers, ReadOnlyMemory<byte> body)
    {
        Headers = headers ?? new SipHeaders();
        Body = body;
    }

    public SipHeaders Headers { get; set; }

    public ReadOnlyMemory<byte> Body { get; set; }

    /// <summary>Стартовая строка без завершающего CRLF.</summary>
    public abstract string StartLine { get; }

    public SipRequest? AsRequest => this as SipRequest;

    public SipResponse? AsResponse => this as SipResponse;

    public string? CallId => Headers.First(SipHeaderName.CallId)?.TrimSip();

    /// <summary>
    /// CSeq — это номер И метод. Проверять надо оба: ответ с правильным номером
    /// но чужим методом относится к другой транзакции.
    /// </summary>
    public (int Number, SipMethod Method)? CSeq
    {
        get
        {
            string? raw = Headers.First(SipHeaderName.CSeq);
            if (raw is null)
            {
                return null;
            }

            string[] parts = raw.TrimSip().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                return null;
            }

            SipMethod? method = SipMethodExtensions.Parse(parts[1]);
            return method is null ? null : (number, method.Value);
        }
    }

    public NameAddress? From => ParseAddress(SipHeaderName.From);

    public NameAddress? To => ParseAddress(SipHeaderName.To);

    private NameAddress? ParseAddress(string name)
    {
        string? raw = Headers.First(name);
        return raw is null ? null : NameAddress.Parse(raw);
    }

    /// <summary>
    /// Верхний Via — свой собственный для исходящего запроса и тот, по которому
    /// маршрутизируется ответ.
    /// </summary>
    public SipVia? TopVia
    {
        get
        {
            IReadOnlyList<string> values = Headers.Values(SipHeaderName.Via);
            return values.Count == 0 ? null : SipVia.Parse(values[0]);
        }
    }

    public IReadOnlyList<SipVia> Vias
    {
        get
        {
            List<SipVia> result = [];
            foreach (string value in Headers.Values(SipHeaderName.Via))
            {
                if (SipVia.Parse(value) is SipVia via)
                {
                    result.Add(via);
                }
            }
            return result;
        }
    }

    public IReadOnlyList<NameAddress> Contacts
    {
        get
        {
            List<NameAddress> result = [];
            foreach (string value in Headers.Values(SipHeaderName.Contact))
            {
                if (NameAddress.Parse(value) is NameAddress contact)
                {
                    result.Add(contact);
                }
            }
            return result;
        }
    }

    public int? Expires => Headers.Number(SipHeaderName.Expires);

    public string? ContentType => Headers.First(SipHeaderName.ContentType)?.TrimSip();

    /// <summary>
    /// Байты сообщения. Content-Length всегда приводится к фактической длине
    /// тела: расхождение здесь на потоковом транспорте рассинхронизирует поток
    /// и ломает все последующие сообщения.
    /// </summary>
    public byte[] Encoded()
    {
        SipHeaders headers = Headers.Clone();
        headers.Set(SipHeaderName.ContentLength, Body.Length.ToString(CultureInfo.InvariantCulture));

        var text = new StringBuilder();
        text.Append(StartLine).Append("\r\n");
        text.Append(headers.Encoded);
        text.Append("\r\n");

        byte[] head = Encoding.UTF8.GetBytes(text.ToString());
        byte[] result = new byte[head.Length + Body.Length];
        head.CopyTo(result, 0);
        Body.Span.CopyTo(result.AsSpan(head.Length));
        return result;
    }
}

/// <summary>Запрос: <c>INVITE sip:100@host SIP/2.0</c>.</summary>
public sealed class SipRequest : SipMessage
{
    public SipRequest(SipMethod method, SipUri uri, SipHeaders? headers = null, ReadOnlyMemory<byte> body = default)
        : base(headers, body)
    {
        Method = method;
        Uri = uri;
    }

    public SipMethod Method { get; set; }

    public SipUri Uri { get; set; }

    public override string StartLine => $"{Method.Name()} {Uri} SIP/2.0";
}

/// <summary>Ответ: <c>SIP/2.0 200 OK</c>.</summary>
public sealed class SipResponse : SipMessage
{
    public SipResponse(
        int statusCode,
        string? reasonPhrase = null,
        SipHeaders? headers = null,
        ReadOnlyMemory<byte> body = default)
        : base(headers, body)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase ?? DefaultReasonPhrase(statusCode);
    }

    public enum ResponseCategory
    {
        Provisional,
        Success,
        Redirect,
        ClientError,
        ServerError,
        GlobalError,
    }

    public int StatusCode { get; set; }

    public string ReasonPhrase { get; set; }

    public override string StartLine =>
        $"SIP/2.0 {StatusCode.ToString(CultureInfo.InvariantCulture)} {ReasonPhrase}";

    public ResponseCategory? Category => StatusCode switch
    {
        >= 100 and < 200 => ResponseCategory.Provisional,
        >= 200 and < 300 => ResponseCategory.Success,
        >= 300 and < 400 => ResponseCategory.Redirect,
        >= 400 and < 500 => ResponseCategory.ClientError,
        >= 500 and < 600 => ResponseCategory.ServerError,
        >= 600 and < 700 => ResponseCategory.GlobalError,
        _ => null,
    };

    public bool IsProvisional => Category == ResponseCategory.Provisional;

    public bool IsSuccess => Category == ResponseCategory.Success;

    /// <summary>Финальный ответ — любой, кроме 1xx: именно он завершает транзакцию.</summary>
    public bool IsFinal => StatusCode >= 200;

    /// <summary>
    /// Требуется аутентификация. 401 приходит от registrar, 407 — от прокси, и
    /// отвечать на них надо разными заголовками.
    /// </summary>
    public bool IsAuthenticationRequired => StatusCode is 401 or 407;

    internal static string DefaultReasonPhrase(int statusCode) => statusCode switch
    {
        100 => "Trying",
        180 => "Ringing",
        183 => "Session Progress",
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        407 => "Proxy Authentication Required",
        408 => "Request Timeout",
        415 => "Unsupported Media Type",
        420 => "Bad Extension",
        423 => "Interval Too Brief",
        480 => "Temporarily Unavailable",
        481 => "Call/Transaction Does Not Exist",
        486 => "Busy Here",
        487 => "Request Terminated",
        488 => "Not Acceptable Here",
        500 => "Server Internal Error",
        503 => "Service Unavailable",
        603 => "Decline",
        _ => "Unknown",
    };
}
