using System.Text.Json;

namespace EliteSIP.PanelLink;

/// <summary>
/// Управляемые поля предустановки, разобранные и <b>необязательные каждое</b>.
/// </summary>
///
/// <remarks>
/// Это самая опасная часть линии предустановок, и опасность у неё одна:
/// <b>отсутствующее поле означает «панель им не управляет», а не «сбросить в
/// умолчание».</b> Машина обязана сохранить своё текущее значение. Правило
/// принято ещё в оригинале, и цена его нарушения названа там же: предустановка,
/// написанная ради макросов, молча стёрла бы политику защиты — на всех рабочих
/// местах разом и обязательным обновлением, которое сотрудник не может
/// отложить.
///
/// Отсюда всё устройство типа: каждое поле — <c>Nullable</c>, и «нет значения»
/// выражено системой типов, а не соглашением. Применяющий код физически не
/// может перепутать «панель прислала ноль» с «панель ничего не присылала»: во
/// втором случае у него на руках <c>null</c>, и присвоить его некуда. В C# это
/// работает ровно как в Swift — при условии, что <c>Nullable</c> включён на всё
/// решение, а он включён в <c>Directory.Build.props</c>.
///
/// Разбор терпимый — второе правило той же пары. Незнакомое поле пропускается,
/// понятное применяется, а <b>отказ от файла целиком недопустим</b>: тогда до
/// старой сборки не доедет и смена адреса АТС, без которой она не звонит.
/// Поэтому каждый блок разбирается сам по себе, и сломанный блок теряется в
/// одиночку, а не уносит остальные.
///
/// Зеркальное правило на стороне панели — строгость: незнакомый ключ при
/// сохранении ревизии там ошибка. Это единственное место, где опечатку в имени
/// поля ещё можно показать человеку.
/// </remarks>
public sealed record ManagedFields
{
    /// <summary>
    /// Имена полей в файле — те же, что в оригинале: <c>isEnabled</c>,
    /// <c>toneMilliseconds</c> и так далее. Панель кладёт их именно так, и
    /// переименовать их здесь значило бы перестать понимать уже выложенные
    /// файлы.
    /// </summary>
    private static readonly JsonSerializerOptions BlockOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    public DtmfFields? Dtmf { get; init; }

    public CallGuard? IncomingCall { get; init; }

    public ConferenceFields? Conference { get; init; }

    public PortKnockFields? PortKnock { get; init; }

    public SiteAddressesFields? SiteAddresses { get; init; }

    /// <summary>
    /// Живёт в приложении внутри активного профиля, а не в общих настройках, —
    /// поэтому и в контракте стоит полем верхнего уровня.
    /// </summary>
    public bool? AcceptsAnyTLSCertificate { get; init; }

    /// <summary>
    /// Протокол связи с АТС: <c>udp</c> или <c>tls</c>.
    ///
    /// Строкой, а не перечислением линии, потому что <c>PanelLink</c> не зависит
    /// от <c>SipCore</c> и зависеть не должен: здесь разбор байтов панели, а не
    /// модель линии. Опознаёт строку тот, кто накладывает поля, — там же, где
    /// живёт правило «незнакомое не применяется».
    ///
    /// Управляется панелью потому, что это свойство АТС, а не машины: сменили на
    /// стороне сервера — сменить надо разом на всех местах. Порт с протоколом не
    /// приезжает: умолчания RFC 3261 приложение знает само.
    /// </summary>
    public string? Transport { get; init; }

    /// <summary>
    /// Режим автоподъёма: <c>off</c>, <c>always</c>, <c>header</c>, <c>list</c>.
    /// Строкой по той же причине, что <see cref="Transport"/>: незнакомое
    /// значение отбрасывает тот, кто накладывает поля.
    /// </summary>
    public string? AutoAnswer { get; init; }

    /// <summary>
    /// Номера для режима <c>list</c>. Приехал — заменяет список целиком;
    /// явный <c>null</c> (так Go пишет пустой список) — список пуст.
    /// </summary>
    public IReadOnlyList<string>? AutoAnswerNumbers { get; init; }

