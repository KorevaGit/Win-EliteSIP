using System.IO;
using EliteSIP.App.History;
using EliteSIP.App.Incoming;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;
using EliteSIP.CallHistory;
using EliteSIP.PanelLink;

namespace EliteSIP.App.Tests;

/// <summary>
/// Маска мобильных выключается (0.1.73): отделам, которые перезванивают
/// клиенту сами, номер нужен открытым. Умолчание — прятать.
/// </summary>
public sealed class MobileNumberMaskTests
{
    private const string Own = "176";
    private const string Mobile = "89181234567";

    [Fact]
    public void По_умолчанию_мобильные_скрыты()
        => Assert.True(new IncomingCallSettings().MasksMobileNumbers);

    [Fact]
    public void Без_маски_окно_входящего_показывает_номер_как_пришёл()
    {
        var shown = IncomingCallSubject.Classify(Mobile, null, false, Own, masksMobileNumbers: false);
        var hidden = IncomingCallSubject.Classify(Mobile, null, false, Own, masksMobileNumbers: true);

        Assert.Equal(Mobile, shown.Headline);
        Assert.Equal("+7**********", hidden.Headline);
    }

    [Fact]
    public void Без_маски_раздача_показывает_номер_внизу()
    {
        var subject = IncomingCallSubject.Classify(Mobile, "Лиды", true, Own, masksMobileNumbers: false);

        Assert.Equal(IncomingCallSubject.DistributionTitle, subject.Headline);
        Assert.Equal(Mobile, subject.SecondaryNumber);
    }

    [Fact]
    public void Без_маски_история_показывает_номер_и_наверху_и_внизу()
    {
        CallRecord record = new()
        {
            CallId = "in",
            Direction = CallDirection.Incoming,
            Number = Mobile,
        };

        Assert.Equal(Mobile, HistoryPresentation.Title(record, Own, masksMobileNumbers: false));
        Assert.Equal(Mobile, HistoryPresentation.Subtitle(record, Own, masksMobileNumbers: false));
    }

    [Fact]
    public void Звонок_по_сделке_прячет_номер_и_без_маски()
    {
        // Это не маска, а свой же добавочный: показывать его нечего.
        var subject = IncomingCallSubject.Classify(Own, null, false, Own, masksMobileNumbers: false);

        Assert.Null(subject.SecondaryNumber);
        Assert.Equal(IncomingCallSubject.DealTitle, subject.Headline);
    }

    [Fact]
    public void Строка_истории_без_флага_прячет()
    {
        // Привязка, не донёсшая третье значение, не должна открыть номер.
        CallRecord record = new() { CallId = "in", Direction = CallDirection.Incoming, Number = Mobile };

        var title = new CallTitleConverter().Convert([record, Own], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("+7**********", title);
    }

    [Fact]
    public void Предустановка_открывает_и_закрывает_номера()
    {
        var settings = new AppSettings();

        settings.Apply(ManagedFields.Parse("""{"masksMobileNumbers":false}"""));
        Assert.False(settings.IncomingCall.MasksMobileNumbers);

        settings.Apply(ManagedFields.Parse("""{"masksMobileNumbers":true}"""));
        Assert.True(settings.IncomingCall.MasksMobileNumbers);
    }

    [Fact]
    public void Предустановка_без_поля_оставляет_своё()
    {
        var settings = new AppSettings();
        settings.IncomingCall.MasksMobileNumbers = false;

        settings.Apply(ManagedFields.Parse("""{"autoAnswer":"off"}"""));

        Assert.False(settings.IncomingCall.MasksMobileNumbers);
    }

    [Fact]
    public void Правка_маски_руками_рвёт_связку_с_панелью()
    {
        var first = new AppSettings();
        var second = new AppSettings();
        second.IncomingCall.MasksMobileNumbers = false;

        Assert.True(first.DiffersInPanelManagedFields(second));
    }

    [Fact]
    public void Черновик_управления_переносит_маску()
    {
        var draft = new IncomingCallSettings { MasksMobileNumbers = false };
        var saved = new IncomingCallSettings();

        saved.CopyFrom(draft);

        Assert.False(saved.MasksMobileNumbers);
    }

    [Fact]
    public void Старый_файл_настроек_читается_с_маской()
    {
        // Файл 0.1.72 поля не знает — место не должно открыть номера само.
        var path = Path.Combine(Path.GetTempPath(), $"elitesip-mask-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"IncomingCall":{"IsEnabled":true,"AutoAnswer":"off"}}""");
        try
        {
            Assert.True(AppSettings.Load(path).IncomingCall.MasksMobileNumbers);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
