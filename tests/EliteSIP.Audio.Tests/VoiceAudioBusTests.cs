using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Общий тракт, который линии берут по очереди.
///
/// <b>Этого набора в оригинале нет.</b> Там шина зависела от конкретного
/// движка, а тот без звуковой карты не заводится, и проверить её было нечем —
/// хотя собственный комментарий шины называет ошибку, ради которой она
/// написана, «первой же, которую сделает любой новый путь». Здесь тракт за
/// интерфейсом, и весь порядок операций закрыт без единого устройства.
/// </summary>
public sealed class VoiceAudioBusTests
{
    [Fact]
    public void Захват_запускает_тракт_и_ставит_владельца()
    {
        var (bus, engine, _) = Bus();
        var line = AudioOwnerToken.New();

        bus.Claim(line, new VoiceAudioConfiguration(), new VoiceAudioHandlers());

        Assert.True(bus.IsBusy);
        Assert.True(bus.IsOwner(line));
        Assert.Equal(1, engine.StartCount);
    }

    [Fact]
    public void Захват_останавливает_до_перестройки_а_не_после()
    {
        // Порядок обязателен: два запущенных тракта на одном устройстве делят
        // его между собой, а Bluetooth-гарнитуру держат в режиме связи всё
        // время, пока жив хоть один.
        var (bus, engine, _) = Bus();

        bus.Claim(AudioOwnerToken.New(), new VoiceAudioConfiguration(), new VoiceAudioHandlers());

        Assert.Equal(["stop", "reconfigure", "start"], engine.Log);
    }

    [Fact]
    public void Неудачный_запуск_не_оставляет_владельца()
    {
        // Иначе линия считала бы своим звук, которого нет, и не отдала бы его
        // следующей: тракт остался бы занят навсегда после одной осечки.
        var (bus, engine, _) = Bus();
        engine.StartFailure = new InvalidOperationException("устройство занято монопольно");
        var line = AudioOwnerToken.New();

        Assert.Throws<InvalidOperationException>(
            () => bus.Claim(line, new VoiceAudioConfiguration(), Handlers()));

        Assert.False(bus.IsBusy);
        Assert.False(bus.IsOwner(line));

        // И обработчики сняты: оставить их значило бы кормить кадрами линию,
        // которая тракта не получила.
        Assert.Same(VoiceAudioHandlers.None, engine.Handlers);
    }

    [Fact]
    public void После_неудачи_тракт_достаётся_следующему()
    {
        var (bus, engine, _) = Bus();
        engine.StartFailure = new InvalidOperationException("устройство занято");

        Assert.Throws<InvalidOperationException>(
            () => bus.Claim(AudioOwnerToken.New(), new VoiceAudioConfiguration(), Handlers()));

        engine.StartFailure = null;
        var second = AudioOwnerToken.New();
        bus.Claim(second, new VoiceAudioConfiguration(), Handlers());

        Assert.True(bus.IsOwner(second));
    }

    [Fact]
    public void Снятая_линия_не_глушит_разговор_на_живой()
    {
        // Ровно та ошибка, ради которой написана шина: запоздавший путь снятой
        // линии приходит отпускать тракт через секунду после того, как его
        // забрала другая.
        var (bus, engine, scheduler) = Bus();
        var removed = AudioOwnerToken.New();
        var live = AudioOwnerToken.New();

        bus.Claim(removed, new VoiceAudioConfiguration(), Handlers());
        bus.Claim(live, new VoiceAudioConfiguration(), Handlers());

        Assert.False(bus.Release(removed));
        Assert.True(bus.IsOwner(live));
        Assert.True(bus.IsBusy);

        // И освобождение устройства чужим ключом не назначается.
        Assert.Equal(0, scheduler.PendingCount);
        Assert.Equal(2, engine.StartCount);
    }

