using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Различение трёх состояний открытого устройства.
///
/// Числа в проверках — из замеров W0 на этой машине, а не из общих
/// соображений: исправная проводная гарнитура отдала 99,977% звука при двух
/// разрывах за 297 с, донгл с выключенной гарнитурой — 79% при 2923 разрывах за
/// 57 с. Система на оба случая отвечает одинаково: «устройство действует».
/// </summary>
public sealed class DeviceActivityWatchTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Исправное_устройство_работает()
    {
        // Проводная LifeChat из замера W0: 99,977% и два разрыва за 297 с.
        DeviceActivityWatch watch = new(SampleRate);
        TimeSpan elapsed = TimeSpan.FromSeconds(297);

        watch.NoteDelivered((int)(SampleRate * 297 * 0.99977));
        watch.NoteDiscontinuity(); // начальный, не считается
        watch.NoteDiscontinuity();
        watch.NoteDiscontinuity();

        Assert.Equal(DeviceActivity.Working, watch.Assess(elapsed));
        Assert.Equal(string.Empty, DeviceActivityWatch.Advice(DeviceActivity.Working));
    }

    [Fact]
    public void Выключенная_гарнитура_на_воткнутом_донгле_опознаётся()
    {
        // Донгл JBL из замера W0: 79% отданного звука и 2923 разрыва за 57 с.
        // Для системы устройство действует; для разговора — нет.
        DeviceActivityWatch watch = new(SampleRate);
        TimeSpan elapsed = TimeSpan.FromSeconds(57);

        watch.NoteDelivered((int)(SampleRate * 57 * 0.79));
        for (int i = 0; i <= 2923; i++)
        {
            watch.NoteDiscontinuity();
        }

        // Сбой, а не молчание: верно и то и другое, но пересборка тракта —
        // действие, а просьба к человеку — крайнее средство.
        Assert.Equal(DeviceActivity.Faulty, watch.Assess(elapsed));
        Assert.Contains("рвётся", DeviceActivityWatch.Advice(DeviceActivity.Faulty), StringComparison.Ordinal);
    }

    [Fact]
    public void Молчащее_но_ровное_устройство_это_не_сбой()
    {
        // Поднятый на штанге микрофон или выключенный микрофон гарнитуры: поток
        // идёт ровно, но звука в нём нет. Здесь чинить нечего, и оператору надо
        // сказать словами.
        DeviceActivityWatch watch = new(SampleRate);
        TimeSpan elapsed = TimeSpan.FromSeconds(30);

        watch.NoteDelivered((int)(SampleRate * 30 * 0.5));

        Assert.Equal(DeviceActivity.SilentButPresent, watch.Assess(elapsed));
        Assert.Contains(
            "гарнитура",
            DeviceActivityWatch.Advice(DeviceActivity.SilentButPresent),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Разгон_не_объявляется_молчанием()
    {
        // Первые пакеты приходят с задержкой открытия устройства, до секунды на
        // Bluetooth. Судить по этому окну значит объявить молчащей любую
        // беспроводную гарнитуру в первый же миг разговора.
        DeviceActivityWatch watch = new(SampleRate);

        Assert.Equal(DeviceActivity.Warmup, watch.Assess(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(DeviceActivity.Warmup, watch.Assess(TimeSpan.FromSeconds(1.9)));
    }

    [Fact]
    public void Первый_разрыв_не_считается()
    {
        // Флаг разрыва на первом пакете после запуска приходит всегда и
        // означает начало потока, а не потерю звука. Считать его сбоем —
        // значит объявлять исправный тракт рваным на каждом прогоне и
        // привыкнуть не замечать настоящий разрыв.
        DeviceActivityWatch watch = new(SampleRate);
        watch.NoteDiscontinuity();

        Assert.Equal(0, watch.Discontinuities);

        watch.NoteDiscontinuity();
        Assert.Equal(1, watch.Discontinuities);
    }

    [Fact]
    public void Порог_молчания_лежит_между_замеренными_случаями()
    {
        // Проверка самого порога, а не поведения: он обязан остаться между 79%
        // пустого донгла и 99,98% исправной гарнитуры, иначе разделять ему
        // нечего.
        Assert.True(DeviceActivityWatch.SilenceThreshold > 0.79);
        Assert.True(DeviceActivityWatch.SilenceThreshold < 0.9977);
    }

    [Fact]
    public void Порог_сбоя_лежит_между_замеренными_случаями()
    {
        // Исправная гарнитура: 2 разрыва за 297 с. Пустой донгл: 51 в секунду.
        Assert.True(DeviceActivityWatch.FaultThreshold > 2.0 / 297.0);
        Assert.True(DeviceActivityWatch.FaultThreshold < 2923.0 / 57.0);
    }

    [Fact]
    public void Сводка_печатает_числа_а_не_только_приговор()
    {
        DeviceActivityWatch watch = new(SampleRate);
        watch.NoteDelivered(SampleRate * 10);

        string summary = watch.Summary(TimeSpan.FromSeconds(10));
        Assert.Contains("работает", summary, StringComparison.Ordinal);
        Assert.Contains("отдано", summary, StringComparison.Ordinal);
        Assert.Contains("разрывов", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Сброс_обнуляет_и_счётчик_первого_разрыва()
    {
        // После пересборки тракта первый пакет снова придёт с флагом разрыва,
        // и снова не должен считаться.
        DeviceActivityWatch watch = new(SampleRate);
        watch.NoteDiscontinuity();
        watch.NoteDiscontinuity();
        watch.Reset();

        watch.NoteDiscontinuity();
        Assert.Equal(0, watch.Discontinuities);
        Assert.Equal(0, watch.DeliveredSamples);
    }
}
