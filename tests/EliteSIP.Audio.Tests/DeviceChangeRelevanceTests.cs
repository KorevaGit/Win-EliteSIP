using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Касается ли нас то, что произошло в звуковом хозяйстве.
///
/// Обе ошибки здесь дорогие и противоположные: лишняя пересборка рвёт живой
/// разговор паузой до восьми десятых секунды, пропущенная оставляет оператора
/// говорить в выдернутую гарнитуру. На живом железе это проверяется только
/// выдёргиванием проводов в нужном порядке; здесь — тестом.
/// </summary>
public sealed class DeviceChangeRelevanceTests
{
    private const string Headset = "{0.0.1.00000000}.{гарнитура}";
    private const string HeadsetOut = "{0.0.0.00000000}.{гарнитура-выход}";
    private const string Builtin = "{0.0.1.00000000}.{встроенный}";
    private const string Stranger = "{0.0.0.00000000}.{чужой}";

    [Fact]
    public void Исчезновение_нашего_устройства_касается_нас()
    {
        // Ради этого случая всё и делается: гарнитуру выдернули посреди
        // разговора.
        Assert.True(Affects(
            AudioDeviceChangeKind.DeviceRemoved,
            Headset,
            Pinned()));
    }

    [Fact]
    public void Исчезновение_чужого_устройства_нас_не_касается()
    {
        // На этой машине двадцать конечных точек. Пересобирать тракт из-за
        // монитора с динамиками значит рвать разговор по поводу, к нему не
        // относящемуся.
        Assert.False(Affects(
            AudioDeviceChangeKind.DeviceRemoved,
            Stranger,
            Pinned()));
    }

    [Fact]
    public void Смена_умолчания_касается_только_той_стороны_что_за_ним_следует()
    {
        // Микрофон назначен явно, выход — нет. Значит смена системного
        // микрофона нас не трогает, а смена системного выхода трогает.
        AudioRouteBinding binding = new(Headset, null, Headset, Builtin);

        Assert.False(Affects(
            AudioDeviceChangeKind.DefaultChanged,
            Stranger,
            binding,
            AudioDeviceDirection.Capture));

        Assert.True(Affects(
            AudioDeviceChangeKind.DefaultChanged,
            Stranger,
            binding,
            AudioDeviceDirection.Render));
    }

    [Fact]
    public void Смена_умолчания_без_направления_считается_нашей()
    {
        // Пропустить смену маршрута хуже, чем пересобрать лишний раз: в первом
        // случае оператор говорит в никуда, во втором слышит паузу.
        Assert.True(Affects(
            AudioDeviceChangeKind.DefaultChanged,
            Stranger,
            new AudioRouteBinding(Headset, HeadsetOut, Headset, HeadsetOut),
            direction: null));
    }

    [Fact]
    public void Возвращение_назначенной_гарнитуры_касается_нас()
    {
        // Гарнитуры не было при старте разговора, тракт взял встроенный
        // микрофон. Ждать, пока оператор сам заметит и переключит, — значит
        // оставить его на динамиках ноутбука весь разговор.
        AudioRouteBinding onFallback = new(Headset, HeadsetOut, Builtin, Builtin);

        Assert.True(Affects(AudioDeviceChangeKind.DeviceAdded, Headset, onFallback));
        Assert.True(Affects(AudioDeviceChangeKind.DeviceAdded, HeadsetOut, onFallback));
    }

    [Fact]
    public void Появление_постороннего_устройства_нас_не_касается()
    {
        AudioRouteBinding onFallback = new(Headset, HeadsetOut, Builtin, Builtin);

        Assert.False(Affects(AudioDeviceChangeKind.DeviceAdded, Stranger, onFallback));
    }

    [Fact]
    public void Появление_устройства_когда_мы_и_так_на_своём_нас_не_касается()
    {
        // Тракт уже работает на назначенной гарнитуре. Появление чего угодно
        // ещё — не повод его трогать.
        Assert.False(Affects(AudioDeviceChangeKind.DeviceAdded, Stranger, Pinned()));
        Assert.False(Affects(AudioDeviceChangeKind.DeviceAdded, Headset, Pinned()));
    }

    [Fact]
    public void Смена_состояния_нашего_устройства_касается_нас()
    {
        // Гарнитуру выключили в параметрах звука или вынули из разъёма.
        Assert.True(Affects(AudioDeviceChangeKind.DeviceStateChanged, HeadsetOut, Pinned()));
    }

    [Fact]
    public void Оживление_назначенного_устройства_на_подмене_касается_нас()
    {
        // Устройство не появилось заново, а сменило состояние на действующее —
        // так возвращается Bluetooth-гарнитура, которая из системы никуда не
        // девалась.
        AudioRouteBinding onFallback = new(Headset, HeadsetOut, Builtin, Builtin);

        Assert.True(Affects(AudioDeviceChangeKind.DeviceStateChanged, Headset, onFallback));
    }

    [Fact]
    public void Следование_умолчанию_не_считается_подменой()
    {
        // Устройство не назначено вовсе — значит ждать нечего, и появление
        // любого нового устройства само по себе не повод.
        AudioRouteBinding follows = new(null, null, Builtin, Builtin);

        Assert.False(follows.UsesFallbackInput);
        Assert.False(follows.UsesFallbackOutput);
        Assert.False(Affects(AudioDeviceChangeKind.DeviceAdded, Headset, follows));
    }

    [Fact]
    public void Пустой_идентификатор_не_совпадает_ни_с_чем()
    {
        // Смена умолчания приходит с пустым идентификатором, когда устройств
        // этого направления не осталось вовсе. Считать это совпадением с нашим
        // устройством нельзя.
        Assert.False(Affects(AudioDeviceChangeKind.DeviceRemoved, string.Empty, Pinned()));
    }

    private static AudioRouteBinding Pinned() => new(Headset, HeadsetOut, Headset, HeadsetOut);

    private static bool Affects(
        AudioDeviceChangeKind kind,
        string deviceId,
        AudioRouteBinding binding,
        AudioDeviceDirection? direction = null) =>
        DeviceChangeRelevance.AffectsRoute(
            new AudioDeviceChange(kind, deviceId, direction, null),
            binding);
}
