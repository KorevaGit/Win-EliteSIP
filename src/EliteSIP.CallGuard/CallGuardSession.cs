namespace EliteSIP.CallGuard;

/// <summary>
/// Защита одного входящего вызова: от появления окна до решения оператора.
/// </summary>
///
/// <remarks>
/// Время не берётся изнутри, а приходит с каждым событием. Из-за этого весь
/// разбор — чистая функция от последовательности событий, и «нажал через 210 мс
/// без движения курсора» проверяется тестом, а не секундомером и мышью.
/// </remarks>
public sealed class CallGuardSession
{
    private readonly Dictionary<CallGuardRejection, int> _rejections = [];

    /// <summary>
    /// Последняя известная позиция курсора. Первое событие пути не даёт: одна
    /// точка — это ещё не движение.
    /// </summary>
    private ScreenPoint? _lastCursorPoint;

    private double _cursorTravel;
    private int _cursorSamples;
    private int? _reactionMilliseconds;

    public CallGuardSession(CallGuardPolicy policy, TimeSpan presentedAt, Random generator)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(generator);

        Policy = policy.Normalized();
        PresentedAt = presentedAt;
        Challenge = Policy.IsEnabled
            ? CallGuardChallenge.Build(Policy, generator)
            : CallGuardChallenge.Unguarded;
    }

    public CallGuardPolicy Policy { get; }

    public CallGuardChallenge Challenge { get; }

    /// <summary>Когда появилось окно. От этого момента считается всё остальное.</summary>
    public TimeSpan PresentedAt { get; }

    /// <summary>Отчёт на текущий момент. Снимок: дальнейшие события его не меняют.</summary>
    public CallGuardReport Report => new()
    {
        WasGuardEnabled = Policy.IsEnabled,
        ReactionMilliseconds = _reactionMilliseconds,
        CursorTravel = _cursorTravel,
        CursorSamples = _cursorSamples,
        Rejections = new Dictionary<CallGuardRejection, int>(_rejections),
    };

    /// <summary>Достаточно ли курсор двигался.</summary>
    public bool HasEnoughCursorMovement
        => !Policy.IsEnabled
            || !Policy.RequiresCursorMovement
            || (_cursorTravel >= Policy.RequiredCursorTravel && _cursorSamples >= Policy.RequiredCursorSamples);

    /// <summary>Отмечает перемещение курсора внутри окна.</summary>
    ///
    /// <remarks>
    /// Считается именно путь, а не факт наличия координаты: <c>SendInput</c>
    /// ставит курсор в точку одним событием, и путь у такого «движения» равен
    /// нулю. Честная рука за то же время проходит десятки точек.
    /// </remarks>
    public void NoteCursor(ScreenPoint point)
    {
        var previous = _lastCursorPoint;
        _lastCursorPoint = point;

        if (previous is not ScreenPoint from)
        {
            return;
        }

        var step = point.DistanceTo(from);
        if (step <= 0)
        {
            return;
        }

        _cursorTravel += step;
        _cursorSamples++;
    }

    /// <summary>Разбирает попытку принять вызов.</summary>
    ///
    /// <remarks>
    /// Порядок проверок не случаен и идёт от самого дешёвого обхода к самому
    /// дорогому: сначала поиск по шаблону изображения, потом отсутствие живой
    /// руки. Так в телеметрии видно, на каком именно слое остановился нарушитель.
    ///
    /// Проверки «нажали слишком рано» здесь больше нет: кнопка активна с первого
    /// кадра, а ровное время реакции — работа статистики в панели, для которой в
    /// отчёте лежит <see cref="CallGuardReport.ReactionMilliseconds"/>. Локальная
    /// задержка стоила оператору внимания на каждом вызове, а кликеру — одной
    /// строки ожидания.
    /// </remarks>
    public CallGuardVerdict Evaluate(CallGuardAttempt attempt)
    {
        if (!Policy.IsEnabled)
        {
            Accept(attempt);
            return CallGuardVerdict.Accepted;
        }

        if (Challenge.HasChoice && attempt.Target != Challenge.Answer)
        {
            return Reject(CallGuardRejection.WrongTarget);
        }

        if (Policy.RejectsSyntheticEvents && attempt.IsSynthetic)
        {
            return Reject(CallGuardRejection.Synthetic);
        }

        // Единственный путь приёма — мышь, поэтому движение курсора требуется
        // всегда. Клавиатурного пути нет намеренно: он не оставлял защите ни
        // одного признака живого человека — ни пути курсора, ни его отсутствия.
        if (!HasEnoughCursorMovement)
        {
            return Reject(CallGuardRejection.NoCursorMovement);
        }

        // Синтетическое нажатие, которое мы решили не отклонять, всё равно
        // должно оказаться в отчёте: иначе слой обнаружения из документа
        // останется без данных, ради которых он и задуман.
        if (attempt.IsSynthetic)
        {
            Count(CallGuardRejection.Synthetic);
        }

        Accept(attempt);
        return CallGuardVerdict.Accepted;
    }

    private void Accept(CallGuardAttempt attempt)
        => _reactionMilliseconds = (int)(attempt.At - PresentedAt).TotalMilliseconds;

    private CallGuardVerdict Reject(CallGuardRejection reason)
    {
        Count(reason);
        return CallGuardVerdict.Rejected(reason);
    }

    private void Count(CallGuardRejection reason)
        => _rejections[reason] = _rejections.GetValueOrDefault(reason) + 1;
}
