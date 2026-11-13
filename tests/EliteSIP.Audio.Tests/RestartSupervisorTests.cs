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
    public async Task Zero_window_timer_can_fire_safely()
    {
        var rebuilt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var supervisor = new RestartSupervisor(_ => rebuilt.TrySetResult(), window: TimeSpan.Zero);
        supervisor.Notify("immediate");
        await rebuilt.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Independent_predicates_without_key_do_not_replace_each_other()
    {
        ManualScheduler scheduler = new();
        List<string> rebuilds = [];
        using var supervisor = Supervisor(scheduler, rebuilds);
        supervisor.Notify("already handled", () => false);
        supervisor.Notify("new change", () => true);
        scheduler.Fire();
        Assert.Equal(["new change"], rebuilds);
    }

    [Fact]
    public async Task Independent_notifications_during_rebuild_are_retained_for_one_followup()
    {
        ManualScheduler scheduler = new();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var count = 0;
        using var supervisor = new RestartSupervisor(_ =>
        {
            if (Interlocked.Increment(ref count) == 1) { entered.Set(); release.Wait(); }
        }, scheduler.Schedule);
        supervisor.Notify("first");
        var rebuilding = Task.Run(scheduler.Fire);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            for (var index = 0; index < 1000; index++) { supervisor.Notify("new independent change"); }
            Assert.Equal(0, scheduler.PendingCount);
            Assert.Equal(1, count);
        }
        finally { release.Set(); }
        await rebuilding.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.Fire();
        Assert.Equal(2, count);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Already_applied_default_does_not_start_another_rebuild()
    {
        ManualScheduler scheduler = new();
        var binding = new AudioRouteBinding(null, null, "mic", "old-output");
        RestartSupervisor? supervisor = null;
        var count = 0;
        supervisor = new RestartSupervisor(_ =>
        {
            count++;
            var observed = binding;
            var change = new AudioDeviceChange(AudioDeviceChangeKind.DefaultChanged, "new-output", AudioDeviceDirection.Render, null);
            supervisor!.Notify("default changed while opening", () => DeviceChangeRelevance.StillRequiresRebuild(change, observed, binding), "default:render");
            binding = new AudioRouteBinding(null, null, "mic", "new-output");
            scheduler.Fire(); // Никаких вложенных таймеров во время сборки.
        }, scheduler.Schedule);
        using (supervisor)
        {
            supervisor.Notify("initial change");
            scheduler.Fire();
            Assert.Equal(1, scheduler.PendingCount);
            scheduler.Fire();
            Assert.Equal(1, count);
            Assert.Equal(0, scheduler.PendingCount);
        }
    }

    [Fact]
    public void Latest_default_replaces_intermediate_selection()
    {
        ManualScheduler scheduler = new();
        List<string> rebuilds = [];
        using var supervisor = Supervisor(scheduler, rebuilds);
        var binding = new AudioRouteBinding(null, null, "mic", "output-A");
        var first = new AudioDeviceChange(AudioDeviceChangeKind.DefaultChanged, "output-B", AudioDeviceDirection.Render, null);
        var last = first with { DeviceId = "output-A" };
        supervisor.Notify("A to B", () => DeviceChangeRelevance.StillRequiresRebuild(first, binding, binding), "default:render");
        supervisor.Notify("B to A", () => DeviceChangeRelevance.StillRequiresRebuild(last, binding, binding), "default:render");
        scheduler.Fire();
        Assert.Empty(rebuilds);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Dispose_during_rebuild_discards_followup()
    {
        ManualScheduler scheduler = new();
        RestartSupervisor? supervisor = null;
        var count = 0;
        supervisor = new RestartSupervisor(_ =>
        {
            count++;
            supervisor!.Notify("independent change");
            supervisor.Dispose();
        }, scheduler.Schedule);
        using (supervisor)
        {
            supervisor.Notify("first");
            scheduler.Fire();
            scheduler.FireEvenIfCancelled();
            Assert.Equal(1, count);
            Assert.Equal(0, scheduler.PendingCount);
        }
    }

    [Fact]
    public void Old_timer_callback_cannot_consume_a_new_batch()
    {
        List<Action> callbacks = [];
        var count = 0;
        using var supervisor = new RestartSupervisor(_ => count++, (_, action) =>
        {
            callbacks.Add(action);
            return new CancellationTokenSource();
        });
        supervisor.Notify("first");
        callbacks[0]();
        supervisor.Notify("second");
        callbacks[0](); // Поздний повтор старого callback.
        Assert.Equal(1, count);
        callbacks[1]();
        Assert.Equal(2, count);
    }

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
