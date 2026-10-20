using EliteSIP.Audio;
using NAudio.CoreAudioApi;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Подписка на смену устройств.
///
/// <b>Чего здесь нет и быть не может.</b> Настоящая проверка этого типа — это
/// выдернуть гарнитуру посреди разговора, и она входит в приёмку W4 на живой
/// машине, а не в модульные тесты: события рождает звуковая служба, подделать
/// её нечем. Поэтому здесь закрываются два случая, которые всё-таки ловятся
/// без железа и оба стоят дорого: подписка, которая не встаёт, и подписка,
/// которая не снимается.
/// </summary>
public sealed class AudioDeviceWatchTests
{
    [Fact]
    public void Подписка_встаёт_и_снимается()
    {
        // На машине без звуковых устройств тоже: перечислитель существует
        // всегда, и подписаться на пустое хозяйство — законно. Если бы это
        // падало, приложение не запускалось бы на агенте сборки.
        using AudioDeviceWatch watch = new(_ => { });
        Assert.NotNull(watch);
    }

    [Fact]
    public void Повторное_освобождение_проходит_вхолостую()
    {
        // Тот же случай, что и с владением трактом: запоздавший путь приходит
        // освобождать второй раз. Здесь это дешевле — но бросить отсюда
        // значило бы уронить приложение на закрытии.
        AudioDeviceWatch watch = new(_ => { });
        watch.Dispose();
        watch.Dispose();
    }

    [Fact]
    public void Пустой_обработчик_это_ошибка_вызывающего()
    {
        Assert.Throws<ArgumentNullException>(() => new AudioDeviceWatch(null!));
    }

    [Theory]
    [InlineData(DeviceState.Active, AudioDeviceAvailability.Active)]
    [InlineData(DeviceState.Unplugged, AudioDeviceAvailability.Unplugged)]
    [InlineData(DeviceState.Disabled, AudioDeviceAvailability.Disabled)]
    [InlineData(DeviceState.NotPresent, AudioDeviceAvailability.Absent)]
    public void Состояние_переводится_без_потерь(DeviceState state, AudioDeviceAvailability expected)
    {
        // Перевод отделён от подписки ровно затем, чтобы его можно было
        // проверить: внутри обработчика он проверяется только выдёргиванием
        // провода.
        Assert.Equal(expected, AudioDeviceWatch.Translate(state));
    }
}
