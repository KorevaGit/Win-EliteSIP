using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.Tests;

/// <summary>Автоподъём: предустановка, решение, сравнение номеров (0.1.61).</summary>
public sealed class AutoAnswerTests
{
    [Theory]
    [InlineData("+7 (999) 123-45-67", "89991234567")]
    [InlineData("9991234567", "+79991234567")]
    [InlineData("176", "176")]
    public void Один_номер(string listed, string caller)
        => Assert.True(AutoAnswerModes.SameNumber(listed, caller));

    [Theory]
    [InlineData("176", "1176")]
    [InlineData("89991234567", "89991234568")]
    [InlineData("", "176")]
    public void Разные_номера(string listed, string caller)
        => Assert.False(AutoAnswerModes.SameNumber(listed, caller));

    [Theory]
    [InlineData("off", true, "176", false)]
    [InlineData("always", false, "999", true)]
    [InlineData("header", true, "999", true)]
    [InlineData("header", false, "999", false)]
    [InlineData("list", false, "+7 999 123 45 67", true)]
    [InlineData("list", true, "555", false)]
    public void Решение_по_режиму(string mode, bool asks, string caller, bool expected)
    {
        var settings = new IncomingCallSettings
        {
            AutoAnswer = mode,
            AutoAnswerNumbers = ["89991234567"],
        };

        Assert.Equal(expected, settings.ShouldAutoAnswer(asks, caller));
    }

    [Fact]
    public void Незнакомый_режим_в_файле_читается_как_выключенный()
    {
        var settings = new IncomingCallSettings { AutoAnswer = "sometimes" };

        Assert.Equal(AutoAnswerModes.Off, settings.AutoAnswer);
        Assert.False(settings.ShouldAutoAnswer(true, "176"));
    }

    [Fact]
    public void Предустановка_разбирается_и_накладывается()
    {
        var fields = ManagedFields.Parse(
            """{"autoAnswer":"list","autoAnswerNumbers":["89991234567"," 176 "]}""");

        Assert.Equal("list", fields.AutoAnswer);
        Assert.Equal(["89991234567", "176"], fields.AutoAnswerNumbers);

        var settings = new AppSettings();
        settings.IncomingCall.AutoAnswerNumbers = ["111"];
        settings.Apply(fields);

        Assert.Equal("list", settings.IncomingCall.AutoAnswer);
        Assert.Equal(["89991234567", "176"], settings.IncomingCall.AutoAnswerNumbers);
    }

    [Fact]
    public void Незнакомый_режим_из_предустановки_не_применяется()
    {
        var settings = new AppSettings();
        settings.IncomingCall.AutoAnswer = "header";

        settings.Apply(ManagedFields.Parse("""{"autoAnswer":"sometimes"}"""));

        Assert.Equal("header", settings.IncomingCall.AutoAnswer);
    }

    [Fact]
    public void Null_списка_от_Go_очищает_список_а_отсутствие_не_трогает()
    {
        var settings = new AppSettings();
        settings.IncomingCall.AutoAnswerNumbers = ["111"];

        settings.Apply(ManagedFields.Parse("""{"autoAnswer":"list"}"""));
        Assert.Equal(["111"], settings.IncomingCall.AutoAnswerNumbers);

        settings.Apply(ManagedFields.Parse("""{"autoAnswerNumbers":null}"""));
        Assert.Empty(settings.IncomingCall.AutoAnswerNumbers);
    }

    [Fact]
    public void Правка_автоподъёма_руками_рвёт_связку_с_панелью()
    {
        var first = new AppSettings();
        var second = new AppSettings();
        Assert.False(first.DiffersInPanelManagedFields(second));

        second.IncomingCall.AutoAnswer = "always";
        Assert.True(first.DiffersInPanelManagedFields(second));

        second.IncomingCall.AutoAnswer = "off";
        second.IncomingCall.AutoAnswerNumbers = ["176"];
        Assert.True(first.DiffersInPanelManagedFields(second));
    }
}
