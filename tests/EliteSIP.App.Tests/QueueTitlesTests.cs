using System.IO;
using EliteSIP.App.Incoming;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.Tests;

/// <summary>
/// Подпись раздачи по имени из <c>From</c>. Те же примеры проверяет Spark
/// (<c>frontend/test/queue-titles.test.js</c>): правило одно на обе стороны.
/// </summary>
public sealed class QueueTitlesTests
{
    private const string Own = "176";

    private static readonly QueueTitles.Rule[] Rules = QueueTitles.Rules(
    [
        new QueueSetting { Match = "GORACHAYA", Title = "🔥Горячая раздача🔥" },
        new QueueSetting { Match = "call center", Title = "Колл-центр" },
        new QueueSetting { Match = " ", Title = "пустое правило не срабатывает" },
        new QueueSetting { Match = "line3", Title = "" },
    ]);

    [Theory]
    [InlineData("GORACHAYA RAZDACHACall Center", "GORACHAYA RAZDACHA Call Center")]
    [InlineData("line3dep", "Line 3 dep")]
    [InlineData("Call_Center", "Call Center")]
    [InlineData("MTS TestIT_Peleshko", "MTS Test IT Peleshko")]
    [InlineData("AutoDialer", "Auto Dialer")]
    [InlineData("  Semenov_Artyom ", "Semenov Artyom")]
    [InlineData("ЛинияМТС2", "Линия МТС 2")]
    [InlineData("", "")]
    public void Имя_причёсывается_но_не_переводится(string name, string shown)
        => Assert.Equal(shown, QueueTitles.Tidy(name));

    [Theory]
    [InlineData("GORACHAYA RAZDACHA", "gorachayarazdacha")]
    [InlineData(" Call_Center ", "callcenter")]
    [InlineData(null, "")]
    public void Ключ_не_зависит_от_регистра_пробелов_и_подчёркиваний(string? text, string key)
        => Assert.Equal(key, QueueTitles.Key(text));

    [Theory]
    [InlineData("GORACHAYA RAZDACHACall Center", "🔥Горячая раздача🔥")]
    [InlineData("Call_Center", "Колл-центр")]
    [InlineData("line3dep", "Line 3 dep")]
    public void Первое_подходящее_правило_иначе_причёсанное_имя(string name, string title)
        => Assert.Equal(title, QueueTitles.Headline(name, Rules));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("79615362688")]
    [InlineData("+7 (961) 536-26-88")]
    public void Имя_без_букв_именем_не_считается(string? name)
        => Assert.Null(QueueTitles.Headline(name, Rules));

    [Fact]
    public void Раздача_называется_по_правилу_а_номер_переводящего_остаётся_под_ней()
    {
        var subject = IncomingCallSubject.Classify("710", "GORACHAYA RAZDACHACall Center", true, Own, masksMobileNumbers: true, Rules);

        Assert.Equal(IncomingCallKind.Queue, subject.Kind);
        Assert.Equal("🔥Горячая раздача🔥", subject.Headline);
        Assert.Equal("710", subject.SecondaryNumber);
    }

    [Fact]
    public void Раздача_без_правила_показывает_имя_а_не_общий_заголовок()
        => Assert.Equal("Line 3 dep", IncomingCallSubject.Classify("716", "line3dep", true, Own, true, Rules).Headline);

    [Fact]
    public void Раздача_без_имени_называется_нейтрально()
        => Assert.Equal(
            IncomingCallSubject.DistributionTitle,
            IncomingCallSubject.Classify("716", null, true, Own, true, Rules).Headline);

    /// <summary>Правила называют раздачу, а не опознают её: коллега остаётся коллегой.</summary>
    [Fact]
    public void Правило_не_превращает_звонок_коллеги_в_раздачу()
    {
        var subject = IncomingCallSubject.Classify("132", "Call_Center", false, Own, true, Rules);

        Assert.Equal(IncomingCallKind.Caller, subject.Kind);
        Assert.Equal("Call_Center", subject.Headline);
    }

    [Fact]
    public void Свой_добавочный_сильнее_правил()
        => Assert.Equal(
            IncomingCallKind.SelfCall,
            IncomingCallSubject.Classify(Own, "GORACHAYA", true, Own, true, Rules).Kind);

    /// <summary>Приехавший блок заменяет правила целиком: убранное в Spark исчезает и здесь.</summary>
    [Fact]
    public void Приехавшие_подписи_заменяют_прежние_и_запоминают_ревизию()
    {
        var settings = new AppSettings();
        settings.Queues.Queues.Add(new QueueSetting { Match = "old", Title = "Старое" });

        settings.Apply(new PresetBundle.QueueBlock
        {
            Revision = 5,
            Titles = [new PresetBundle.QueueTitle("GORACHAYA", "Горячая раздача")],
        });

        var rule = Assert.Single(settings.Queues.Queues);
        Assert.Equal(("GORACHAYA", "Горячая раздача"), (rule.Match, rule.Title));
        Assert.Equal(5, settings.Panel.AppliedQueuesRevision);
    }

    /// <summary>Записи прежнего словаря «номер → название» в старом файле настроек не остаются пустыми строками.</summary>
    [Fact]
    public void Записи_прежнего_словаря_убираются_при_чтении_старого_файла()
    {
        var directory = Directory.CreateTempSubdirectory("elitesip-queues-").FullName;
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var old = new AppSettings { Revision = 3 };
            old.Queues.Queues.Add(new QueueSetting { Title = "Горячая раздача" });
            old.Queues.Queues.Add(new QueueSetting { Match = "line3dep", Title = "Линия 3" });
            old.Save(path);

            var loaded = AppSettings.Load(path);

            Assert.Equal("line3dep", Assert.Single(loaded.Queues.Queues).Match);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
