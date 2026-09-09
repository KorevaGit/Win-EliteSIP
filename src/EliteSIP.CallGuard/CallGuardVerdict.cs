using System.Globalization;
using EliteSIP.CallGuard.Resources;

namespace EliteSIP.CallGuard;

/// <summary>
/// Попытка принять вызов.
/// </summary>
///
/// <remarks>
/// Источник у неё всегда один — мышь. Клавиатурного приёма нет намеренно: он не
/// оставлял защите ни одного признака живого человека, а «Отклонить» с
/// клавиатуры по-прежнему работает, то есть отказаться от вызова можно и без
/// мыши.
/// </remarks>
///
/// <param name="Target">По какой цели нажали.</param>
///
/// <param name="IsSynthetic">
/// Признак программного происхождения события. В Windows берётся из
/// <c>LLMHF_INJECTED</c> низкоуровневого хука мыши — клик от <c>SendInput</c>
/// виден явно. Драйверный кликер его не ставит, поэтому сам по себе признак
/// ничего не доказывает; ценность у него не как у барьера, а как у показателя:
/// у честного оператора он не встречается никогда.
/// </param>
///
/// <param name="At">
/// Когда нажали, по тем же часам, что и показ окна. Время приходит снаружи,
/// чтобы «нажал через 210 мс без движения курсора» проверялось тестом, а не
/// секундомером и мышью.
/// </param>
public readonly record struct CallGuardAttempt(char Target, bool IsSynthetic, TimeSpan At);

/// <summary>Почему попытка не принята.</summary>
public enum CallGuardRejection
{
    /// <summary>Нажали не ту цель.</summary>
    WrongTarget,

    /// <summary>Курсор не двигался: приехал в точку и нажал.</summary>
    NoCursorMovement,

    /// <summary>Событие с признаком программного происхождения.</summary>
    Synthetic,
}

public static class CallGuardRejections
{
    /// <summary>Что об этом сказать оператору.</summary>
    ///
    /// <remarks>
    /// Формулировки намеренно не объясняют, чего именно не хватило: подсказка
    /// «пройдите курсором 40 точек» — это готовая инструкция для того, кто
    /// подбирает обход.
    /// </remarks>
    public static string OperatorMessage(this CallGuardRejection reason) => reason switch
    {
        CallGuardRejection.WrongTarget => PackageStrings.Get("RefusalWrongTarget"),
        CallGuardRejection.NoCursorMovement => PackageStrings.Get("RefusalNoCursorMovement"),
        _ => PackageStrings.Get("RefusalSynthetic"),
    };

    /// <summary>Что об этом написать в журнал.</summary>
    // не переводится: строка журнала — её сравнивают между машинами.
    public static string LogMessage(this CallGuardRejection reason) => reason switch
    {
        CallGuardRejection.WrongTarget => "нажата не та цель",
        CallGuardRejection.NoCursorMovement => "нажатие без движения курсора",
        _ => "нажатие с признаком синтетического события",
    };

    /// <summary>Стоит ли считать это признаком автоматизации, а не промахом человека.</summary>
    ///
    /// <remarks>
    /// Не та кнопка — обычная человеческая ошибка. Остальные две без участия
    /// программы не получаются.
    /// </remarks>
    public static bool SuggestsAutomation(this CallGuardRejection reason)
        => reason is not CallGuardRejection.WrongTarget;
}

/// <summary>Решение по одной попытке: принято или отклонено и почему.</summary>
public readonly record struct CallGuardVerdict
{
    private CallGuardVerdict(CallGuardRejection? rejection) => Rejection = rejection;

    public static CallGuardVerdict Accepted { get; } = new(null);

    public static CallGuardVerdict Rejected(CallGuardRejection reason) => new(reason);

    /// <summary>Причина отказа; <c>null</c> — вызов принят.</summary>
    public CallGuardRejection? Rejection { get; }

    public bool IsAccepted => Rejection is null;
}

/// <summary>
/// Что защита увидела за один входящий вызов.
/// </summary>
///
/// <remarks>
/// Это и есть слой 3 из документа в зачаточном виде: на клиенте по одному
/// звонку решить ничего нельзя, а на нескольких сотнях время реакции 210 ± 5 мс
/// при нулевом пути курсора видно сразу. На W10 уезжает в панель.
/// </remarks>
public sealed record CallGuardReport
{
    public bool WasGuardEnabled { get; init; } = true;

    /// <summary>Сколько прошло от появления окна до принятого нажатия, мс.</summary>
    ///
    /// <remarks>
    /// Заполнено — значит вызов приняли: другого способа сюда попасть нет. Это
    /// же и главное число слоя обнаружения: локальной задержки активации больше
    /// нет, и ровное время реакции ловится только статистикой.
    /// </remarks>
    public int? ReactionMilliseconds { get; init; }

    /// <summary>Длина пути курсора внутри окна, в точках.</summary>
    public double CursorTravel { get; init; }

    /// <summary>Сколько отдельных перемещений курсора зафиксировано.</summary>
    public int CursorSamples { get; init; }

    /// <summary>Отклонённые попытки по причинам.</summary>
    public IReadOnlyDictionary<CallGuardRejection, int> Rejections { get; init; }
        = new Dictionary<CallGuardRejection, int>();

    /// <summary>Сколько раз защита сработала.</summary>
    public int RejectedAttempts => Rejections.Values.Sum();

    /// <summary>Есть ли в этом звонке хоть что-то, похожее на автоматизацию.</summary>
    public bool LooksAutomated
        => Rejections.Any(pair => pair.Key.SuggestsAutomation() && pair.Value > 0);

    /// <summary>
    /// Строка для журнала. Короткая: в панели диагностики места мало, а
    /// подробности всё равно уедут в панель целиком.
    /// </summary>
    // не переводится: журнал и отчёт диагностики.
    public string Summary()
    {
        if (!WasGuardEnabled)
        {
            return "защита выключена";
        }

        var parts = new List<string>(3);
        if (ReactionMilliseconds is int reaction)
        {
            parts.Add($"реакция {reaction} мс");
        }

        parts.Add(string.Format(
            CultureInfo.InvariantCulture,
            "курсор {0:F0} pt за {1} движ.",
            CursorTravel,
            CursorSamples));

        if (RejectedAttempts > 0)
        {
            var detail = string.Join(
                ", ",
                Rejections
                    .Where(pair => pair.Value > 0)
                    .OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key.LogMessage()} ×{pair.Value}"));

            parts.Add($"отклонено {RejectedAttempts} ({detail})");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Равенство по содержимому: словарь сам по себе сравнивается ссылкой.</summary>
    public bool Equals(CallGuardReport? other)
        => other is not null
            && WasGuardEnabled == other.WasGuardEnabled
            && ReactionMilliseconds == other.ReactionMilliseconds
            && CursorTravel.Equals(other.CursorTravel)
            && CursorSamples == other.CursorSamples
            && Rejections.Count == other.Rejections.Count
            && Rejections.All(pair =>
                other.Rejections.TryGetValue(pair.Key, out var count) && count == pair.Value);

    public override int GetHashCode()
        => HashCode.Combine(WasGuardEnabled, ReactionMilliseconds, CursorTravel, CursorSamples, Rejections.Count);
}