    [Fact]
    public void Переключение_линий_отбирает_тракт_без_освобождения_устройства()
    {
        // Между двумя линиями одного оператора устройство не пересобирается:
        // пауза в начале разговора здесь стоила бы дороже, чем что-либо ещё.
        var (bus, _, scheduler) = Bus();
        var first = AudioOwnerToken.New();
        var second = AudioOwnerToken.New();

        bus.Claim(first, new VoiceAudioConfiguration(), Handlers());
        bus.Claim(second, new VoiceAudioConfiguration(), Handlers());

        Assert.True(bus.IsOwner(second));
        Assert.False(bus.IsOwner(first));
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Второй_отбой_по_той_же_линии_проходит_вхолостую()
    {
        var (bus, _, scheduler) = Bus();
        var line = AudioOwnerToken.New();

        bus.Claim(line, new VoiceAudioConfiguration(), Handlers());

        Assert.True(bus.Release(line));
        Assert.False(bus.Release(line));

        // И освобождение устройства назначено один раз, а не дважды.
        Assert.Equal(1, scheduler.PendingCount);
    }

    [Fact]
    public void Отбой_снимает_обработчики()
    {
        // Иначе тракт продолжал бы отдавать кадры линии, которая уже положила
        // трубку, — и она отправляла бы их в закрытую сессию.
        var (bus, engine, _) = Bus();
        var line = AudioOwnerToken.New();

        bus.Claim(line, new VoiceAudioConfiguration(), Handlers());
        bus.Release(line);

        Assert.Same(VoiceAudioHandlers.None, engine.Handlers);
    }

    [Fact]
    public void Освобождение_устройства_откладывается_а_не_делается_сразу()
    {
        // Не в витке отбоя: там оператор ждёт, а закрытие потоков WASAPI — это
        // ожидание звуковой службы.
        var (bus, engine, scheduler) = Bus();
        var line = AudioOwnerToken.New();

        bus.Claim(line, new VoiceAudioConfiguration(), Handlers());
        bus.Release(line);

        Assert.Equal(0, engine.DisposeCount);
        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal(VoiceAudioBus.DefaultRetirementDelay, scheduler.LastDelay);

        scheduler.Fire();
        Assert.Equal(1, engine.DisposeCount);
    }

    [Fact]
    public void Звонок_раньше_срока_отменяет_освобождение_устройства()
    {
        // Пересобирать устройство, которое сейчас же понадобится снова, значит
        // добавить оператору лишнюю паузу в начале разговора.
        var (bus, engine, scheduler) = Bus();
        var first = AudioOwnerToken.New();

        bus.Claim(first, new VoiceAudioConfiguration(), Handlers());
        bus.Release(first);

        bus.Claim(AudioOwnerToken.New(), new VoiceAudioConfiguration(), Handlers());

        Assert.Equal(1, scheduler.CancelledCount);
        scheduler.Fire();
        Assert.Equal(0, engine.DisposeCount);
    }

    [Fact]
    public void Опоздавшее_освобождение_не_трогает_начавшийся_разговор()
    {
        // Отмена не останавливает работу, которая уже началась — так ведут себя
        // и таймер, и очередь оригинала. Единственное, что отделяет
        // освобождение устройства от разговора, начавшегося миллисекунду
        // назад, — повторная проверка владения внутри.
        var (bus, engine, scheduler) = Bus();
        var first = AudioOwnerToken.New();

        bus.Claim(first, new VoiceAudioConfiguration(), Handlers());
        bus.Release(first);
        bus.Claim(AudioOwnerToken.New(), new VoiceAudioConfiguration(), Handlers());

        scheduler.FireEvenIfCancelled();

        Assert.Equal(0, engine.DisposeCount);
    }

    [Fact]
    public void Выключенное_освобождение_оставляет_устройство_за_нами()
    {
        // На проводной гарнитуре отпускать нечего, а лишняя пересборка стоит
        // паузы в начале следующего звонка.
        var (bus, engine, scheduler) = Bus();
        var line = AudioOwnerToken.New();

        bus.Claim(
            line,
            new VoiceAudioConfiguration { ReleasesDeviceWhenIdle = false },
            Handlers());
        bus.Release(line);
        scheduler.Fire();

        Assert.Equal(0, engine.DisposeCount);
    }

    [Fact]
    public void Несобравшийся_сменный_тракт_оставляет_прежний()
    {
        // Разговор без эхоподавления плох, разговор без тракта невозможен вовсе.
        FakeVoiceAudioEngine engine = new();
        ManualScheduler scheduler = new();
        using VoiceAudioBus bus = new(
            engine,
            _ => throw new InvalidOperationException("устройства нет"),
            scheduler.Schedule,
            TimeSpan.FromSeconds(1));

        var line = AudioOwnerToken.New();
        bus.Claim(line, new VoiceAudioConfiguration(), Handlers());
        bus.Release(line);
        scheduler.Fire();

        Assert.Equal(0, engine.DisposeCount);

        // И тракт всё ещё рабочий: следующий звонок поднимется.
        var next = AudioOwnerToken.New();
        bus.Claim(next, new VoiceAudioConfiguration(), Handlers());
        Assert.True(bus.IsOwner(next));
    }

    [Fact]
    public void После_освобождения_устройства_работает_сменный_тракт()
    {
        FakeVoiceAudioEngine first = new();
        FakeVoiceAudioEngine second = new();
        ManualScheduler scheduler = new();
        using VoiceAudioBus bus = new(first, _ => second, scheduler.Schedule, TimeSpan.FromSeconds(1));

        var line = AudioOwnerToken.New();
        bus.Claim(line, new VoiceAudioConfiguration(), Handlers());
        bus.Release(line);
        scheduler.Fire();

        Assert.Equal(1, first.DisposeCount);

        var next = AudioOwnerToken.New();
        bus.Claim(next, new VoiceAudioConfiguration(), Handlers());

        Assert.Equal(1, second.StartCount);
        Assert.Equal(1, first.StartCount);
    }

    [Fact]
    public void Сменный_тракт_собирается_под_настройки_последнего_разговора()
    {
        // Иначе после отбоя на гарнитуре тракт пересобрался бы под умолчания и
        // следующий звонок ушёл бы на встроенный микрофон.
        FakeVoiceAudioEngine first = new();
        FakeVoiceAudioEngine second = new();
        VoiceAudioConfiguration? asked = null;
        ManualScheduler scheduler = new();
        using VoiceAudioBus bus = new(
            first,
            configuration => { asked = configuration; return second; },
            scheduler.Schedule,
            TimeSpan.FromSeconds(1));

        VoiceAudioConfiguration used = new() { InputDeviceId = "гарнитура", MicrophoneGain = 1.5f };
        var line = AudioOwnerToken.New();
        bus.Claim(line, used, Handlers());
        bus.Release(line);
        scheduler.Fire();

        Assert.NotNull(asked);
        Assert.Equal("гарнитура", asked.InputDeviceId);
        Assert.Equal(1.5f, asked.MicrophoneGain);
    }

    [Fact]
    public void Дотянуться_до_тракта_может_только_владелец()
    {
        var (bus, engine, _) = Bus();
        var owner = AudioOwnerToken.New();
        var stranger = AudioOwnerToken.New();

        bus.Claim(owner, new VoiceAudioConfiguration(), Handlers());

        Assert.False(bus.WithEngine(stranger, _ => Assert.Fail("чужому тракт доставаться не должен")));
        Assert.True(bus.WithEngine(owner, e => e.Restart("проверка")));
        Assert.Equal(1, engine.RestartCount);
    }

    [Fact]
    public void Пересборка_чужим_ключом_не_проходит()
    {
        var (bus, engine, _) = Bus();
        var owner = AudioOwnerToken.New();

        bus.Claim(owner, new VoiceAudioConfiguration(), Handlers());

        Assert.False(bus.Restart(AudioOwnerToken.New(), "чужая пересборка"));
        Assert.Equal(0, engine.RestartCount);

        Assert.True(bus.Restart(owner, "своя пересборка"));
        Assert.Equal("своя пересборка", engine.LastRestartReason);
    }

    [Fact]
    public void Ответ_из_тракта_доходит_до_владельца()
    {
        var (bus, _, _) = Bus();
        var owner = AudioOwnerToken.New();
        bus.Claim(owner, new VoiceAudioConfiguration(), Handlers());

        Assert.True(bus.TryWithEngine(owner, _ => 42, out int answer));
        Assert.Equal(42, answer);

        Assert.False(bus.TryWithEngine(AudioOwnerToken.New(), _ => 42, out int nothing));
        Assert.Equal(0, nothing);
    }

    [Fact]
    public void Освобождение_шины_гасит_тракт_и_отменяет_отсрочку()
    {
        var (bus, engine, scheduler) = Bus();
        var line = AudioOwnerToken.New();

        bus.Claim(line, new VoiceAudioConfiguration(), Handlers());
        bus.Release(line);
        bus.Dispose();

        Assert.Equal(1, scheduler.CancelledCount);
        Assert.Equal(1, engine.DisposeCount);

        // Повторное — вхолостую: приложение закрывают один раз, но путей туда
        // несколько.
        bus.Dispose();
        Assert.Equal(1, engine.DisposeCount);
    }

    [Fact]
    public void Захват_после_закрытия_это_ошибка()
    {
        var (bus, _, _) = Bus();
        bus.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => bus.Claim(AudioOwnerToken.New(), new VoiceAudioConfiguration(), Handlers()));
    }

    private static VoiceAudioHandlers Handlers() => new() { Diagnostic = _ => { } };

    private static (VoiceAudioBus Bus, FakeVoiceAudioEngine Engine, ManualScheduler Scheduler) Bus()
    {
        FakeVoiceAudioEngine engine = new();
        ManualScheduler scheduler = new();
        VoiceAudioBus bus = new(
            engine,
            _ => new FakeVoiceAudioEngine(),
            scheduler.Schedule,
            VoiceAudioBus.DefaultRetirementDelay);

        return (bus, engine, scheduler);
    }
}
