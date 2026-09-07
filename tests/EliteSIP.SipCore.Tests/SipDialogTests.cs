namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Диалог SIP.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/SIPDialogTests.swift</c>.
/// </summary>
public sealed class SipDialogTests
{
    private static SipRequest MakeInvite()
    {
        var request = new SipRequest(SipMethod.Invite, new SipUri("127.0.0.1", user: "600"));
        request.Headers.Append(SipHeaderName.Via, "SIP/2.0/UDP 192.168.1.50:5060;branch=z9hG4bKinv1;rport");
        request.Headers.Append(SipHeaderName.MaxForwards, "70");
        request.Headers.Append(SipHeaderName.From, "\"Agent 100\" <sip:100@127.0.0.1>;tag=localtag");
        request.Headers.Append(SipHeaderName.To, "<sip:600@127.0.0.1>");
        request.Headers.Append(SipHeaderName.CallId, "call-abc@win.local");
        request.Headers.Append(SipHeaderName.CSeq, "20 INVITE");
        request.Headers.Append(SipHeaderName.Contact, "<sip:100@192.168.1.50:5060>");
        return request;
    }

    private static SipResponse MakeOk(
        string toTag = "as77aa11",
        string contact = "<sip:600@172.17.0.2:5060>",
        params string[] recordRoutes)
    {
        var headers = new SipHeaders();
        headers.Append(
            SipHeaderName.Via,
            "SIP/2.0/UDP 192.168.1.50:5060;branch=z9hG4bKinv1;received=192.168.65.1;rport=54321");
        foreach (string route in recordRoutes)
        {
            headers.Append(SipHeaderName.RecordRoute, route);
        }
        headers.Append(SipHeaderName.From, "\"Agent 100\" <sip:100@127.0.0.1>;tag=localtag");
        headers.Append(SipHeaderName.To, $"<sip:600@127.0.0.1>;tag={toTag}");
        headers.Append(SipHeaderName.CallId, "call-abc@win.local");
        headers.Append(SipHeaderName.CSeq, "20 INVITE");
        headers.Append(SipHeaderName.Contact, contact);
        return new SipResponse(200, headers: headers);
    }

    private static SipDialog RequireDialog(SipRequest request, SipResponse response)
    {
        SipDialog? dialog = SipDialog.FromInitiator(request, response);
        Assert.NotNull(dialog);
        return dialog;
    }

    [Fact]
    public void Собирается_из_200_OK_на_наш_INVITE()
    {
        SipDialog dialog = RequireDialog(MakeInvite(), MakeOk());

        Assert.Equal("call-abc@win.local", dialog.CallId);
        Assert.Equal("localtag", dialog.LocalTag);
        Assert.Equal("as77aa11", dialog.RemoteTag);
        Assert.Equal(20, dialog.LocalSequence);
        Assert.True(dialog.IsInitiator);

        // Remote target — это Contact из ответа, а не адрес из To. За NAT они
        // почти всегда разные, и BYE на адрес из To не дойдёт.
        Assert.Equal("172.17.0.2", dialog.RemoteTarget.Host);
        Assert.Equal((ushort?)5060, dialog.RemoteTarget.Port);
    }

    [Fact]
    public void Без_Contact_в_ответе_диалог_не_создаётся()
    {
        var headers = new SipHeaders();
        headers.Append(SipHeaderName.From, "<sip:100@127.0.0.1>;tag=localtag");
        headers.Append(SipHeaderName.To, "<sip:600@127.0.0.1>;tag=remotetag");
        headers.Append(SipHeaderName.CallId, "c1");
        headers.Append(SipHeaderName.CSeq, "20 INVITE");

        // Падать назад на адрес из To было бы хуже, чем не создать диалог:
        // запросы уходили бы в никуда, а ошибка проявилась бы позже и глуше.
        Assert.Null(SipDialog.FromInitiator(MakeInvite(), new SipResponse(200, headers: headers)));
    }

    [Fact]
    public void Неуспешный_ответ_диалога_не_создаёт()
    {
        var headers = new SipHeaders();
        headers.Append(SipHeaderName.To, "<sip:600@127.0.0.1>;tag=t");
        headers.Append(SipHeaderName.CallId, "c1");
        headers.Append(SipHeaderName.CSeq, "20 INVITE");
        headers.Append(SipHeaderName.Contact, "<sip:600@172.17.0.2>");

        Assert.Null(SipDialog.FromInitiator(MakeInvite(), new SipResponse(486, headers: headers)));
    }

    [Fact]
    public void Набор_маршрутов_берётся_из_RecordRoute()
    {
        SipDialog dialog = RequireDialog(
            MakeInvite(),
            MakeOk(recordRoutes: ["<sip:proxy1.example.com;lr>", "<sip:proxy2.example.com;lr>"]));

        Assert.Equal(2, dialog.RouteSet.Count);
        Assert.Equal("proxy1.example.com", dialog.RouteSet[0].Host);

        // При наличии lr-маршрута запрос физически уходит на него, а не на
        // Contact собеседника.
        Assert.Equal("proxy1.example.com", dialog.RequestDestination.Host);
    }

    [Fact]
    public void Без_маршрутов_запрос_идёт_прямо_на_Contact() =>
        Assert.Equal("172.17.0.2", RequireDialog(MakeInvite(), MakeOk()).RequestDestination.Host);

