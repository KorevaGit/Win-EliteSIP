namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Наблюдение за входящим потоком.
///
/// Перенесено из
/// <c>Packages/MediaCore/Tests/MediaCoreTests/InboundStreamWatchTests.swift</c>.
/// </summary>
public sealed class InboundStreamWatchTests
{
    [Fact]
    public void Пока_пакеты_идут_сообщать_не_о_чем()
    {
        InboundStreamWatch watch = new();
        int received = 0;
        for (double tick = 0; tick <= 10; tick += 0.05)
        {
            received++;
            Assert.Equal(InboundStreamKind.Flowing, watch.Update(received, tick).Kind);
        }
    }

    [Fact]
    public void Разбег_в_начале_разговора_не_считается_поломкой()
    {
        // RTP начинается сразу за подтверждением, но доли секунды разбега
        // законны. Ругаться на них — приучить оператора не читать сообщения.
        InboundStreamWatch watch = new(3, 2);
        Assert.Equal(new InboundStreamState(InboundStreamKind.Flowing), watch.Update(0, 0));
        Assert.Equal(new InboundStreamState(InboundStreamKind.Flowing), watch.Update(0, 2.9));
        Assert.Equal(new InboundStreamState(InboundStreamKind.Flowing), watch.Update(1, 2.95));
    }

    [Fact]
    public void Поток_который_так_и_не_начался_замечается()
    {
        // Ровно случай стенда 3 августа: 29 секунд разговора, принято ноль
        // пакетов, и приложение об этом молчало.
        InboundStreamWatch watch = new(3, 2);
        Assert.Equal(new InboundStreamState(InboundStreamKind.Flowing), watch.Update(0, 0));
        Assert.Equal(new InboundStreamState(InboundStreamKind.NeverStarted, 3), watch.Update(0, 3));
        Assert.Equal(new InboundStreamState(InboundStreamKind.NeverStarted, 29), watch.Update(0, 29));
    }

    [Fact]
    public void Первый_же_пакет_снимает_тревогу()
    {
        // Отсчёт идёт от первого замера, а не от нуля шкалы: наблюдение
        // заводится вместе с медиа, и «сколько ждём» считается от него.
        InboundStreamWatch watch = new(3, 2);
        Assert.Equal(new InboundStreamState(InboundStreamKind.Flowing), watch.Update(0, 100));
        Assert.Equal(new InboundStreamState(InboundStreamKind.NeverStarted, 5), watch.Update(0, 105));
        Assert.Equal(new InboundStreamState(InboundStreamKind.Flowing), watch.Update(1, 106));
    }

    [Fact]
    public void Оборвавшийся_поток_замечается()
    {
        InboundStreamWatch watch = new(3, 2);
        watch.Update(10, 0);

        Assert.Equal(InboundStreamKind.Flowing, watch.Update(10, 1.9).Kind);
        Assert.Equal(new InboundStreamState(InboundStreamKind.Stalled, 2), watch.Update(10, 2));
        Assert.Equal(new InboundStreamState(InboundStreamKind.Stalled, 8), watch.Update(10, 8));
    }

    [Fact]
    public void Вернувшийся_поток_снимает_тревогу()
    {
        InboundStreamWatch watch = new(3, 2);
        watch.Update(10, 0);
        Assert.Equal(new InboundStreamState(InboundStreamKind.Stalled, 3), watch.Update(10, 3));
        Assert.Equal(InboundStreamKind.Flowing, watch.Update(11, 4).Kind);

        // И отсчёт следующего перерыва идёт от возврата, а не от старого замера.
        Assert.Equal(InboundStreamKind.Flowing, watch.Update(11, 5).Kind);
        Assert.Equal(new InboundStreamState(InboundStreamKind.Stalled, 2), watch.Update(11, 6));
    }

    [Fact]
    public void Короткая_потеря_не_поднимает_тревогу()
    {
        // Одиночные потери — дело джиттер-буфера, он их скрывает. Здесь ловится
        // только грубое, иначе сообщение обесценится.
        InboundStreamWatch watch = new(3, 2);
        watch.Update(100, 0);

        // Сто миллисекунд тишины — пять потерянных кадров подряд, предел
        // сокрытия. Тревоги быть не должно.
        Assert.Equal(InboundStreamKind.Flowing, watch.Update(100, 0.1).Kind);
    }

    [Fact]
    public void Сброс_возвращает_наблюдение_в_исходное()
    {
        InboundStreamWatch watch = new(3, 2);
        watch.Update(5, 0);
        Assert.Equal(new InboundStreamState(InboundStreamKind.Stalled, 4), watch.Update(5, 4));

        watch.Reset();

        // После сброса счётчик пакетов новой сессии начинается с нуля, и старое
        // значение не должно выглядеть как «поток уже шёл».
        Assert.Equal(InboundStreamKind.Flowing, watch.Update(0, 10).Kind);
        Assert.Equal(new InboundStreamState(InboundStreamKind.NeverStarted, 3), watch.Update(0, 13));
    }
}

/// <summary>
/// Состояние без подробностей.
///
/// Живой прогон 18 августа 2026: в архиве для поддержки 62 строки из 266
/// оказались одним и тем же предупреждением, повторённым двадцать раз в
/// секунду. Причина была в самом состоянии — счётчик секунд внутри него растёт
/// на каждом опросе, и сравнение «состояние то же?» отвечало «нет» всегда, хотя
/// сказать надо было один раз на переходе.
/// </summary>
public sealed class InboundStreamKindTests
{
    [Fact]
    public void Растущие_секунды_не_меняют_состояния()
    {
        InboundStreamWatch watch = new(1, 1);

        // Первый замер задаёт начало отсчёта: фора считается от него, а не от
        // нуля шкалы.
        watch.Update(0, 0);

        InboundStreamState first = watch.Update(0, 1.5);
        InboundStreamState later = watch.Update(0, 9.0);

        Assert.NotEqual(first, later);
        Assert.Equal(first.Kind, later.Kind);
        Assert.Equal(InboundStreamKind.NeverStarted, first.Kind);
    }

    [Fact]
    public void Переход_между_состояниями_виден_по_тому_же_признаку()
    {
        InboundStreamWatch watch = new(1, 1);

        Assert.Equal(InboundStreamKind.Flowing, watch.Update(0, 0.1).Kind);
        Assert.Equal(InboundStreamKind.NeverStarted, watch.Update(0, 2.0).Kind);
        Assert.Equal(InboundStreamKind.Flowing, watch.Update(1, 2.5).Kind);
        Assert.Equal(InboundStreamKind.Stalled, watch.Update(1, 4.0).Kind);
        Assert.Equal(InboundStreamKind.Flowing, watch.Update(2, 4.5).Kind);
    }
}
