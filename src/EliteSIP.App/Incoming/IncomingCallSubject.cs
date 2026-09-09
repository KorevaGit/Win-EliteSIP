using EliteSIP.App.Resources;

namespace EliteSIP.App.Incoming;

/// <summary>Про что этот вызов — и, значит, что стоит на главном месте окна.</summary>
public enum IncomingCallKind
{
    /// <summary>Раздача из очереди. Название сверху, номер под ним — всегда.</summary>
    Queue,

    /// <summary>Звонок по сделке из CRM: АТС поднимает менеджера, потом набирает клиента.</summary>
    SelfCall,

    /// <summary>Обычный звонок: имя главным, номер под ним.</summary>
    Caller,
}

/// <summary>Разбор того, что пришло в INVITE.</summary>
///
/// <remarks>
/// Три случая, а не запасная цепочка внутри одного.
///
/// <b>Раздача.</b> Номер в ней принадлежит не клиенту, а сотруднику
/// колл-центра, переведшему лид: раздача устроена переводом, и в CallerID
/// переведённого вызова уходит добавочный переводящего. Менеджеру он нужен —
/// это тот, к кому идти с вопросом по лиду.
///
/// <b>Звонок по сделке.</b> Менеджер нажал «Позвонить» в CRM, АТС сперва
/// поднимает его самого и только потом набирает клиента. Отдельный случай,
/// потому что новичка он сбивает: в окне видно вызов от собственного
/// добавочного, и выглядит это как ошибка, а не как начало своего звонка.
///
/// <b>Словарь очередей на боевом сервере не срабатывает — снято 27 августа
/// 2026.</b> Живые INVITE показали, что диалплан CallerID не подменяет, и ни
/// одна запись словаря там не появится никогда. Прямой признак раздачи —
/// заголовок <c>X-Autoanswer</c>; запасной — форма <c>From</c>: на раздаче имя
/// есть и номер внешний, прямой внешний звонок приходит вообще без имени.
///
/// Одно место на все вызовы окна, включая проверочный показ из настроек: иначе
/// проверка показывала бы не то, что увидит оператор на боевом вызове, — а ради
/// этого её и открывают.
/// </remarks>
public sealed record IncomingCallSubject
{
    /// <summary>Сколько цифр делает номер внешним.</summary>
    ///
    /// <remarks>
    /// Добавочные у заказчика трёхзначные, номера очередей — от двух до четырёх
    /// цифр. Семь — первый порог, за которым не остаётся ни одного внутреннего
    /// номера, и при этом он ниже любого городского. Порог нужен затем, чтобы
    /// правило не съело обычный звонок коллеги: «Иванов» с добавочного 172 — это
    /// имя и номер, оба полезные, и прятать 172 не от кого.
    /// </remarks>
    private const int ExternalNumberLength = 7;

    private IncomingCallSubject(IncomingCallKind kind, string title, string number, string? name)
    {
        Kind = kind;
        Title = title;
        Number = number;
        Name = name;
    }

    public IncomingCallKind Kind { get; }

    private string Title { get; }

    private string Number { get; }

    private string? Name { get; }

    /// <summary>Заголовок раздачи.</summary>
    ///
    /// <remarks>
    /// Строкой в коде, а не настройкой, и это отмена словаря очередей: сорок
    /// заполненных записей не совпадали ни с одним боевым вызовом и не могли
    /// совпасть. Настройка, которую невозможно заполнить правильно, — это не
    /// настройка, а обряд.
    /// </remarks>
    public static string DistributionTitle => Strings.Get("IncomingDistribution");

    /// <summary>Заголовок случая «звонок по сделке».</summary>
    ///
    /// <remarks>
    /// Строкой в коде, а не записью словаря: добавочный менеджера приложение и
    /// так знает из активного профиля. Требовать вписать его руками значило бы
    /// оставить случай выключенным на каждой установке, где про него забыли.
    /// </remarks>
    public static string DealTitle => Strings.Get("IncomingDeal");

    /// <summary>Что стоит на главном месте — в окне входящего и в шапке панели.</summary>
    ///
    /// <remarks>
    /// Имя отвечает на «кто звонит», номер — только на «откуда»: увидев
    /// «IT_test», оператор знает, с чем к нему идут, а «172» ему сначала нужно
    /// вспомнить. Имени нет — главным становится номер: пустого главного места
    /// не бывает.
    /// </remarks>
    public string Headline => Kind switch
    {
        IncomingCallKind.Queue => Title,
        IncomingCallKind.SelfCall => DealTitle,
        _ => string.IsNullOrEmpty(Name) ? Shown(Number) : Name,
    };

    /// <summary>Номер мелкой строкой под заголовком. <c>null</c> — показывать нечего.</summary>
    ///
    /// <remarks>
    /// Пусто на звонке по сделке: там номер — свой же добавочный, одинаковый от
    /// вызова к вызову, и сказать им нечего. Пусто и на обычном звонке без
    /// имени: номер уже стоит заголовком и повторил бы сам себя.
    /// </remarks>
    public string? SecondaryNumber => Kind switch
    {
        IncomingCallKind.SelfCall => null,
        IncomingCallKind.Queue => Number.Length == 0 ? null : Shown(Number),
        _ => string.IsNullOrEmpty(Name) || Number.Length == 0 ? null : Shown(Number),
    };

