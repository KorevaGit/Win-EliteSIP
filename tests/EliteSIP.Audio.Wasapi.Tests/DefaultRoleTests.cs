using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Роли системных устройств Windows.
///
/// Вынесено из <c>SpareCaptureTests</c> 20 октября 2026: правила выбора
/// микрофона общие для всех ОС и проверяются в <c>EliteSIP.Audio.Tests</c>, а
/// роль — это MMDevice API, и проверять её можно только здесь.
/// </summary>
public sealed class DefaultRoleTests
{
    [Fact]
    public void Микрофон_берётся_обычный_по_умолчанию_а_выход_для_связи()
    {
        // Микрофон — тот, что выбран в параметрах Windows: «для связи» Windows
        // сама отдаёт вебкамере. Выход — «для связи»: у Bluetooth-гарнитуры
        // обычным стоит стереоточка, которая молчит при открытом микрофоне.
        Assert.Equal(NAudio.CoreAudioApi.Role.Console, AudioDeviceCatalog.DefaultRole(AudioDeviceDirection.Capture));
        Assert.Equal(NAudio.CoreAudioApi.Role.Communications, AudioDeviceCatalog.DefaultRole(AudioDeviceDirection.Render));
    }
}
