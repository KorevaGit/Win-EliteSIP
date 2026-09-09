namespace EliteSIP.CallGuard;

/// <summary>
/// Что окно должно показать, чтобы приём вызова требовал человека.
/// </summary>
///
/// <remarks>
/// Собирается один раз на звонок и больше не меняется: перерисовка целей под
/// уже потянувшейся рукой оператора — это не защита, а издевательство.
/// </remarks>
public sealed record CallGuardChallenge
{
    /// <summary>
    /// Больше девяти целей не бывает: ряд из десятка одинаковых кнопок
    /// перестаёт читаться, а ноль среди цифр виден хуже остальных.
    /// </summary>
    public const int MaximumTargets = 9;

    /// <summary>Цифры на кнопках, слева направо.</summary>
    public required IReadOnlyList<char> Targets { get; init; }

    /// <summary>Та единственная, которая действительно принимает вызов.</summary>
    public required char Answer { get; init; }

    /// <summary>
    /// Задание без задания: одна цель. Так выглядит окно с выключенной защитой.
    /// </summary>
    public static CallGuardChallenge Unguarded { get; } = new() { Targets = ['1'], Answer = '1' };

    /// <summary>
    /// Есть ли из чего выбирать. Одна цель — это не выбор, и говорить оператору
    /// «нажмите 4» в таком случае незачем.
    /// </summary>
    public bool HasChoice => Targets.Count > 1;

    /// <summary>Собирает задание по политике.</summary>
    ///
    /// <remarks>
    /// Генератор снаружи — иначе поведение защиты нельзя проверить тестом, а
    /// непроверяемая защита ничем не отличается от её отсутствия.
    /// </remarks>
    public static CallGuardChallenge Build(CallGuardPolicy policy, Random generator)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(generator);

        policy = policy.Normalized();

        // Цифры выбираются без повторов: две одинаковые цели сделали бы задание
        // неразрешимым, а виноватым выглядел бы оператор.
        var pool = new List<char>("123456789");
        var chosen = new List<char>(policy.TargetCount);
        for (var index = 0; index < policy.TargetCount && pool.Count > 0; index++)
        {
            var pick = generator.Next(pool.Count);
            chosen.Add(pool[pick]);
            pool.RemoveAt(pick);
        }

        return new CallGuardChallenge
        {
            Targets = chosen,
            Answer = chosen[generator.Next(chosen.Count)],
        };
    }

    /// <summary>Равенство по содержимому: список сам по себе сравнивается ссылкой.</summary>
    public bool Equals(CallGuardChallenge? other)
        => other is not null && Answer == other.Answer && Targets.SequenceEqual(other.Targets);

    public override int GetHashCode()
    {
        var code = new HashCode();
        code.Add(Answer);
        foreach (var target in Targets)
        {
            code.Add(target);
        }

        return code.ToHashCode();
    }
}