    /// <summary>
    /// Прятать ли мобильные номера звонящих. <c>null</c> — панель этим не
    /// управляет, место оставляет своё. Полем верхнего уровня, как автоподъём:
    /// в приложении это настройка входящих, а не часть политики защиты.
    /// </summary>
    public bool? MasksMobileNumbers { get; init; }

    // MARK: - Блоки
    //
    // Приставка `Fields` у половины из них — вынужденная: в C# вложенный тип не
    // может называться так же, как свойство, которое его держит, а имена
    // свойств здесь — имена полей контракта и меняться не должны. У `DtmfFields`
    // она снимает ещё и спор анализатора о регистре с полем `Dtmf`. У
    // `CallGuard` и `Macro` столкновения нет вовсе, поэтому и приставки у них
    // нет: давать её ради единообразия значило бы уводить имя от контракта там,
    // где этого никто не требовал.

    public sealed record DtmfFields
    {
        public int? ToneMilliseconds { get; init; }

        public int? GapMilliseconds { get; init; }

        public int? PauseMilliseconds { get; init; }

        public IReadOnlyList<Macro>? Macros { get; init; }

        public int? MacroColumns { get; init; }

        public int? MacroHeight { get; init; }

        public bool? MacroHeightIsManual { get; init; }
    }

    public sealed record Macro
    {
        /// <summary>
        /// Опознаёт клавишу между ревизиями: по нему панель считает, что
        /// изменилось, и по нему же клавиша остаётся собой при переименовании.
        /// </summary>
        public string? ID { get; init; }

        public string? Title { get; init; }

        public string? Sequence { get; init; }

        /// <summary>
        /// Уводит ли клавиша звонок другому человеку.
        ///
        /// Отвечает администратор, а не догадка по коду: <c>*02</c> — это
        /// Attended Transfer конкретно боевого сервера, а не общее правило
        /// Asterisk. Пометка уходит в историю звонков, которую читают как
        /// свидетельство при разборе жалобы, поэтому угадывать её нельзя.
        /// </summary>
        public bool? TransfersCall { get; init; }
    }

    /// <summary>
    /// Политика защиты приёма вызова.
    /// </summary>
    ///
    /// <remarks>
    /// Признака «управляется сервером» здесь нет намеренно, хотя в
    /// <c>CallGuardPolicy</c> он есть: приложение выводит его из режима машины —
    /// «Предустановка» или «Вручную», — а приехавший полем он означал бы два
    /// источника одного факта.
    /// </remarks>
    public sealed record CallGuard
    {
        public bool? IsEnabled { get; init; }

        public bool? IsRandomPositionEnabled { get; init; }

        public bool? TunesRandomnessByHand { get; init; }

        public double? MinimumTravel { get; init; }

        public double? ScreenMargin { get; init; }

        public int? TargetCount { get; init; }

        public bool? RequiresCursorMovement { get; init; }

        public bool? TunesLivenessByHand { get; init; }

        public double? RequiredCursorTravel { get; init; }

        public int? RequiredCursorSamples { get; init; }

        public bool? RejectsSyntheticEvents { get; init; }
    }

    public sealed record ConferenceFields
    {
        public string? FeatureCode { get; init; }

        public string? RoomExtension { get; init; }
    }

    public sealed record PortKnockFields
    {
        public IReadOnlyList<KnockStep>? Steps { get; init; }

        public double? SpacingSeconds { get; init; }

        public double? RepeatIntervalSeconds { get; init; }
    }

    public sealed record KnockStep
    {
        /// <summary>Пустой адрес означает «основной адрес АТС».</summary>
        public string? Host { get; init; }

        public int? PayloadBytes { get; init; }

        public int? Count { get; init; }
    }

    public sealed record SiteAddressesFields
    {
        public string? Office { get; init; }

        public string? Remote { get; init; }
    }

    // MARK: - Разбор

