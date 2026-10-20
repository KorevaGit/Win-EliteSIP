using EliteSIP.App.Incoming;
using EliteSIP.App.Resources;
using EliteSIP.CallHistory;

namespace EliteSIP.App.History;

/// <summary>
/// Что пишется в строке истории: заголовок и нижняя строка.
/// </summary>
///
/// <remarks>
/// <para>
/// Правило — то же, что у окна входящего (<see cref="IncomingCallSubject"/>), а не
/// своё: один и тот же звонок обязан называться одинаково везде, где его
/// видно. До 1 октября 2026 история писала имя из <c>From</c> или номер как
/// есть — раздача показывалась номером переводящего, звонок по сделке —
/// собственным добавочным, а мобильный лида лежал открытым текстом за весь
/// день. Так же сделано на macOS (CallHistoryWindowView, решения 19, 20 и
/// 27 августа 2026).
/// </para>
/// <para>
/// Номер в записи хранится как пришёл (решение 30 июля 2026), маска
/// накладывается при показе. Исходящие маски не получают и разбору не
/// подлежат: номер набрал сам оператор.
/// </para>
/// </remarks>
public static class HistoryPresentation
{
    /// <summary>Главная строка.</summary>
    public static string Title(CallRecord record, string ownNumber, bool masksMobileNumbers)
    {
        if (Subject(record, ownNumber, masksMobileNumbers) is not IncomingCallSubject subject)
        {
            return record.Title;
        }

        // Номера нет вовсе — «неизвестный номер» из самой записи, а не пустая
        // маска.
        return record.Number.Length == 0 && string.IsNullOrEmpty(record.DisplayName)
            ? record.Title
            : subject.Headline;
    }

    /// <summary>
    /// Нижняя строка: номер, если он не скрыт, и пометки консультации,
    /// перевода и конференции.
    /// </summary>
    ///
    /// <remarks>
    /// Номер стоит внизу всегда, даже когда он же написан наверху: без него
    /// нижняя строка у большинства звонков пустая, а высоту всё равно
    /// занимает. На звонке по сделке номера нет — там это свой же добавочный,
    /// — и вместо него стоит источник, «Bitrix».
    /// </remarks>
    public static string Subtitle(CallRecord record, string ownNumber, bool masksMobileNumbers)
    {
        List<string> parts = [];
        IncomingCallSubject? subject = Subject(record, ownNumber, masksMobileNumbers);

        if (subject?.Kind is IncomingCallKind.SelfCall)
        {
            parts.Add(IncomingCallSubject.DealSource);
        }
        else if (record.Number.Length > 0)
        {
            parts.Add(subject is null ? record.Number : subject.ShownNumber(record.Number));
        }

        if (record.Role is CallRole.Consultation)
        {
            parts.Add(Strings.Get("HistoryConsultation"));
        }

        if (record.WasTransferred)
        {
            parts.Add(Strings.Get("HistoryTransferred"));
        }

        if (record.WasConference)
        {
            parts.Add(Strings.Get("HistoryConference"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Разбор входящего. <c>null</c> у исходящих: про что вызов, знает сам
    /// оператор, он его и набрал.
    /// </summary>
    ///
    /// <remarks>
    /// Раздачу отличает признак, сохранённый в записи на входящем INVITE
    /// (<see cref="CallRecord.WasDistribution"/>): заголовок автоподъёма живёт
    /// ровно столько, сколько сам вызов, а по номеру и имени раздачу от
    /// звонка коллеги не отличить. Свой добавочный — из активного профиля:
    /// список и так отобран по нему.
    /// </remarks>
    private static IncomingCallSubject? Subject(CallRecord record, string ownNumber, bool masksMobileNumbers) =>
        record.Direction is CallDirection.Incoming
            ? IncomingCallSubject.Classify(record.Number, record.DisplayName, record.WasDistribution, ownNumber, masksMobileNumbers)
            : null;
}
