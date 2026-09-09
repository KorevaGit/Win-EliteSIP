namespace EliteSIP.PanelLink.Tests;

/// <summary>Управляемые поля.</summary>
public sealed class ManagedFieldsTests
{
    /// <summary>
    /// Главное правило всей линии, и цена его нарушения названа в оригинале:
    /// предустановка, написанная ради макросов, молча стёрла бы политику защиты
    /// на всех рабочих местах разом.
    /// </summary>
    [Fact]
    public void Отсутствующий_блок_остаётся_пустым_а_не_умолчанием()
    {
        var fields = ManagedFields.Parse("""{"dtmf":{"toneMilliseconds":90}}""");

        Assert.NotNull(fields.Dtmf);
        Assert.Null(fields.IncomingCall);
        Assert.Null(fields.Conference);
        Assert.Null(fields.PortKnock);
        Assert.Null(fields.SiteAddresses);
        Assert.Null(fields.AcceptsAnyTLSCertificate);
        Assert.Null(fields.Transport);
    }

    /// <summary>
    /// Протокол приезжает строкой и разбирается строкой: опознаёт её тот, кто
    /// накладывает поля, — там же, где живёт правило «незнакомое не
    /// применяется».
    /// </summary>
    [Fact]
    public void Протокол_связи_с_АТС_читается_как_есть()
    {
        Assert.Equal("udp", ManagedFields.Parse("""{"transport":"udp"}""").Transport);
        Assert.Equal("tls", ManagedFields.Parse("""{"transport":"tls"}""").Transport);

        // Незнакомое сюда доходит нетронутым — отсеивает его наложение, а не
        // разбор: разбор не знает, что умеет линия.
        Assert.Equal("ws", ManagedFields.Parse("""{"transport":"ws"}""").Transport);

        // Не строка — это не «умолчание», а «поля нет»: машина сохранит своё.
        Assert.Null(ManagedFields.Parse("""{"transport":5060}""").Transport);
    }

    /// <summary>
    /// То же правило на уровне поля: «панель прислала ноль» и «панель ничего не
    /// присылала» — разные вещи, и различить их обязан тип, а не соглашение.
    /// </summary>
    [Fact]
    public void Отсутствующее_поле_внутри_блока_остаётся_пустым()
    {
        var dtmf = ManagedFields.Parse("""{"dtmf":{"toneMilliseconds":90}}""").Dtmf;

        Assert.NotNull(dtmf);
        Assert.Equal(90, dtmf.ToneMilliseconds);
        Assert.Null(dtmf.GapMilliseconds);
        Assert.Null(dtmf.Macros);
        Assert.Null(dtmf.MacroColumns);
    }

    /// <summary>
    /// Ноль — это значение, и спутать его с отсутствием нельзя: выключенная
    /// пауза и неуправляемая пауза — разные состояния машины.
    /// </summary>
    [Fact]
    public void Ноль_и_пустая_строка_это_значения_а_не_отсутствие()
    {
        var fields = ManagedFields.Parse(
            """{"dtmf":{"pauseMilliseconds":0},"conference":{"featureCode":""}}""");

        Assert.Equal(0, fields.Dtmf?.PauseMilliseconds);
        Assert.Equal(string.Empty, fields.Conference?.FeatureCode);
        Assert.Null(fields.Conference?.RoomExtension);
    }

    /// <summary>
    /// Отказ от файла целиком недопустим: тогда до старой сборки не доедет и
    /// смена адреса АТС, без которой она не звонит.
    /// </summary>
    [Fact]
    public void Незнакомое_поле_не_мешает_разобрать_остальное()
    {
        var fields = ManagedFields.Parse(
            """
            {"dtmf":{"toneMilliseconds":90,"чегоТоНовое":42},
             "совсемНовыйБлок":{"a":1},
             "siteAddresses":{"office":"10.0.0.1","remote":"crm.example.com"}}
            """);

        Assert.Equal(90, fields.Dtmf?.ToneMilliseconds);
        Assert.Equal("10.0.0.1", fields.SiteAddresses?.Office);
        Assert.Equal("crm.example.com", fields.SiteAddresses?.Remote);
    }

