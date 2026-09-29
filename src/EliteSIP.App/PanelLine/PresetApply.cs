using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Наложение управляемых полей панели на настройки машины.
/// </summary>
///
/// <remarks>
/// Разбор живёт в пакете <c>PanelLink</c> — он проверяется тестами, — а здесь
/// только перекладывание: из необязательных полей в настоящие. Перекладывание
/// нарочно многословное, по строке на поле. Это не небрежность: каждая строка
/// читается и проверяется глазами по отдельности, а всякая попытка сократить их
/// в цикл или в таблицу превращает два десятка отдельных решений в одно общее —
/// и ошибка в нём уезжает на все рабочие места разом.
///
/// <b>Правило одно на весь файл: <c>null</c> — это «панель этим не
/// управляет».</b> Не «ноль», не «пусто», не «выключено». Машина сохраняет своё
/// текущее значение, и записывается оно тем, что присвоения просто не
/// происходит. Правило принято в оригинале и стоит там же: предустановка,
/// написанная ради макросов, иначе молча стёрла бы политику защиты.
///
/// <b>Чего эта сборка не накладывает.</b> Тайминги DTMF (<c>toneMilliseconds</c>,
/// <c>gapMilliseconds</c>, <c>pauseMilliseconds</c>) не накладываются по другой
/// причине: у них нет настройки, потому что до тракта они не доходят и сейчас —
/// <c>DtmfTiming</c> берётся умолчаниями внутри <c>MediaCore</c>, и завести
/// настройку значило бы показать администратору ползунок, который ничего не
/// делает. Оба случая молчат осознанно, и молчат <b>одинаково</b> с правилом
/// выше: машина сохраняет своё.
/// </remarks>
internal static class PresetApply
{
    /// <summary>
    /// Накладывает управляемые поля, оставляя нетронутым всё, чем панель не
    /// управляет.
    /// </summary>
    internal static void Apply(this AppSettings settings, ManagedFields fields)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fields);

        ApplyDtmf(settings, fields.Dtmf);
        ApplyIncomingCall(settings, fields.IncomingCall);
        ApplyConference(settings, fields.Conference);
        ApplySiteAddresses(settings, fields.SiteAddresses);
        ApplyPortKnock(settings, fields.PortKnock);
        ApplyTlsTrust(settings, fields.AcceptsAnyTLSCertificate);
        ApplyTransport(settings, fields.Transport);
        ApplyAutoAnswer(settings, fields.AutoAnswer, fields.AutoAnswerNumbers);

        // Признак «этим управляет сервер» выводится из режима машины, а не из
        // файла, и ставится здесь — в одном месте на все управляемые поля.
        settings.IncomingCall.IsServerManaged = settings.Panel.IsManaged;
    }

    /// <summary>
    /// Тронуто ли хоть одно поле, которым управляет панель.
    /// </summary>
    ///
    /// <remarks>
    /// Список обязан совпадать с тем, что накладывает <see cref="Apply"/>, и
    /// потому стоит вплотную к нему: разъехавшись, эти два перечня дадут машину,
    /// которая считает себя связанной с панелью, а на деле правится руками, — или
    /// обратное. Оба хуже.
    ///
    /// Признак «управляется сервером» из сравнения выброшен намеренно: его ставит
    /// не человек, а само наложение полей, и попадание его в сравнение означало
    /// бы, что связка рвётся от собственного признака связки.
    ///
    /// Очередей здесь нет, и это не забывчивость: панель ими не управляет — в
    /// контракте таких полей нет вовсе. Названия раздач заводит администратор на
    /// месте, и связки с панелью они не рвут.
    /// </remarks>
    internal static bool DiffersInPanelManagedFields(this AppSettings settings, AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(other);

        return DiffersInMacros(settings.Dtmf, other.Dtmf)
            || settings.Dtmf.MacroColumns != other.Dtmf.MacroColumns
            || settings.Dtmf.MacroHeight != other.Dtmf.MacroHeight
            || settings.Dtmf.MacroHeightIsManual != other.Dtmf.MacroHeightIsManual
            || DiffersInGuard(settings.IncomingCall, other.IncomingCall)
            || !string.Equals(settings.Pbx.ConferenceFeatureCode, other.Pbx.ConferenceFeatureCode, StringComparison.Ordinal)
            || !string.Equals(settings.Pbx.OfficeAddress, other.Pbx.OfficeAddress, StringComparison.Ordinal)
            || !string.Equals(settings.Pbx.RemoteAddress, other.Pbx.RemoteAddress, StringComparison.Ordinal)
            || settings.Pbx.AcceptsAnyTlsCertificate != other.Pbx.AcceptsAnyTlsCertificate
            || settings.Pbx.Transport != other.Pbx.Transport
            || !string.Equals(settings.IncomingCall.AutoAnswer, other.IncomingCall.AutoAnswer, StringComparison.Ordinal)
            || !settings.IncomingCall.AutoAnswerNumbers.SequenceEqual(other.IncomingCall.AutoAnswerNumbers, StringComparer.Ordinal)
            || DiffersInKnock(settings.PortKnock, other.PortKnock);
    }

    // MARK: - Клавиши

    private static void ApplyDtmf(AppSettings settings, ManagedFields.DtmfFields? incoming)
    {
        if (incoming is null)
        {
            return;
        }

        if (incoming.MacroColumns is { } columns)
        {
            settings.Dtmf.MacroColumns = columns;
        }

        if (incoming.MacroHeight is { } height)
        {
            settings.Dtmf.MacroHeight = height;
        }

        if (incoming.MacroHeightIsManual is { } manual)
        {
            settings.Dtmf.MacroHeightIsManual = manual;
        }

        if (incoming.Macros is not { } macros)
        {
            return;
        }

        // Список заменяется целиком, а не сливается по одной клавише.
        //
        // Слияние выглядело бы бережнее и было бы неверно: раскладку задаёт
        // администратор целиком, и удалённая им клавиша обязана исчезнуть.
        // Слияние оставило бы её на месте навсегда — ровно ту клавишу, которую
        // убрали, потому что она набирала не тот код.
        settings.Dtmf.Macros.Clear();
        foreach (var macro in macros)
        {
            settings.Dtmf.Macros.Add(new MacroSetting
            {
                Id = Identity(macro.ID),
                Title = macro.Title ?? string.Empty,
                Sequence = macro.Sequence ?? string.Empty,
                TransfersCall = macro.TransfersCall ?? false,
            });
        }
    }

    // MARK: - Приём вызова

    private static void ApplyIncomingCall(AppSettings settings, ManagedFields.CallGuard? incoming)
    {
        if (incoming is null)
        {
            return;
        }

        var guard = settings.IncomingCall;

        if (incoming.IsEnabled is { } isEnabled)
        {
            guard.IsEnabled = isEnabled;
        }

        if (incoming.IsRandomPositionEnabled is { } randomPosition)
        {
            guard.IsRandomPositionEnabled = randomPosition;
        }

        if (incoming.TunesRandomnessByHand is { } tunesRandomness)
        {
            guard.TunesRandomnessByHand = tunesRandomness;
        }

        if (incoming.MinimumTravel is { } minimumTravel)
        {
            guard.MinimumTravel = minimumTravel;
        }

        if (incoming.ScreenMargin is { } screenMargin)
        {
            guard.ScreenMargin = screenMargin;
        }

        if (incoming.TargetCount is { } targetCount)
        {
            guard.TargetCount = targetCount;
        }

        if (incoming.RequiresCursorMovement is { } requiresMovement)
        {
            guard.RequiresCursorMovement = requiresMovement;
        }

        if (incoming.TunesLivenessByHand is { } tunesLiveness)
        {
            guard.TunesLivenessByHand = tunesLiveness;
        }

        if (incoming.RequiredCursorTravel is { } requiredTravel)
        {
            guard.RequiredCursorTravel = requiredTravel;
        }

        if (incoming.RequiredCursorSamples is { } requiredSamples)
        {
            guard.RequiredCursorSamples = requiredSamples;
        }

        if (incoming.RejectsSyntheticEvents is { } rejectsSynthetic)
        {
            guard.RejectsSyntheticEvents = rejectsSynthetic;
        }

        // Признак «управляется сервером» не трогается ни при каких
        // обстоятельствах: его выводит режим машины, а не файл. Приехавший полем
        // он означал бы два источника одного факта — поэтому его нет и в самом
        // контракте.
        //
        // Приведения к своим границам после наложения здесь не видно, потому что
        // оно уже случилось: сеттеры настроек защиты сами зажимают значения в
        // свои пределы. Панель проверяет те же пределы у себя, но её проверка и
        // наши границы могут разойтись на новой сборке, и последнее слово должно
        // оставаться за машиной.
    }

    // MARK: - Конференция

    private static void ApplyConference(AppSettings settings, ManagedFields.ConferenceFields? incoming)
    {
        if (incoming?.FeatureCode is { } code)
        {
            settings.Pbx.ConferenceFeatureCode = code;
        }

        // `roomExtension` из контракта здесь не оседает: у этой сборки конференция
        // собирается кодом возможности на активной линии, отдельного номера
        // комнаты у неё нет. Поле остаётся неприменённым молча — по тому же
        // правилу, что и `null`.
    }

    // MARK: - Адреса

    private static void ApplySiteAddresses(AppSettings settings, ManagedFields.SiteAddressesFields? incoming)
    {
        if (incoming is null)
        {
            return;
        }

        // Самое опасное, что возит эта линия: разъехавшийся адрес означает
        // телефон, который не звонит на всех рабочих местах разом. Поэтому
        // половины накладываются порознь — панель может управлять одной и не
        // управлять другой.
        if (incoming.Office is { } office)
        {
            settings.Pbx.OfficeAddress = office;
        }

        if (incoming.Remote is { } remote)
        {
            settings.Pbx.RemoteAddress = remote;
        }
    }

    /// <summary>
    /// Протокол связи с АТС.
    /// </summary>
    ///
    /// <remarks>
    /// Незнакомая строка не применяется вовсе — это то же правило, что и у
    /// <c>null</c>, и по той же причине. Панель проверяет значение у себя и
    /// присылает одно из двух, но разбор здесь обязан быть терпимым: приехавшее
    /// из будущего <c>ws</c> не должно уводить рабочее место на UDP молча.
    ///
    /// Порт не трогается. Свой, вписанный руками, остаётся своим.
    /// </remarks>
    /// <summary>
    /// Стук по портам.
    /// </summary>
    ///
    /// <remarks>
    /// Пустой список шагов — это «стучать нечем», то есть выключенный стук, а не
    /// отсутствие управления. Отличает их то же самое, что и везде здесь:
    /// отсутствие ключа даёт <c>null</c> и сюда не доходит.
    ///
    /// Шаги заменяются целиком по той же причине, что и клавиши: последовательность
    /// задаёт администратор целиком, и убранный им шаг обязан исчезнуть.
    /// </remarks>
    private static void ApplyPortKnock(AppSettings settings, ManagedFields.PortKnockFields? incoming)
    {
        if (incoming is null)
        {
            return;
        }

        if (incoming.SpacingSeconds is { } spacing)
        {
            settings.PortKnock.SpacingSeconds = spacing;
        }

        if (incoming.RepeatIntervalSeconds is { } interval)
        {
            settings.PortKnock.RepeatIntervalSeconds = interval;
        }

        if (incoming.Steps is not { } steps)
        {
            return;
        }

        settings.PortKnock.Steps.Clear();
        foreach (var step in steps)
        {
            settings.PortKnock.Steps.Add(new PortKnockStepSetting
            {
                Host = step.Host ?? string.Empty,
                PayloadBytes = step.PayloadBytes ?? 0,
                Count = step.Count ?? 1,
            });
        }
    }

    private static void ApplyTlsTrust(AppSettings settings, bool? incoming)
    {
        // Единственное поле, которым панель управляет затем, чтобы держать его
        // выключенным: аудит оригинала нашёл включённое ради лаборатории
        // значение, молча оставшееся включённым на боевом профиле.
        if (incoming is { } accepts)
        {
            settings.Pbx.AcceptsAnyTlsCertificate = accepts;
        }
    }

    private static void ApplyTransport(AppSettings settings, string? incoming)
    {
        if (incoming is null)
        {
            return;
        }

        if (string.Equals(incoming, "udp", StringComparison.OrdinalIgnoreCase))
        {
            settings.Pbx.Transport = SipTransport.Udp;
        }
        else if (string.Equals(incoming, "tls", StringComparison.OrdinalIgnoreCase))
        {
            settings.Pbx.Transport = SipTransport.Tls;
        }
    }

    /// <summary>
    /// Автоподъём: режим — только знакомый (незнакомый из более новой панели
    /// не применяется, остаётся свой); список — целиком, как клавиши.
    /// </summary>
    private static void ApplyAutoAnswer(AppSettings settings, string? mode, IReadOnlyList<string>? numbers)
    {
        if (AutoAnswerModes.IsKnown(mode))
        {
            settings.IncomingCall.AutoAnswer = mode!;
        }

        if (numbers is not null)
        {
            settings.IncomingCall.AutoAnswerNumbers = [.. numbers];
        }
    }

    // MARK: - Опознание строк

    /// <summary>
    /// Идентификатор клавиши из того, что прислала панель.
    /// </summary>
    ///
    /// <remarks>
    /// Панель выдаёт настоящие UUID и проверяет это у себя, поэтому обычная
    /// дорога — прямой разбор. Запасная нужна на случай, когда прислали не
    /// UUID: <b>выводим устойчивый идентификатор из строки</b>, а не заводим
    /// новый. Новый на каждом наложении означал бы, что клавиша меняет личность
    /// каждые два часа, — а по личности её отличают и настройки, и всё, что на
    /// них завязано.
    /// </remarks>
    private static Guid Identity(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return Guid.NewGuid();
        }

        if (Guid.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));

        return new Guid(digest.AsSpan(0, 16));
    }

    private static bool DiffersInMacros(DtmfSettings mine, DtmfSettings theirs)
    {
        if (mine.Macros.Count != theirs.Macros.Count)
        {
            return true;
        }

        for (var index = 0; index < mine.Macros.Count; index++)
        {
            var left = mine.Macros[index];
            var right = theirs.Macros[index];

            if (!string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                || !string.Equals(left.Sequence, right.Sequence, StringComparison.Ordinal)
                || left.TransfersCall != right.TransfersCall)
            {
                return true;
            }
        }

        return false;
    }

    private static bool DiffersInKnock(PortKnockSettings mine, PortKnockSettings theirs)
    {
        if (mine.Steps.Count != theirs.Steps.Count
            || mine.SpacingSeconds != theirs.SpacingSeconds
            || mine.RepeatIntervalSeconds != theirs.RepeatIntervalSeconds)
        {
            return true;
        }

        for (var index = 0; index < mine.Steps.Count; index++)
        {
            var left = mine.Steps[index];
            var right = theirs.Steps[index];

            if (!string.Equals(left.Host, right.Host, StringComparison.Ordinal)
                || left.PayloadBytes != right.PayloadBytes
                || left.Count != right.Count)
            {
                return true;
            }
        }

        return false;
    }

    private static bool DiffersInGuard(IncomingCallSettings mine, IncomingCallSettings theirs)
        => mine.IsEnabled != theirs.IsEnabled
            || mine.IsRandomPositionEnabled != theirs.IsRandomPositionEnabled
            || mine.TunesRandomnessByHand != theirs.TunesRandomnessByHand
            || mine.MinimumTravel != theirs.MinimumTravel
            || mine.ScreenMargin != theirs.ScreenMargin
            || mine.TargetCount != theirs.TargetCount
            || mine.RequiresCursorMovement != theirs.RequiresCursorMovement
            || mine.TunesLivenessByHand != theirs.TunesLivenessByHand
            || mine.RequiredCursorTravel != theirs.RequiredCursorTravel
            || mine.RequiredCursorSamples != theirs.RequiredCursorSamples
            || mine.RejectsSyntheticEvents != theirs.RejectsSyntheticEvents;
}
