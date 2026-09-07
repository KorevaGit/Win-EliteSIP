using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Склейка поводов пересобрать тракт.
///
/// Одно выдёргивание гарнитуры порождает не одно уведомление, а очередь — и к
/// ней добавляются отказы, которые в тот же момент получают потоки звука. Без
/// склейки оператор услышал бы пять пауз вместо одной.
/// </summary>
public sealed class RestartSupervisorTests
{
    [Fact]
    public void Повод_не_пересобирает_немедленно()
    {
        // Пока устройство в переходе, пересобирать бесполезно: получим тот же
        // отказ и потратим попытку зря.
        ManualScheduler scheduler = new();
        List<string> rebuilds = [];
        using RestartSupervisor supervisor = Supervisor(scheduler, rebuilds);

        supervisor.Notify("гарнитуру выдернули");

        Assert.Empty(rebuilds);
        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal(RestartSupervisor.DefaultWindow, scheduler.LastDelay);

        scheduler.Fire();
        Assert.Equal(["гарнитуру выдернули"], rebuilds);
    }

    [Fact]
    public void Пачка_поводов_даёт_одну_пересборку()
    {
        // Главная проверка файла. У гарнитуры две конечные точки, на каждую
        // приходит смена состояния и исчезновение, плюс смена умолчания, плюс
        // отказы обоих потоков звука.
        ManualScheduler scheduler = new();
        List<string> rebuilds = [];
        using RestartSupervisor supervisor = Supervisor(scheduler, rebuilds);

        supervisor.Notify("устройство исчезло");
        supervisor.Notify("сменилось устройство для связи");
        supervisor.Notify("захват: устройство отказало");
        supervisor.Notify("вывод: устройство отказало");

        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal(3, supervisor.CoalescedCount);

        scheduler.Fire();

        Assert.Single(rebuilds);
        Assert.Equal(1, supervisor.RebuildCount);
    }

    [Fact]
    public void В_журнал_попадает_первый_повод()
    {
        // Первый и есть настоящая причина: остальные — её следствия, и по ним
        // разбирать нечего.
        ManualScheduler scheduler = new();
        List<string> rebuilds = [];
        using RestartSupervisor supervisor = Supervisor(scheduler, rebuilds);

        supervisor.Notify("устройство исчезло");
        supervisor.Notify("захват: устройство отказало");
        scheduler.Fire();

        Assert.Equal(["устройство исчезло"], rebuilds);
    }

    [Fact]
    public void Поток_поводов_не_откладывает_пересборку_бесконечно()
    {
        // Первое уведомление задаёт срок, последующие его не двигают.
        // Bluetooth умеет лить уведомления непрерывно, и «сдвинуть ещё на
        // окно» означало бы, что тракт не соберётся никогда.
        ManualScheduler scheduler = new();
        List<string> rebuilds = [];
        using RestartSupervisor supervisor = Supervisor(scheduler, rebuilds);

        supervisor.Notify("первый");
        for (int i = 0; i < 1000; i++)
        {
            supervisor.Notify($"повод {i}");
        }

        // Срок остался один и тот, что назначил первый повод.
        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal(0, scheduler.CancelledCount);

        scheduler.Fire();
        Assert.Single(rebuilds);
    }

    [Fact]
    public void Следующая_пачка_даёт_следующую_пересборку()
    {
        // Склейка не должна превращаться в глушение: вторая смена устройства за
        // разговор — законное событие.
        ManualScheduler scheduler = new();
        List<string> rebuilds = [];
        using RestartSupervisor supervisor = Supervisor(scheduler, rebuilds);

        supervisor.Notify("первая смена");
        scheduler.Fire();

        supervisor.Notify("вторая смена");
        scheduler.Fire();

        Assert.Equal(["первая смена", "вторая смена"], rebuilds);
        Assert.Equal(2, supervisor.RebuildCount);
    }

    [Fact]
    public void Поводы_во_время_пересборки_не_запускают_вторую_поверх_неё()
    {
        // Пересборка сама порождает поводы: она закрывает и открывает
        // устройства. Вторая, начатая поверх первой, разбирала бы то, что
        // первая собирает.
        ManualScheduler scheduler = new();
        int depth = 0;
        int maximumDepth = 0;
        RestartSupervisor? supervisor = null;

        supervisor = new RestartSupervisor(
            _ =>
            {
                depth++;
                maximumDepth = Math.Max(maximumDepth, depth);

                // Ровно то, что делает настоящая пересборка: закрытие и
                // открытие устройств порождает уведомления.
                supervisor!.Notify("устройство исчезло");
                supervisor.Notify("устройство вернулось");
                scheduler.Fire();

                depth--;
            },
            scheduler.Schedule,
            RestartSupervisor.DefaultWindow);

        using (supervisor)
        {
            supervisor.Notify("гарнитуру выдернули");
            scheduler.Fire();
        }

        Assert.Equal(1, maximumDepth);
    }

    [Fact]
    public void Закрытие_отменяет_назначенную_пересборку()
    {
        // Тракт остановили, пока повод ждал своего часа. Собирать его обратно
        // значит открыть устройство, которое уже никому не нужно.
        ManualScheduler scheduler = new();
        List<string> rebuilds = [];
        RestartSupervisor supervisor = Supervisor(scheduler, rebuilds);

        supervisor.Notify("устройство исчезло");
        supervisor.Dispose();

        Assert.Equal(1, scheduler.CancelledCount);

        // Отмена не останавливает то, что уже началось, — проверка внутри
        // обязана поймать и этот случай.
        scheduler.FireEvenIfCancelled();
        Assert.Empty(rebuilds);
    }

    [Fact]
    public void После_закрытия_поводы_не_принимаются()
    {
        ManualScheduler scheduler = new();
        List<string> rebuilds = [];
        RestartSupervisor supervisor = Supervisor(scheduler, rebuilds);
        supervisor.Dispose();

        supervisor.Notify("поздно");
        scheduler.Fire();

        Assert.Empty(rebuilds);
        Assert.Equal(0, supervisor.RebuildCount);
    }

    [Fact]
    public void Повторное_закрытие_проходит_вхолостую()
    {
        ManualScheduler scheduler = new();
        RestartSupervisor supervisor = Supervisor(scheduler, []);
        supervisor.Dispose();
        supervisor.Dispose();
    }

    private static RestartSupervisor Supervisor(ManualScheduler scheduler, List<string> rebuilds) =>
        new(rebuilds.Add, scheduler.Schedule, RestartSupervisor.DefaultWindow);
}