    /// <summary>Прячет ли этот вызов номер собеседника целиком.</summary>
    public bool HidesNumber => Kind is IncomingCallKind.SelfCall;

    /// <summary>Разбирает вызов по тому, что пришло в INVITE.</summary>
    ///
    /// <remarks>
    /// Свой добавочный проверяется <b>раньше</b> словаря: администратор,
    /// вписавший туда собственный номер, иначе получил бы «раздачу» вместо
    /// звонка по сделке. Сравнение по цифрам — номер приходит из SIP, а в
    /// настройки его вписывает человек.
    /// </remarks>
    public static IncomingCallSubject Classify(
        string callerNumber,
        string? callerName,
        bool requestsAutoAnswer,
        string ownNumber,
        string? queueTitle = null)
    {
        var own = Digits(ownNumber);
        var caller = Digits(callerNumber);
        var named = queueTitle?.Trim();

        if (own.Length > 0 && own == caller)
        {
            return new IncomingCallSubject(IncomingCallKind.SelfCall, DealTitle, callerNumber, callerName);
        }

        // Название из словаря — уточнение поверх общего заголовка, и потому
        // проверяется раньше признаков раздачи, но позже своего добавочного:
        // администратор вписал номер очереди затем, чтобы она называлась своим
        // именем, а не общим «горячая раздача».
        if (!string.IsNullOrEmpty(named))
        {
            return new IncomingCallSubject(IncomingCallKind.Queue, named, callerNumber, callerName);
        }

        // Просьба снять трубку самостоятельно — и есть признак раздачи. Прямой
        // признак, а не догадка по форме From, и потому главный: боевая раздача
        // приходит с внутреннего номера и с именем, то есть выглядит ровно как
        // звонок коллеги, и отличить их по номеру или имени нельзя в принципе.
        //
        // Заголовку мы при этом не подчиняемся — приложение написано затем,
        // чтобы приём вызова требовал живого человека. Из него берётся только
        // ответ на вопрос «что это за вызов».
        if (requestsAutoAnswer || IsCampaign(caller, callerName))
        {
            return new IncomingCallSubject(IncomingCallKind.Queue, DistributionTitle, callerNumber, callerName);
        }

        return new IncomingCallSubject(IncomingCallKind.Caller, string.Empty, callerNumber, callerName);
    }

    /// <summary>Номер входящего в том виде, в каком его видит менеджер.</summary>
    ///
    /// <remarks>
    /// Одна дверь на все места, где номер показывается: окно входящего, шапка
    /// панели в разговоре и история. Три отдельных правила разошлись бы — это
    /// уже случилось однажды, когда окно номер прятало, а панель через секунду
    /// показывала его крупно.
    /// </remarks>
    public static string Shown(string number) => Masked(number) ?? number;

    /// <summary>Мобильный номер под маской: <c>+7</c> и звёздочки.</summary>
    ///
    /// <remarks>
    /// Страховка на случай, когда номер клиента снова окажется там, где его быть
    /// не должно. Разбор раздачи опирается на форму <c>From</c>, а форму эту
    /// задаёт чужой диалплан — он уже менялся один раз, и менеджеры полдня
    /// видели мобильные лидов. Маска не зависит ни от какого разбора.
    ///
    /// Мобильный узнаётся по коду: одиннадцать цифр, страна 7 или 8, следующая —
    /// 9. Городские (74952…) под правило не попадают: их показывают в CRM
    /// открыто. Звёздочек ровно столько, сколько скрыто цифр, — маска не должна
    /// врать про длину номера.
    /// </remarks>
    public static string? Masked(string number)
    {
        var digits = Digits(number);
        if (digits.Length != 11 || digits[0] is not ('7' or '8') || digits[1] != '9')
        {
            return null;
        }

        return "+7" + new string('*', digits.Length - 1);
    }

    /// <summary>Только цифры и служебные знаки.</summary>
    ///
    /// <remarks>
    /// Номер приходит из SIP по-разному: <c>+7…</c>, <c>8 (918)…</c>, просто
    /// <c>712</c>. Сравнивать их между собой можно только по цифрам.
    /// </remarks>
    private static string Digits(string number)
        => new([.. number.Where(symbol => char.IsDigit(symbol) || symbol is '*' or '#')]);

    /// <summary>Раздача ли это — по форме <c>From</c>.</summary>
    ///
    /// <remarks>
    /// Два условия, и оба обязательны. <b>Имя есть и это не сам номер</b> —
    /// прямой внешний звонок приходит либо без имени вовсе, либо с именем,
    /// повторяющим номер (так его кладёт в <c>P-Asserted-Identity</c> транк), и
    /// принимать такой повтор за название кампании значило бы прятать номер у
    /// всех подряд. <b>Номер внешний</b> — иначе под правило попал бы коллега с
    /// добавочного.
    /// </remarks>
    private static bool IsCampaign(string digits, string? name)
    {
        var title = name?.Trim();
        return !string.IsNullOrEmpty(title)
            && Digits(title) != digits
            && digits.Length >= ExternalNumberLength;
    }
}
