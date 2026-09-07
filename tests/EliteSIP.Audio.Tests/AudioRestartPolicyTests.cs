using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Политика пересборки тракта.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/AudioRestartPolicyTests.swift</c>.
/// Секунды заменены на <see cref="TimeSpan"/>, в остальном проверки те же:
/// они и есть формулировка решения, ради которого тип отделён от движка.
/// </summary>
public sealed class AudioRestartPolicyTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Первая_неудача_не_приговор()
    {
        // Это и есть суть правки: раньше единственная неудачная пересборка
        // означала конец разговора, и обычный переход Bluetooth ронял звонок,
        // который через секунду починился бы сам.
        AudioRestartPolicy policy = new();
        var decision = policy.RecordFailure(TimeSpan.Zero);

        Assert.Equal(RestartAction.Retry, decision.Action);
        Assert.Equal(S(0.3), decision.Delay);
        Assert.Equal(1, decision.Attempt);
        Assert.True(policy.IsRecovering);
    }

    [Fact]
    public void Отсрочка_растёт_но_упирается_в_потолок()
    {
        // Растёт затем, чтобы не жечь попытки, пока устройство ещё в переходе.
        // Упирается затем, чтобы после возвращения устройства не ждать лишнего.
        AudioRestartPolicy policy = new(S(0.3), S(2), S(100));
        List<TimeSpan> delays = [];
        var now = TimeSpan.Zero;

        for (int i = 0; i < 6; i++)
        {
            var decision = policy.RecordFailure(now);
            Assert.Equal(RestartAction.Retry, decision.Action);
            delays.Add(decision.Delay);
            now += decision.Delay;
        }

        Assert.Equal([S(0.3), S(0.6), S(1.2), S(2), S(2), S(2)], delays);
    }

    [Fact]
    public void Номер_попытки_растёт_оператору_есть_что_показать()
    {
        AudioRestartPolicy policy = new();
        List<int> attempts = [];
        var now = TimeSpan.Zero;

        while (true)
        {
            var decision = policy.RecordFailure(now);
            if (decision.Action != RestartAction.Retry)
            {
                break;
            }

            attempts.Add(decision.Attempt);
            now += decision.Delay;
        }

        // Нумерация сплошная и с единицы: «попытка 3» в журнале должна значить
        // ровно третью, иначе по журналу нельзя понять, сколько всего было.
        Assert.Equal(Enumerable.Range(1, attempts.Count), attempts);
    }

    [Fact]
    public void Запас_терпения_кончается_и_тогда_разговор_пора_закрывать()
    {
        // Отступиться тоже надо: если устройства действительно больше нет,
        // молчащий разговор хуже завершённого.
        AudioRestartPolicy policy = new(S(0.3), S(2), S(10));
        var now = TimeSpan.Zero;
        var decision = policy.RecordFailure(now);
        int rounds = 0;

        while (decision.Action == RestartAction.Retry && rounds < 100)
        {
            now += decision.Delay;
            decision = policy.RecordFailure(now);
            rounds++;
        }

        Assert.True(rounds < 100, "политика не сходится — попытки не кончаются");
        Assert.Equal(RestartAction.GiveUp, decision.Action);
        Assert.True(decision.Attempt > 1, "сдаваться с первой попытки — прежнее поведение");
        Assert.True(decision.Elapsed <= S(10));
    }

    [Fact]
    public void Последняя_попытка_не_назначается_за_пределы_запаса()
    {
        // Проверка порядка: если сначала выдать отсрочку, а запас сверить
        // потом, оператор досидит до конца запаса и ещё две секунды сверх — уже
        // зная, что всё равно отказ.
        AudioRestartPolicy policy = new(S(1), S(1), S(3));
        var now = TimeSpan.Zero;
        List<TimeSpan> scheduled = [];

        while (true)
        {
            var decision = policy.RecordFailure(now);
            if (decision.Action != RestartAction.Retry)
            {
                break;
            }

            now += decision.Delay;
            scheduled.Add(now);
        }

        Assert.All(scheduled, at => Assert.True(at <= S(3), $"попытка назначена за пределом запаса: {at}"));
    }

    [Fact]
    public void Успех_обнуляет_серию()
    {
        // Иначе вторая смена устройства за разговор начиналась бы с
        // исчерпанным запасом, и вторая пара наушников роняла бы звонок сразу.
        AudioRestartPolicy policy = new();
        policy.RecordFailure(TimeSpan.Zero);
        policy.RecordFailure(S(0.3));
        policy.RecordSuccess();

        Assert.False(policy.IsRecovering);
        Assert.Equal(0, policy.AttemptCount);

        var decision = policy.RecordFailure(S(60));
        Assert.Equal(RestartAction.Retry, decision.Action);
        Assert.Equal(S(0.3), decision.Delay);
        Assert.Equal(1, decision.Attempt);
    }

    [Fact]
    public void Запас_считается_от_первой_неудачи_а_не_от_каждой()
    {
        AudioRestartPolicy policy = new(S(0.3), S(0.3), S(5));
        policy.RecordFailure(S(100));

        // Прошло больше запаса — дальше тянуть нечего, сколько бы попыток ни
        // осталось.
        var decision = policy.RecordFailure(S(106));
        Assert.Equal(RestartAction.GiveUp, decision.Action);
        Assert.Equal(S(6), decision.Elapsed);
    }

    [Fact]
    public void Часы_идущие_назад_не_ломают_счёт()
    {
        // Часы монотонные, но защита стоит: отрицательное «прошло» превратило
        // бы запас в бесконечный, и разговор навсегда остался бы в состоянии
        // «восстанавливаю звук».
        AudioRestartPolicy policy = new(S(0.3), S(0.3), S(1));
        policy.RecordFailure(S(100));

        var decision = policy.RecordFailure(S(90));
        Assert.Equal(RestartAction.Retry, decision.Action);
        Assert.Equal(S(0.3), decision.Delay);
        Assert.Equal(2, decision.Attempt);
    }

    [Theory]
    [InlineData(0, 2, 10)]
    [InlineData(2, 1, 10)]
    [InlineData(0.3, 2, 0)]
    public void Бессмысленные_настройки_отвергаются_на_месте(double first, double max, double budget)
    {
        // Нулевая отсрочка — цикл на отказе, потолок ниже первой отсрочки —
        // отсрочка, которая сразу уменьшается, нулевой запас — прежнее
        // поведение под видом политики. Каждый случай хочется поймать при
        // создании, а не по молчащему разговору.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AudioRestartPolicy(S(first), S(max), S(budget)));
    }
}
