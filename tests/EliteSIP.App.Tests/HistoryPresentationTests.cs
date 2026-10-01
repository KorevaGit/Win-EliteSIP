using EliteSIP.App.History;
using EliteSIP.App.Incoming;
using EliteSIP.CallHistory;

namespace EliteSIP.App.Tests;

/// <summary>
/// Строка истории называет звонок так же, как окно входящего: раздача и
/// звонок по сделке словами, мобильный — под маской. До 1 октября 2026
/// история писала номер как есть.
/// </summary>
public sealed class HistoryPresentationTests
{
    private const string Own = "176";

    [Fact]
    public void Мобильный_входящий_под_маской_и_наверху_и_внизу()
    {
        CallRecord record = Incoming("79615362641");

        Assert.Equal("+7**********", HistoryPresentation.Title(record, Own));
        Assert.Equal("+7**********", HistoryPresentation.Subtitle(record, Own));
    }

    [Fact]
    public void Раздача_называется_словами_а_номер_переводящего_остаётся_внизу()
    {
        CallRecord record = Incoming("712", name: "Call_Center", distribution: true);

        Assert.Equal(IncomingCallSubject.DistributionTitle, HistoryPresentation.Title(record, Own));
        Assert.Equal("712", HistoryPresentation.Subtitle(record, Own));
    }

    [Fact]
    public void Мобильный_просочившийся_в_раздачу_тоже_под_маской()
    {
        CallRecord record = Incoming("89181234567", distribution: true);

        Assert.Equal(IncomingCallSubject.DistributionTitle, HistoryPresentation.Title(record, Own));
        Assert.Equal("+7**********", HistoryPresentation.Subtitle(record, Own));
    }

    [Fact]
    public void Звонок_по_сделке_со_своего_добавочного_без_номера()
    {
        CallRecord record = Incoming(Own);

        Assert.Equal(IncomingCallSubject.DealTitle, HistoryPresentation.Title(record, Own));
        Assert.Equal(IncomingCallSubject.DealSource, HistoryPresentation.Subtitle(record, Own));
    }

    [Fact]
    public void Коллега_с_добавочного_именем_и_номером()
    {
        CallRecord record = Incoming("132", name: "Semenov_Artyom");

        Assert.Equal("Semenov_Artyom", HistoryPresentation.Title(record, Own));
        Assert.Equal("132", HistoryPresentation.Subtitle(record, Own));
    }

    [Fact]
    public void Исходящий_номер_как_набрали()
    {
        // Номер набрал сам оператор — прятать его не от кого.
        CallRecord record = new()
        {
            CallId = "out",
            Direction = CallDirection.Outgoing,
            Number = "89181234567",
        };

        Assert.Equal("89181234567", HistoryPresentation.Title(record, Own));
        Assert.Equal("89181234567", HistoryPresentation.Subtitle(record, Own));
    }

    [Fact]
    public void Городской_не_маскируется()
    {
        CallRecord record = Incoming("74952223344");

        Assert.Equal("74952223344", HistoryPresentation.Title(record, Own));
    }

    private static CallRecord Incoming(string number, string? name = null, bool distribution = false) => new()
    {
        CallId = Guid.NewGuid().ToString(),
        Direction = CallDirection.Incoming,
        Number = number,
        DisplayName = name,
        WasDistribution = distribution,
    };
}