    [Fact]
    public void Запрос_внутри_диалога_несёт_правильные_теги_и_маршруты()
    {
        SipDialog dialog = RequireDialog(MakeInvite(), MakeOk(recordRoutes: ["<sip:proxy1.example.com;lr>"]));

        (SipDialog updated, int sequence) = dialog.NextSequence();
        Assert.Equal(21, sequence);

        var via = new SipVia(SipTransport.Udp, "192.168.1.50", 5060) { Branch = SipToken.Branch() };
        SipRequest bye = updated.MakeRequest(SipMethod.Bye, sequence, via);

        Assert.Equal(SipMethod.Bye, bye.Method);
        Assert.Equal("172.17.0.2", bye.Uri.Host);
        Assert.Equal("localtag", bye.From?.Tag);
        Assert.Equal("as77aa11", bye.To?.Tag);
        Assert.Equal("call-abc@win.local", bye.CallId);
        Assert.Equal(21, bye.CSeq?.Number);
        Assert.Equal(SipMethod.Bye, bye.CSeq?.Method);
        Assert.Single(bye.Headers.Values(SipHeaderName.Route));
    }

    [Fact]
    public void NextSequence_не_выдаёт_один_номер_дважды()
    {
        SipDialog dialog = RequireDialog(MakeInvite(), MakeOk());

        // Метод возвращает новый диалог именно для того, чтобы номер нельзя было
        // случайно использовать повторно: сервер счёл бы это ретрансмиссией.
        (SipDialog afterFirst, int first) = dialog.NextSequence();
        (_, int second) = afterFirst.NextSequence();
        Assert.Equal(21, first);
        Assert.Equal(22, second);

        (_, int repeated) = dialog.NextSequence();
        Assert.Equal(21, repeated);
    }

    [Fact]
    public void Сопоставление_требует_совпадения_всех_трёх_составляющих()
    {
        SipDialog dialog = RequireDialog(MakeInvite(), MakeOk());

        Assert.True(dialog.Matches("call-abc@win.local", "localtag", "as77aa11"));

        // Только Call-ID недостаточно: при перезвоне он может повториться.
        Assert.False(dialog.Matches("call-abc@win.local", "localtag", "другой"));
        Assert.False(dialog.Matches("другой", "localtag", "as77aa11"));
        Assert.False(dialog.Matches("call-abc@win.local", null, "as77aa11"));
    }
}

/// <summary>
/// ACK и CANCEL: то, что слой транзакций собирает сам.
///
/// Перенесено из того же файла оригинала (набор <c>InviteHelperTests</c>).
/// </summary>
public sealed class InviteHelperTests
{
    private static SipRequest MakeInvite()
    {
        var request = new SipRequest(SipMethod.Invite, new SipUri("127.0.0.1", user: "600"));
        request.Headers.Append(SipHeaderName.Via, "SIP/2.0/UDP 192.168.1.50:5060;branch=z9hG4bKinv1");
        request.Headers.Append(SipHeaderName.From, "<sip:100@127.0.0.1>;tag=localtag");
        request.Headers.Append(SipHeaderName.To, "<sip:600@127.0.0.1>");
        request.Headers.Append(SipHeaderName.CallId, "call-abc");
        request.Headers.Append(SipHeaderName.CSeq, "20 INVITE");
        request.Headers.Append(SipHeaderName.Route, "<sip:proxy;lr>");
        return request;
    }

    [Fact]
    public void ACK_на_неуспешный_ответ_берёт_тег_из_ответа()
    {
        var headers = new SipHeaders();
        headers.Append(SipHeaderName.Via, "SIP/2.0/UDP 192.168.1.50:5060;branch=z9hG4bKinv1");
        headers.Append(SipHeaderName.From, "<sip:100@127.0.0.1>;tag=localtag");
        headers.Append(SipHeaderName.To, "<sip:600@127.0.0.1>;tag=servertag");
        headers.Append(SipHeaderName.CallId, "call-abc");
        headers.Append(SipHeaderName.CSeq, "20 INVITE");

        SipRequest ack = SipTransactionLayer.MakeFailureAck(MakeInvite(), new SipResponse(486, headers: headers));

        Assert.Equal(SipMethod.Ack, ack.Method);

        // Тег собеседника есть только в ответе — без него сервер ACK не опознает.
        Assert.Equal("servertag", ack.To?.Tag);
        Assert.Equal("localtag", ack.From?.Tag);
        Assert.Equal("call-abc", ack.CallId);
        Assert.Equal(20, ack.CSeq?.Number);
        Assert.Equal(SipMethod.Ack, ack.CSeq?.Method);

        // Тот же branch: этот ACK — часть транзакции INVITE.
        Assert.Equal("z9hG4bKinv1", ack.TopVia?.Branch);
        Assert.Single(ack.Headers.Values(SipHeaderName.Route));
    }

    [Fact]
    public void CANCEL_повторяет_branch_отменяемого_INVITE()
    {
        SipRequest cancel = SipTransactionLayer.MakeCancel(MakeInvite());

        Assert.Equal(SipMethod.Cancel, cancel.Method);

        // По branch сервер понимает, какую транзакцию отменять.
        Assert.Equal("z9hG4bKinv1", cancel.TopVia?.Branch);
        Assert.Equal(20, cancel.CSeq?.Number);
        Assert.Equal(SipMethod.Cancel, cancel.CSeq?.Method);
        Assert.Equal("600", cancel.Uri.User);
        Assert.Null(cancel.To?.Tag);
    }

    [Fact]
    public void Ключ_транзакции_включает_метод()
    {
        // Иначе CANCEL, который обязан нести branch отменяемого INVITE, затирал
        // бы его транзакцию.
        Assert.NotEqual(
            SipTransactionLayer.TransactionKey("z9hG4bK1", SipMethod.Invite),
            SipTransactionLayer.TransactionKey("z9hG4bK1", SipMethod.Cancel));
    }
}