    /// <summary>
    /// Разбирает управляемые поля. <b>Не бросает никогда.</b>
    /// </summary>
    ///
    /// <remarks>
    /// Отсутствие броска — не небрежность, а требование: отказ от файла целиком
    /// недопустим. Что не разобралось, то остаётся <c>null</c> и просто не
    /// применяется; машина сохраняет по этому полю своё текущее значение — ровно
    /// то же, что она сделала бы, если бы панель им не управляла.
    ///
    /// Блоки разбираются порознь по той же причине: испорченный <c>dtmf</c> не
    /// должен уносить с собой <c>siteAddresses</c>, без которого машина не
    /// звонит.
    /// </remarks>
    public static ManagedFields Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ManagedFields();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new ManagedFields();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ManagedFields();
            }

            return new ManagedFields
            {
                Dtmf = ParseDtmf(root),
                IncomingCall = Block<CallGuard>(root, "incomingCall"),
                Conference = Block<ConferenceFields>(root, "conference"),
                PortKnock = ParsePortKnock(root),
                SiteAddresses = Block<SiteAddressesFields>(root, "siteAddresses"),
                AcceptsAnyTLSCertificate = Flag(root, "acceptsAnyTLSCertificate"),
                Transport = Text(root, "transport"),
                AutoAnswer = Text(root, "autoAnswer"),
                AutoAnswerNumbers = TextList(root, "autoAnswerNumbers"),
                MasksMobileNumbers = Flag(root, "masksMobileNumbers"),
            };
        }
    }

    /// <summary>
    /// Блок клавиш: как любой другой, но с одним уточнением про пустой список.
    /// </summary>
    ///
    /// <remarks>
    /// Spark пишет предустановку на Go, и пустой список клавиш уходит у него
    /// как <c>"macros": null</c>, а не <c>[]</c>. Обычное правило разбора —
    /// «<c>null</c> значит не прислали, оставить своё» — здесь давало машину,
    /// на которой убранные в Spark клавиши жили вечно. Поэтому явный
    /// <c>null</c> внутри присланного блока <c>dtmf</c> значит «клавиш нет»;
    /// отсутствующий блок или поле по-прежнему значат «не трогать».
    /// </remarks>
    private static DtmfFields? ParseDtmf(JsonElement root)
    {
        var dtmf = Block<DtmfFields>(root, "dtmf");

        if (dtmf is { Macros: null }
            && root.GetProperty("dtmf").TryGetProperty("macros", out var macros)
            && macros.ValueKind is JsonValueKind.Null)
        {
            return dtmf with { Macros = [] };
        }

        return dtmf;
    }

    /// <summary>Шаги стука — то же правило про <c>null</c>, что у клавиш.</summary>
    private static PortKnockFields? ParsePortKnock(JsonElement root)
    {
        var knock = Block<PortKnockFields>(root, "portKnock");

        if (knock is { Steps: null }
            && root.GetProperty("portKnock").TryGetProperty("steps", out var steps)
            && steps.ValueKind is JsonValueKind.Null)
        {
            return knock with { Steps = [] };
        }

        return knock;
    }

    /// <summary>Разбирает один блок, молча теряя непонятное.</summary>
    private static T? Block<T>(JsonElement root, string name)
        where T : class
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return value.Deserialize<T>(BlockOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Признак верхнего уровня.
    ///
    /// Не через <c>Deserialize</c>: у одиночного значения нет блока, который
    /// можно потерять целиком, а <c>true</c> из строки <c>"true"</c> здесь не
    /// принимается нарочно — панель кладёт признак признаком.
    /// </summary>
    private static bool? Flag(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>
    /// Список строк верхнего уровня. Отсутствует — <c>null</c> («не трогать»);
    /// явный <c>null</c> — пусто, по правилу клавиш. Не строки теряются молча.
    /// </summary>
    private static List<string>? TextList(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => [],
            JsonValueKind.Array => value.EnumerateArray()
                .Where(item => item.ValueKind is JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .Where(item => item.Length > 0)
                .ToList(),
            _ => null,
        };
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
