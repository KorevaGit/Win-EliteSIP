using System.Text;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Разбор и сериализация сообщений.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/SIPParserTests.swift</c>.
/// Проверки digest-заголовков из оригинального набора приедут вместе с
/// аутентификацией — она следующим шагом этапа W2.
/// </summary>
public sealed class SipParserTests
{
    /// <summary>
    /// Собирает сообщение с правильными CRLF: в литерале их не видно, а ошибка
    /// в переводах строк — самая частая причина «сервер молчит».
    /// </summary>
    internal static byte[] SipMessageBytes(string[] lines, string body = "")
    {
        var text = new StringBuilder();
        text.AppendJoin("\r\n", lines);
        text.Append("\r\n\r\n");
        text.Append(body);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    [Fact]
    public void Разбирает_REGISTER()
    {
        byte[] data = SipMessageBytes(
        [
            "REGISTER sip:127.0.0.1 SIP/2.0",
            "Via: SIP/2.0/UDP 192.168.1.50:5060;branch=z9hG4bKabc;rport",
            "Max-Forwards: 70",
            "From: \"Agent 100\" <sip:100@127.0.0.1>;tag=t1",
            "To: <sip:100@127.0.0.1>",
            "Call-ID: call-1@win.local",
            "CSeq: 1 REGISTER",
            "Contact: <sip:100@192.168.1.50:5060>",
            "Expires: 300",
            "User-Agent: EliteSIP/0.1",
            "Content-Length: 0",
        ]);

        SipRequest? request = SipParser.Parse(data).AsRequest;
        Assert.NotNull(request);
        Assert.Equal(SipMethod.Register, request.Method);
        Assert.Equal("127.0.0.1", request.Uri.Host);
        Assert.Equal("call-1@win.local", request.CallId);
        Assert.Equal(1, request.CSeq?.Number);
        Assert.Equal(SipMethod.Register, request.CSeq?.Method);
        Assert.Equal("t1", request.From?.Tag);
        Assert.Equal("Agent 100", request.From?.DisplayName);
        Assert.Null(request.To?.Tag);
        Assert.Equal(300, request.Expires);
        Assert.Equal("z9hG4bKabc", request.TopVia?.Branch);
        Assert.Single(request.Contacts);
        Assert.True(request.Body.IsEmpty);
    }

    [Fact]
    public void Разбирает_401_от_chan_sip()
    {
        // Форма заголовка ровно такая, как её отдаёт chan_sip: algorithm первым,
        // без qop.
        byte[] data = SipMessageBytes(
        [
            "SIP/2.0 401 Unauthorized",
            "Via: SIP/2.0/UDP 192.168.1.50:5060;branch=z9hG4bKabc;received=192.168.65.1;rport=54321",
            "From: \"Agent 100\" <sip:100@127.0.0.1>;tag=t1",
            "To: <sip:100@127.0.0.1>;tag=as5f0e9b1c",
            "Call-ID: call-1@win.local",
            "CSeq: 1 REGISTER",
            "WWW-Authenticate: Digest algorithm=MD5, realm=\"asterisk\", nonce=\"1234abcd\"",
            "Content-Length: 0",
        ]);

        SipResponse? response = SipParser.Parse(data).AsResponse;
        Assert.NotNull(response);
        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Unauthorized", response.ReasonPhrase);
        Assert.True(response.IsFinal);
        Assert.False(response.IsSuccess);
        Assert.True(response.IsAuthenticationRequired);
        Assert.Equal("as5f0e9b1c", response.To?.Tag);
        Assert.Equal((ushort?)54321, response.TopVia?.Rport);
        Assert.Equal("192.168.65.1", response.TopVia?.Received);
    }

    [Fact]
    public void Тело_берётся_по_ContentLength_а_не_до_конца_буфера()
    {
        const string Sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\n";
        byte[] message = SipMessageBytes(
        [
            "SIP/2.0 200 OK",
            "Via: SIP/2.0/UDP host;branch=z9hG4bK1",
            "Call-ID: c1",
            "CSeq: 1 INVITE",
            "Content-Type: application/sdp",
            $"Content-Length: {Encoding.UTF8.GetByteCount(Sdp)}",
        ], Sdp);

        // Дописываем хвост, как это бывает при склейке в потоке.
        byte[] garbage = Encoding.UTF8.GetBytes("МУСОР");
        byte[] data = [.. message, .. garbage];

        SipResponse? response = SipParser.Parse(data).AsResponse;
        Assert.NotNull(response);
        Assert.Equal(Encoding.UTF8.GetByteCount(Sdp), response.Body.Length);
        Assert.Equal(Sdp, Encoding.UTF8.GetString(response.Body.Span));
        Assert.Equal("application/sdp", response.ContentType);
    }

    [Fact]
    public void Свёрнутые_заголовки_склеиваются()
    {
        string text = string.Join("\r\n",
        [
            "SIP/2.0 200 OK",
            "Via: SIP/2.0/UDP host;branch=z9hG4bK1",
            "Call-ID: c1",
            "CSeq: 1 REGISTER",
            "Contact: <sip:100@host>",
            "\t;expires=300",
            "Content-Length: 0",
        ]) + "\r\n\r\n";

        SipResponse? response = SipParser.Parse(Encoding.UTF8.GetBytes(text)).AsResponse;
        Assert.NotNull(response);
        Assert.Equal(300, response.Contacts[0].Expires);
    }

    [Fact]
    public void Переводы_строк_только_LF_тоже_разбираются()
    {
        // Своё мы всегда пишем с CRLF, но снисходительность на приёме дешевле,
        // чем разбираться, почему звонок не идёт.
        const string Text = "SIP/2.0 100 Trying\nVia: SIP/2.0/UDP host;branch=z9hG4bK1\nCall-ID: c1\nCSeq: 1 INVITE\n\n";

        SipResponse? response = SipParser.Parse(Encoding.UTF8.GetBytes(Text)).AsResponse;
        Assert.NotNull(response);
        Assert.Equal(100, response.StatusCode);
        Assert.True(response.IsProvisional);
    }

    [Fact]
    public void Reason_phrase_с_пробелами_не_режется()
    {
        byte[] data = SipMessageBytes(
        [
            "SIP/2.0 480 Temporarily Unavailable",
            "Via: SIP/2.0/UDP host;branch=z9hG4bK1",
            "Call-ID: c1",
            "CSeq: 1 INVITE",
            "Content-Length: 0",
        ]);

        Assert.Equal("Temporarily Unavailable", SipParser.Parse(data).AsResponse?.ReasonPhrase);
    }

    [Fact]
    public void Категории_кодов_ответа()
    {
        Assert.Equal(SipResponse.ResponseCategory.Provisional, new SipResponse(100).Category);
        Assert.Equal(SipResponse.ResponseCategory.Success, new SipResponse(200).Category);
        Assert.Equal(SipResponse.ResponseCategory.Redirect, new SipResponse(302).Category);
        Assert.Equal(SipResponse.ResponseCategory.ClientError, new SipResponse(401).Category);
        Assert.Equal(SipResponse.ResponseCategory.ServerError, new SipResponse(503).Category);
        Assert.Equal(SipResponse.ResponseCategory.GlobalError, new SipResponse(603).Category);
        Assert.Equal("OK", new SipResponse(200).ReasonPhrase);
    }

    [Fact]
    public void Ошибки_разбора_называются_своими_именами()
    {
        Assert.Equal(SipParseErrorKind.Empty, KindOf([]));
        Assert.Equal(
            SipParseErrorKind.MissingHeaderTerminator,
            KindOf(Encoding.UTF8.GetBytes("REGISTER sip:host SIP/2.0\r\n")));
        Assert.Equal(SipParseErrorKind.UnsupportedVersion, KindOf(SipMessageBytes(["REGISTER sip:host SIP/1.0"])));
        Assert.Equal(SipParseErrorKind.UnknownMethod, KindOf(SipMessageBytes(["PUBLISH sip:host SIP/2.0"])));
        Assert.Equal(SipParseErrorKind.InvalidStatusCode, KindOf(SipMessageBytes(["SIP/2.0 999 Nope"])));
        Assert.Equal(SipParseErrorKind.InvalidStatusCode, KindOf(SipMessageBytes(["SIP/2.0 abc Nope"])));
        Assert.Equal(
            SipParseErrorKind.MalformedHeader,
            KindOf(SipMessageBytes(["REGISTER sip:host SIP/2.0", "БезДвоеточия"])));

        // Объявленное тело длиннее фактического — это обрыв, а не пустое тело.
        Assert.Equal(
            SipParseErrorKind.TruncatedBody,
            KindOf(SipMessageBytes(["SIP/2.0 200 OK", "Content-Length: 100"], "коротко")));

        static SipParseErrorKind? KindOf(byte[] data)
        {
            try
            {
                SipParser.Parse(data);
                return null;
            }
            catch (SipParseException error)
            {
                return error.Kind;
            }
        }
    }

    [Fact]
    public void Сериализация_выставляет_ContentLength_по_факту()
    {
        var request = new SipRequest(SipMethod.Invite, new SipUri("host"));
        request.Headers.Append("Call-ID", "c1");
        request.Headers.Append("Content-Length", "999");   // намеренно неверно
        request.Body = Encoding.UTF8.GetBytes("v=0\r\n");

        SipRequest? parsed = SipParser.Parse(request.Encoded()).AsRequest;
        Assert.NotNull(parsed);
        Assert.Equal(5, parsed.Headers.Number("Content-Length"));
        Assert.True(parsed.Body.Span.SequenceEqual(request.Body.Span));
        Assert.Single(parsed.Headers.Values("Content-Length"));
    }

    [Fact]
    public void Round_trip_запроса_и_ответа()
    {
        var request = new SipRequest(SipMethod.Register, new SipUri("127.0.0.1"));
        request.Headers.Append("Via", $"SIP/2.0/TLS 10.0.0.5:5061;branch={SipToken.Branch()}");
        request.Headers.Append(
            "From",
            new NameAddress(
                new SipUri("127.0.0.1", user: "100"),
                "Agent 100",
                [new SipParameter("tag", "t1")]).ToString());
        request.Headers.Append("To", "<sip:100@127.0.0.1>");
        request.Headers.Append("Call-ID", SipToken.CallId());
        request.Headers.Append("CSeq", "1 REGISTER");
        request.Headers.Append("Max-Forwards", "70");

        SipRequest? parsed = SipParser.Parse(request.Encoded()).AsRequest;
        Assert.NotNull(parsed);
        Assert.Equal(request.Method, parsed.Method);
        Assert.Equal(request.Uri, parsed.Uri);
        Assert.Equal(request.From, parsed.From);
        Assert.Equal(request.TopVia, parsed.TopVia);
        Assert.Equal(request.CallId, parsed.CallId);
    }
}
