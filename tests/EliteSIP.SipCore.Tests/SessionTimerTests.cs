namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Таймер сессии (RFC 4028): разбор заголовка, моменты срабатывания и
/// договорённость.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/SessionTimerTests.swift</c>.
/// Проверки самого обмена с сервером приедут вместе с SipUserAgent — он
/// следующим шагом этапа W2.
/// </summary>
public sealed class SessionTimerTests
{
    // Разбор заголовка

    [Fact]
    public void Разбирается_срок_с_ролью_обновляющего()
    {
        (int Expires, SipRefresher? Refresher) parsed = Require("1800;refresher=uas");
        Assert.Equal(1800, parsed.Expires);
        Assert.Equal(SipRefresher.Uas, parsed.Refresher);
    }

    [Fact]
    public void Разбирается_срок_без_роли()
    {
        (int Expires, SipRefresher? Refresher) parsed = Require("1800");
        Assert.Equal(1800, parsed.Expires);

        // Именно null, а не подставленная роль: сторона о ролях не высказалась,
        // и додумывать за неё нельзя — обновлять стали бы либо оба, либо никто.
        Assert.Null(parsed.Refresher);
    }

    [Fact]
    public void Регистр_и_пробелы_разбору_не_мешают()
    {
        (int Expires, SipRefresher? Refresher) parsed = Require(" 900 ;REFRESHER=UAC");
        Assert.Equal(900, parsed.Expires);
        Assert.Equal(SipRefresher.Uac, parsed.Refresher);
    }

    [Theory]
    [InlineData("")]
    [InlineData("не число")]
    // Ноль и отрицательное — не «сессия без срока», а испорченный заголовок.
    [InlineData("0")]
    [InlineData("-5")]
    public void Негодное_значение_не_разбирается(string value) => Assert.Null(SipSessionTimer.Parse(value));

    [Fact]
    public void Заголовок_собирается_обратно_в_разбираемый_вид()
    {
        var timer = new SipSessionTimer(1800, SipRefresher.Uas);
        Assert.Equal("1800;refresher=uas", timer.HeaderValue);

        (int Expires, SipRefresher? Refresher) parsed = Require(timer.HeaderValue);
        Assert.Equal(timer.Expires, parsed.Expires);
        Assert.Equal(timer.Refresher, parsed.Refresher);
    }

    // Моменты срабатывания

    [Fact]
    public void Обновляем_на_середине_срока_а_следим_до_конца()
    {
        var timer = new SipSessionTimer(1800, SipRefresher.Uas);

        // Половина, чтобы одно потерянное обновление не обрывало разговор:
        // второй заход успевает пройти до истечения.
        Assert.Equal(TimeSpan.FromSeconds(900), timer.RefreshAfter);
        Assert.Equal(TimeSpan.FromSeconds(1800), timer.ExpireAfter);
    }

    [Fact]
    public void Крошечный_срок_не_превращается_в_нулевые_интервалы()
    {
        // Нулевой интервал в цикле обновления означал бы занятое ожидание, а
        // нулевой в слежении — трубку, положенную мгновенно.
        var timer = new SipSessionTimer(1, SipRefresher.Uas);
        Assert.True(timer.RefreshAfter > TimeSpan.Zero);
        Assert.True(timer.ExpireAfter > TimeSpan.Zero);
    }

    // Договорённость по ответу сервера

    [Fact]
    public void Молчание_сервера_означает_отсутствие_таймера() =>
        // Ключевая гарантия: без подтверждения сервера таймер не заводится.
        // Иначе мы следили бы за сроком, о котором вторая сторона не знает, и
        // положили бы трубку посреди работающего разговора.
        Assert.Null(new SipSessionTimerPolicy().NegotiatedFromResponse(new SipHeaders()));

    [Fact]
    public void Ответ_без_роли_читается_как_обновляет_сервер()
    {
        var headers = new SipHeaders();
        headers.Append(SipSessionTimerHeader.SessionExpires, "1800");

        SipSessionTimer? timer = new SipSessionTimerPolicy().NegotiatedFromResponse(headers);
        Assert.NotNull(timer);
        Assert.Equal(1800, timer.Value.Expires);
        Assert.Equal(SipRefresher.Uas, timer.Value.Refresher);
    }

    [Fact]
    public void Сервер_вправе_назначить_обновляющим_нас()
    {
        var headers = new SipHeaders();
        headers.Append(SipSessionTimerHeader.SessionExpires, "600;refresher=uac");

        SipSessionTimer? timer = new SipSessionTimerPolicy().NegotiatedFromResponse(headers);
        Assert.NotNull(timer);
        Assert.Equal(600, timer.Value.Expires);
        Assert.Equal(SipRefresher.Uac, timer.Value.Refresher);
    }

    [Fact]
    public void Выключенная_политика_не_заводит_таймер_даже_на_согласие_сервера()
    {
        var headers = new SipHeaders();
        headers.Append(SipSessionTimerHeader.SessionExpires, "1800;refresher=uas");

        Assert.Null(new SipSessionTimerPolicy { IsEnabled = false }.NegotiatedFromResponse(headers));
    }

    // Договорённость по входящему

    [Fact]
    public void Звонящему_не_просившему_таймер_его_не_навязываем() =>
        Assert.Null(new SipSessionTimerPolicy().NegotiatedForIncoming(new SipHeaders()));

    [Fact]
    public void На_входящем_обновляющим_назначается_звонящий()
    {
        var headers = new SipHeaders();
        headers.Append(SipSessionTimerHeader.SessionExpires, "1800");

        SipSessionTimer? timer = new SipSessionTimerPolicy().NegotiatedForIncoming(headers);
        Assert.NotNull(timer);
        Assert.Equal(SipRefresher.Uac, timer.Value.Refresher);
        Assert.Equal(1800, timer.Value.Expires);
    }

    [Fact]
    public void Слишком_короткий_срок_входящего_поднимается_до_нашего_порога()
    {
        var headers = new SipHeaders();
        headers.Append(SipSessionTimerHeader.SessionExpires, "30");

        SipSessionTimer? timer = new SipSessionTimerPolicy { MinimumExpires = 90 }.NegotiatedForIncoming(headers);
        Assert.NotNull(timer);
        Assert.Equal(90, timer.Value.Expires);
    }

    private static (int Expires, SipRefresher? Refresher) Require(string value)
    {
        (int Expires, SipRefresher? Refresher)? parsed = SipSessionTimer.Parse(value);
        Assert.NotNull(parsed);
        return parsed.Value;
    }
}