    /// <summary>
    /// Испорченный блок теряется в одиночку. Это и есть то, ради чего блоки
    /// разбираются порознь: сломанный dtmf не должен уносить адрес АТС.
    /// </summary>
    [Fact]
    public void Сломанный_блок_не_уносит_соседние()
    {
        var fields = ManagedFields.Parse(
            """
            {"dtmf":{"toneMilliseconds":"это не число"},
             "siteAddresses":{"office":"10.0.0.1","remote":"crm.example.com"},
             "acceptsAnyTLSCertificate":false}
            """);

        Assert.Null(fields.Dtmf);
        Assert.Equal("10.0.0.1", fields.SiteAddresses?.Office);
        Assert.False(fields.AcceptsAnyTLSCertificate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("42")]
    public void Мусор_вместо_файла_даёт_пустые_поля_а_не_отказ(string garbage)
    {
        Assert.Equal(new ManagedFields(), ManagedFields.Parse(garbage));
    }

    /// <summary>
    /// Признак «управляется сервером» приложение выводит из режима машины, а
    /// приехавший полем он означал бы два источника одного факта.
    /// </summary>
    [Fact]
    public void Признак_управления_сервером_из_файла_не_берётся()
    {
        var fields = ManagedFields.Parse(
            """{"incomingCall":{"isEnabled":true,"isServerManaged":true}}""");

        Assert.True(fields.IncomingCall?.IsEnabled);

        // Поля просто нет в типе: взять его неоткуда, даже если панель пришлёт.
        Assert.Null(typeof(ManagedFields.CallGuard).GetProperty("IsServerManaged"));
    }

    [Fact]
    public void Клавиши_разбираются_списком()
    {
        var macros = ManagedFields.Parse(
            """
            {"dtmf":{"macros":[
               {"id":"a","title":"ЮРИСТ","sequence":"*02,101","transfersCall":true},
               {"id":"b","title":"СКЛАД","sequence":"*02,110"}]}}
            """).Dtmf?.Macros;

        Assert.NotNull(macros);
        Assert.Equal(2, macros.Count);
        Assert.Equal("ЮРИСТ", macros[0].Title);
        Assert.True(macros[0].TransfersCall);

        // У второй клавиши пометки нет — и это «администратор не сказал», а не
        // «перевода тут нет».
        Assert.Null(macros[1].TransfersCall);
    }

    /// <summary>
    /// Панель прежних ревизий шлёт словарь очередей — клиент его больше не
    /// знает. Незнакомый ключ обязан быть пропущен молча, а не уронить разбор:
    /// иначе машина, не успевшая получить новую ревизию, осталась бы вообще без
    /// управляемых полей.
    /// </summary>
    [Fact]
    public void Словарь_очередей_от_старой_панели_пропускается_молча()
    {
        var fields = ManagedFields.Parse(
            """
            {"queues":{"queues":[{"id":"q","number":"1000","title":"Раздача"}]},
             "conference":{"featureCode":"*3"}}
            """);

        Assert.Equal("*3", fields.Conference?.FeatureCode);
    }

    [Fact]
    public void Стук_разбирается_вместе_с_шагами()
    {
        var knock = ManagedFields.Parse(
            """
            {"portKnock":{"steps":[{"payloadBytes":228,"count":2},
                                   {"host":"45.10.53.84","payloadBytes":126,"count":1}],
                          "spacingSeconds":1.5,"repeatIntervalSeconds":600}}
            """).PortKnock;

        Assert.NotNull(knock?.Steps);
        Assert.Equal(2, knock.Steps.Count);

        // Пустой адрес означает «основной адрес АТС», и отсутствие ключа здесь
        // значит то же самое.
        Assert.Null(knock.Steps[0].Host);
        Assert.Equal("45.10.53.84", knock.Steps[1].Host);
        Assert.Equal(1.5, knock.SpacingSeconds);
    }

    /// <summary>
    /// Проверка на том же файле, что приезжает с боевой панели: разбор обязан
    /// сходиться не с придуманным JSON, а с настоящим.
    /// </summary>
    [Fact]
    public void Поля_из_настоящего_файла_предустановок_разбираются()
    {
        var bundle = PresetBundle.Verified(Fixture.SignedBundle, Fixture.PublicKey);
        var entry = Assert.Single(bundle.Presets);

        var fields = ManagedFields.Parse(entry.Fields);

        Assert.Equal("192.168.1.2", fields.SiteAddresses?.Office);
        Assert.Equal("crm.elitesochi.com", fields.SiteAddresses?.Remote);

        // Всем остальным панель в этом файле не управляет.
        Assert.Null(fields.Dtmf);
        Assert.Null(fields.IncomingCall);
    }
}
