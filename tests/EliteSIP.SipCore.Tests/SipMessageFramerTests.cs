using System.Text;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Нарезка потока на сообщения.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/SIPMessageFramerTests.swift</c>.
/// </summary>
public sealed class SipMessageFramerTests
{
    private static byte[] Options(string callId, string body = "") =>
        SipParserTests.SipMessageBytes(
        [
            "OPTIONS sip:100@10.0.0.5 SIP/2.0",
            $"Via: SIP/2.0/TLS pbx;branch=z9hG4bK{callId}",
            $"Call-ID: {callId}",
            "CSeq: 1 OPTIONS",
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}",
        ], body);

    [Fact]
    public void Два_сообщения_в_одном_чанке_разбираются_по_отдельности()
    {
        var framer = new SipMessageFramer();
        byte[] chunk = [.. Options("one"), .. Options("two")];
        framer.Append(chunk);

        IReadOnlyList<SipMessage> messages = framer.DrainMessages();
        Assert.Equal(2, messages.Count);
        Assert.Equal("one", messages[0].AsRequest?.CallId);
        Assert.Equal("two", messages[1].AsRequest?.CallId);
        Assert.Equal(0, framer.BufferedByteCount);
    }

    [Fact]
    public void Сообщение_разорванное_по_байту_собирается_целиком()
    {
        var framer = new SipMessageFramer();
        byte[] message = Options("slow", "v=0\r\n");

        List<SipMessage> delivered = [];
        foreach (byte value in message)
        {
            framer.Append([value]);
            delivered.AddRange(framer.DrainMessages());
        }

        SipMessage only = Assert.Single(delivered);
        Assert.Equal("slow", only.AsRequest?.CallId);
        Assert.Equal(0, framer.BufferedByteCount);
    }

    [Fact]
    public void Ждёт_тело_если_по_ContentLength_его_ещё_не_всё()
    {
        var framer = new SipMessageFramer();
        byte[] full = Options("partial", "v=0\r\ns=x\r\n");

        framer.Append(full.AsSpan(0, full.Length - 4));
        Assert.Null(framer.NextMessageData());

        framer.Append(full.AsSpan(full.Length - 4));
        Assert.NotNull(framer.NextMessageData());
    }

    [Fact]
    public void Одинокие_CRLF_между_сообщениями_выбрасываются()
    {
        // На TLS Asterisk и клиенты гоняют CRLF как keep-alive. Если не
        // выбросить их здесь, парсер решит, что соединение сломано.
        var framer = new SipMessageFramer();
        framer.Append(Encoding.UTF8.GetBytes("\r\n\r\n\r\n"));
        framer.Append(Options("after-ping"));

        SipMessage only = Assert.Single(framer.DrainMessages());
        Assert.Equal("after-ping", only.AsRequest?.CallId);
    }

    [Fact]
    public void Только_keep_alive_и_ничего_больше_не_сообщение_и_не_ошибка()
    {
        var framer = new SipMessageFramer();
        framer.Append(Encoding.UTF8.GetBytes("\r\n"));
        Assert.Null(framer.NextMessageData());
        Assert.Equal(0, framer.BufferedByteCount);
    }

    [Fact]
    public void Слишком_большое_сообщение_не_растит_буфер_бесконечно()
    {
        var framer = new SipMessageFramer(maximumMessageSize: 512);

        // Заголовки, которые никогда не кончатся.
        framer.Append(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("X: y\r\n", 200))));

        SipFramingException error = Assert.Throws<SipFramingException>(() => framer.NextMessageData());
        Assert.Equal(SipFramingErrorKind.MessageTooLarge, error.Kind);
    }

    [Fact]
    public void Битый_ContentLength_явная_ошибка_а_не_тишина()
    {
        var framer = new SipMessageFramer();
        framer.Append(SipParserTests.SipMessageBytes(
        [
            "OPTIONS sip:host SIP/2.0",
            "Call-ID: c1",
            "Content-Length: не число",
        ]));

        SipFramingException error = Assert.Throws<SipFramingException>(() => framer.NextMessageData());
        Assert.Equal(SipFramingErrorKind.MalformedContentLength, error.Kind);
    }

    /// <summary>
    /// Предельное значение разбирается как число успешно, и дальше длина
    /// заголовков к нему прибавляется. Без запаса разрядности это отрицательная
    /// длина, то есть в лучшем случае исключение из глубины кода, а в худшем —
    /// чтение мимо буфера от одного пакета.
    /// </summary>
    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775806")]
    [InlineData("2147483647")]
    public void ContentLength_у_самого_предела_не_роняет_процесс(string value)
    {
        var framer = new SipMessageFramer();
        framer.Append(SipParserTests.SipMessageBytes(
        [
            "OPTIONS sip:host SIP/2.0",
            "Call-ID: c1",
            $"Content-Length: {value}",
        ]));

        Assert.Throws<SipFramingException>(() => framer.NextMessageData());
    }
}
