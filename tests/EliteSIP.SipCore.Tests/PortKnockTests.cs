using System.Text.Json;
using EliteSIP.SipCore;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Стук по портам.
/// </summary>
///
/// <remarks>
/// Сеть здесь не участвует ни в одной проверке: проверяется то, что можно
/// испортить правкой, — решение «стучать или нет», порядок и длины пакетов и
/// правило пропуска повторов. Живой шлюз это не заменяет и не претендует: он
/// либо откроет порт, либо нет, и узнать это можно только на нём.
/// </remarks>
public sealed class PortKnockTests
{
    private static void Silent(string message)
    {
    }

    // MARK: - Кому стучать

    [Theory]
    [InlineData("192.168.1.2")]
    [InlineData("192.168.1.154")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("asterisk.local")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    public void Внутреннему_серверу_стучать_не_надо(string host)
    {
        Assert.True(PortKnockPolicy.IsInternal(host));
        Assert.False(PortKnockPolicy.NeedsKnocking(host));
    }

    [Theory]
    [InlineData("crm.elitesochi.com")]
    [InlineData("45.10.53.84")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("11.0.0.1")]
    public void Внешнему_серверу_стучать_надо(string host)
    {
        Assert.False(PortKnockPolicy.IsInternal(host));
        Assert.True(PortKnockPolicy.NeedsKnocking(host));
    }

    /// <summary>
    /// Пустой адрес — это ненастроенная машина, а не внешний сервер: стучать в
    /// никуда семь секунд перед заведомо провальной регистрацией незачем.
    /// </summary>
    [Fact]
    public void Пустой_адрес_считается_внутренним()
    {
        Assert.True(PortKnockPolicy.IsInternal(string.Empty));
        Assert.True(PortKnockPolicy.IsInternal("   "));
    }

    /// <summary>Диапазон 172.16.0.0/12 — это 172.16–172.31, а не весь 172.</summary>
    [Fact]
    public void Границы_приватного_диапазона_на_месте()
    {
        Assert.True(PortKnockPolicy.IsInternal("172.16.0.0"));
        Assert.True(PortKnockPolicy.IsInternal("172.31.0.0"));
        Assert.False(PortKnockPolicy.IsInternal("172.15.0.0"));
        Assert.False(PortKnockPolicy.IsInternal("172.32.0.0"));
    }

    // MARK: - Пометка рабочего места

    /// <summary>
    /// Явная пометка сильнее адреса, и в этом весь смысл поля: офис за внешним
    /// доменом не платит семью секундами за подключение.
    /// </summary>
    [Theory]
    [InlineData("crm.elitesochi.com")]
    [InlineData("45.10.53.84")]
    [InlineData("192.168.1.2")]
    public void Офисное_место_не_стучит_никогда(string host)
    {
        Assert.False(PortKnockPolicy.NeedsKnocking(host, WorkplaceSite.Office));
        Assert.Null(PortKnocker.ForServer(
            host, WorkplaceSite.Office, PortKnockSequence.Production, Silent));
    }

    /// <summary>
    /// Обратный случай: удалённое место, которому сервер виден по внутреннему
    /// адресу — чужой туннель или проброс. Догадка по адресу здесь ошибается,
    /// пометка нет.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.2")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    public void Удалённое_место_стучит_даже_на_внутренний_адрес(string host)
    {
        Assert.True(PortKnockPolicy.NeedsKnocking(host, WorkplaceSite.Remote));

        using var knocker = PortKnocker.ForServer(
            host, WorkplaceSite.Remote, PortKnockSequence.Production, Silent);

        Assert.NotNull(knocker);
    }

    /// <summary>
    /// Пустая последовательность сильнее любой пометки: стучать нечем.
    /// </summary>
    ///
    /// <remarks>
    /// Это и есть способ выключить стук правкой настроек — иначе выключить его
    /// было бы нельзя вовсе.
    /// </remarks>
    [Fact]
    public void Пустая_последовательность_сильнее_пометки()
    {
        PortKnockSequence disabled = new() { Steps = [] };

        Assert.True(disabled.IsEmpty);
        Assert.Null(PortKnocker.ForServer(
            "192.168.1.2", WorkplaceSite.Remote, disabled, Silent));
        Assert.Null(PortKnocker.ForServer(
            "crm.elitesochi.com", WorkplaceSite.Automatic, disabled, Silent));
    }

    /// <summary>
    /// Интерфейс показывает две кнопки, и выбранной должна быть та, по которой
    /// машина работает на самом деле, — даже если место ей ещё не задавали.
    /// </summary>
    [Fact]
    public void Невыбранное_место_разрешается_по_адресу()
    {
        Assert.Equal(
            WorkplaceSite.Office,
            PortKnockPolicy.ResolvedSite("192.168.1.2", WorkplaceSite.Automatic));
        Assert.Equal(
            WorkplaceSite.Remote,
            PortKnockPolicy.ResolvedSite("crm.elitesochi.com", WorkplaceSite.Automatic));

        // Ненастроенная машина показывается офисной: стучать в пустой адрес
        // незачем, и «удалённо» у неё было бы обещанием, а не фактом.
        Assert.Equal(
            WorkplaceSite.Office,
            PortKnockPolicy.ResolvedSite(string.Empty, WorkplaceSite.Automatic));
    }

    [Fact]
    public void Заданное_руками_не_пересчитывается_по_адресу()
    {
        Assert.Equal(
            WorkplaceSite.Remote,
            PortKnockPolicy.ResolvedSite("192.168.1.2", WorkplaceSite.Remote));
        Assert.Equal(
            WorkplaceSite.Office,
            PortKnockPolicy.ResolvedSite("crm.elitesochi.com", WorkplaceSite.Office));
    }

    /// <summary>
    /// Журнал должен различать догадку и настройку: иначе «почему семь секунд»
    /// разбирается чтением кода, а не строки.
    /// </summary>
    [Fact]
    public void Объяснение_называет_причину()
    {
        Assert.Equal(
            "рабочее место помечено как удалённое",
            PortKnockPolicy.Explanation("192.168.1.2", WorkplaceSite.Remote));
        Assert.Equal(
            "рабочее место помечено как офисное",
            PortKnockPolicy.Explanation("crm.elitesochi.com", WorkplaceSite.Office));
        Assert.Equal(
            "адрес сервера внутренний",
            PortKnockPolicy.Explanation("192.168.1.2", WorkplaceSite.Automatic));
        Assert.Equal(
            "адрес сервера внешний",
            PortKnockPolicy.Explanation("crm.elitesochi.com", WorkplaceSite.Automatic));
    }

    // MARK: - Что именно уходит

    /// <summary>
    /// Последовательность должна совпадать со скриптом побайтово и по порядку.
    /// </summary>
    ///
    /// <remarks>
    /// Правило на шлюзе не наше, проверить его мы не можем, и любое «улучшение»
    /// здесь означает, что рабочее место молча перестаёт подключаться.
    /// </remarks>
    [Fact]
    public void Боевая_последовательность_совпадает_со_скриптом()
    {
        var steps = PortKnockSequence.Production.Steps;

        Assert.Equal(6, steps.Count);

        Assert.Equal(string.Empty, steps[0].Host);
        Assert.Equal(228, steps[0].PayloadBytes);
        Assert.Equal(2, steps[0].Count);

        Assert.Equal(string.Empty, steps[1].Host);
        Assert.Equal(126, steps[1].PayloadBytes);
        Assert.Equal(2, steps[1].Count);

        Assert.Equal(string.Empty, steps[2].Host);
        Assert.Equal(125, steps[2].PayloadBytes);
        Assert.Equal(1, steps[2].Count);

        Assert.Equal("45.10.53.84", steps[3].Host);
        Assert.Equal(228, steps[3].PayloadBytes);
        Assert.Equal("45.10.53.86", steps[4].Host);
        Assert.Equal(126, steps[4].PayloadBytes);
        Assert.Equal("45.10.53.94", steps[5].Host);
        Assert.Equal(125, steps[5].PayloadBytes);

        Assert.Equal(8, PortKnockSequence.Production.PacketCount);
    }

    /// <summary>Пустой хост шага — это «сервер из настроек», а не пустой адрес.</summary>
    [Fact]
    public void Шаг_без_адреса_берёт_адрес_сервера()
    {
        Assert.Equal(
            "crm.elitesochi.com",
            new PortKnockStep(228, count: 2).ResolvedHost("crm.elitesochi.com"));
        Assert.Equal(
            "45.10.53.84",
            new PortKnockStep(228, "45.10.53.84").ResolvedHost("crm.elitesochi.com"));
    }

    /// <summary>
    /// Задержка перед первым REGISTER должна быть известна заранее: её видно в
    /// журнале, и она же — то, чем удалённый запуск отличается от офисного.
    /// </summary>
    [Fact]
    public void Оценка_длительности_считает_промежутки_а_не_пакеты()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), PortKnockSequence.Production.EstimatedDuration);

        PortKnockSequence single = new() { Steps = [new PortKnockStep(100)] };
        Assert.Equal(TimeSpan.Zero, single.EstimatedDuration);
    }

    // MARK: - Когда пропускать

    [Fact]
    public void Первый_стук_проходит_всегда()
    {
        PortKnockThrottle throttle = new(TimeSpan.FromSeconds(600));

        Assert.True(throttle.ShouldKnock(SipPathOpenReason.Registration, DateTimeOffset.UtcNow));
        Assert.True(throttle.ShouldKnock(SipPathOpenReason.Periodic, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void До_истечения_срока_стук_пропускается()
    {
        PortKnockThrottle throttle = new(TimeSpan.FromSeconds(600));
        var start = DateTimeOffset.UtcNow;
        throttle.RecordKnock(start);

        Assert.False(throttle.ShouldKnock(SipPathOpenReason.Periodic, start.AddSeconds(1)));
        Assert.False(throttle.ShouldKnock(SipPathOpenReason.Registration, start.AddSeconds(599)));
        Assert.True(throttle.ShouldKnock(SipPathOpenReason.Periodic, start.AddSeconds(600)));
    }

    /// <summary>
    /// Повтор после отказа не пропускается никогда.
    /// </summary>
    ///
    /// <remarks>
    /// Это главное правило здесь: отказ регистрации и есть самый сильный
    /// признак, что адрес сменился и в списке шлюза нас больше нет.
    /// </remarks>
    [Fact]
    public void Повтор_после_отказа_не_пропускается()
    {
        PortKnockThrottle throttle = new(TimeSpan.FromSeconds(600));
        var start = DateTimeOffset.UtcNow;
        throttle.RecordKnock(start);

        Assert.True(throttle.ShouldKnock(SipPathOpenReason.Retry, start.AddSeconds(1)));
    }

    [Fact]
    public void Сброс_забывает_прошлый_стук()
    {
        PortKnockThrottle throttle = new(TimeSpan.FromSeconds(600));
        var start = DateTimeOffset.UtcNow;
        throttle.RecordKnock(start);

        Assert.False(throttle.ShouldKnock(SipPathOpenReason.Periodic, start));
        throttle.Invalidate();
        Assert.True(throttle.ShouldKnock(SipPathOpenReason.Periodic, start));
    }

    // MARK: - Сам пакет

    /// <summary>
    /// <c>ping -s N</c> кладёт N байт данных после восьмибайтового заголовка.
    /// </summary>
    ///
    /// <remarks>
    /// Длина и есть подпись стука, поэтому это не деталь реализации, а
    /// требование. Заголовок на Windows собирает система — проверять тут нечего,
    /// а вот длину данных задаём мы.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(125)]
    [InlineData(126)]
    [InlineData(228)]
    public void Длина_данных_ровно_такая_какую_просили(int payload)
    {
        Assert.Equal(payload, PortKnockPayload.Make(payload).Length);
    }

    [Fact]
    public void Заполнение_повторяет_ping()
    {
        var payload = PortKnockPayload.Make(300);

        Assert.Equal(0, payload[0]);
        Assert.Equal(255, payload[255]);
        Assert.Equal(0, payload[256]);
    }

    // MARK: - Настройки

    /// <summary>
    /// Файл настроек без списка шагов даёт боевую последовательность, а с пустым
    /// списком — выключенный стук.
    /// </summary>
    ///
    /// <remarks>
    /// Иначе выключить стук правкой файла было бы невозможно: пустой список
    /// читался бы как «взять умолчание».
    /// </remarks>
    [Fact]
    public void Разбор_настроек_различает_отсутствие_и_пустоту()
    {
        var empty = JsonSerializer.Deserialize<PortKnockSequence>("{}")!;

        Assert.Equal(PortKnockSequence.Production.Steps.Count, empty.Steps.Count);
        Assert.Equal(600, empty.RepeatIntervalSeconds);

        var disabled = JsonSerializer.Deserialize<PortKnockSequence>("""{"Steps":[]}""")!;

        Assert.True(disabled.IsEmpty);
    }

    [Fact]
    public void Последовательность_переживает_запись_и_чтение()
    {
        var encoded = JsonSerializer.Serialize(PortKnockSequence.Production);
        var decoded = JsonSerializer.Deserialize<PortKnockSequence>(encoded)!;

        Assert.Equal(PortKnockSequence.Production.PacketCount, decoded.PacketCount);
        Assert.Equal(
            PortKnockSequence.Production.Steps.Select(step => (step.Host, step.PayloadBytes, step.Count)),
            decoded.Steps.Select(step => (step.Host, step.PayloadBytes, step.Count)));
    }
}
