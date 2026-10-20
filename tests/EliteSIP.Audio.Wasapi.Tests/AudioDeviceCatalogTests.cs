using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Перечисление устройств на той машине, где идёт прогон.
///
/// <b>Что здесь можно проверять, а что нельзя.</b> Числа зависят от машины: в
/// CI звуковых устройств нет вовсе, на машине разработки их два десятка.
/// Поэтому проверяется не состав, а то, что перечисление <b>не падает</b> и
/// отдаёт связные описания. Требование не выдуманное: машина без звуковой
/// карты — это и есть сборочный агент, и приложение на ней должно сказать
/// «устройств нет», а не закрыться.
/// </summary>
public sealed class AudioDeviceCatalogTests
{
    [Theory]
    [InlineData(AudioDeviceDirection.Capture)]
    [InlineData(AudioDeviceDirection.Render)]
    public void Перечисление_не_падает_на_машине_без_устройств(AudioDeviceDirection direction)
    {
        IReadOnlyList<AudioDevice> devices = AudioDeviceCatalog.Devices(direction, includeInactive: true);
        Assert.NotNull(devices);
    }

    [Theory]
    [InlineData(AudioDeviceDirection.Capture)]
    [InlineData(AudioDeviceDirection.Render)]
    public void Умолчание_отсутствует_а_не_бросает(AudioDeviceDirection direction)
    {
        // На агенте сборки устройства для связи нет, и это законно.
        AudioDevice? device = AudioDeviceCatalog.Default(direction);
        if (device is not null)
        {
            Assert.Equal(direction, device.Direction);
        }
    }

    [Theory]
    [InlineData(AudioDeviceDirection.Capture)]
    [InlineData(AudioDeviceDirection.Render)]
    public void Описания_связны_и_опознаваемы(AudioDeviceDirection direction)
    {
        foreach (AudioDevice device in AudioDeviceCatalog.Devices(direction, includeInactive: true))
        {
            // Идентификатор — единственное, чем устройство опознаётся, и
            // пустым он быть не может: правило W0 про нестабильный порядок
            // держится целиком на нём.
            Assert.False(string.IsNullOrWhiteSpace(device.Id), "устройство без идентификатора");
            Assert.Equal(direction, device.Direction);
            Assert.True(device.SampleRate >= 0);
            Assert.False(string.IsNullOrWhiteSpace(device.Summary));
        }
    }

    [Fact]
    public void Идентификаторы_не_повторяются()
    {
        // Опознание по идентификатору имеет смысл, только если он один на
        // устройство. У Realtek в списке три конечные точки с именем
        // «Динамики (Realtek(R) Audio)» — по имени их не различить вовсе.
        foreach (AudioDeviceDirection direction in (AudioDeviceDirection[])[
            AudioDeviceDirection.Capture, AudioDeviceDirection.Render])
        {
            IReadOnlyList<AudioDevice> devices = AudioDeviceCatalog.Devices(direction, includeInactive: true);
            HashSet<string> seen = [];
            foreach (AudioDevice device in devices)
            {
                Assert.True(seen.Add(device.Id), $"идентификатор повторился: {device.Id}");
            }
        }
    }

    [Fact]
    public void Найденное_по_идентификатору_совпадает_с_перечисленным()
    {
        IReadOnlyList<AudioDevice> devices =
            AudioDeviceCatalog.Devices(AudioDeviceDirection.Render, includeInactive: false);
        if (devices.Count == 0)
        {
            // Устройств нет — проверять нечего, и это не провал.
            return;
        }

        AudioDevice expected = devices[0];
        AudioDevice? found = AudioDeviceCatalog.Find(expected.Id, AudioDeviceDirection.Render);

        Assert.NotNull(found);
        Assert.Equal(expected.Id, found.Id);
        Assert.Equal(expected.Transport, found.Transport);
    }

    [Fact]
    public void Отсутствующее_устройство_возвращает_ничто()
    {
        // Гарнитуру выдернули — вызывающий обязан взять умолчание, а не
        // отказаться от звонка. Для этого отказ должен быть значением, а не
        // исключением.
        AudioDevice? found = AudioDeviceCatalog.Find(
            "{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}",
            AudioDeviceDirection.Render);

        Assert.Null(found);
    }

    [Fact]
    public void Пустой_идентификатор_это_ошибка_вызывающего()
    {
        // Не «устройства нет», а «спросили не то»: пустая строка приходит
        // только из ненастроенных настроек, и молчать о ней значит однажды
        // молча уехать на чужое устройство.
        Assert.Throws<ArgumentException>(
            () => AudioDeviceCatalog.Find(string.Empty, AudioDeviceDirection.Render));
    }
}
