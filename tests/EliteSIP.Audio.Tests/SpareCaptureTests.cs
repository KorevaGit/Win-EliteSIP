namespace EliteSIP.Audio.Tests;

/// <summary>
/// Выбор запасного микрофона, когда системный виртуальный или молчит.
/// </summary>
///
/// <remarks>
/// Случай с живой машины 11 сентября 2026: системным микрофоном для связи был
/// «Headset Microphone (Oculus Virtual Audio Device)», наушники — «Наушники
/// (Realtek(R) Audio)», гарнитура воткнута в Realtek. Тракт открывал Oculus и
/// отдавал собеседнику тишину.
/// </remarks>
public sealed class SpareCaptureTests
{
    private static readonly AudioDevice Oculus = Capture(
        "oculus", "Headset Microphone (Oculus Virtual Audio Device)", AudioTransport.Virtual);

    private static readonly AudioDevice LifeChat = Capture(
        "lifechat", "Микрофон гарнитуры (2- Microsoft LifeChat LX-3000)", AudioTransport.Usb);

    private static readonly AudioDevice Realtek = Capture(
        "realtek", "Набор микрофонов (Realtek(R) Audio)", AudioTransport.BuiltIn);

    private static readonly AudioDevice AirPods = Capture(
        "airpods", "Головной телефон (AirPods Pro Hands-Free AG Audio)", AudioTransport.BluetoothHandsFree);

    [Fact]
    public void Берётся_микрофон_той_же_карты_что_и_наушники()
    {
        AudioDevice? spare = AudioDeviceCatalog.PickSpareCapture(
            [Oculus, LifeChat, Realtek],
            ["oculus"],
            "Наушники (Realtek(R) Audio)");

        Assert.Equal("realtek", spare?.Id);
    }

    [Fact]
    public void Виртуальный_не_берётся_никогда()
    {
        AudioDevice? spare = AudioDeviceCatalog.PickSpareCapture([Oculus], [], "Наушники (Oculus Virtual Audio Device)");

        Assert.Null(spare);
    }

    [Fact]
    public void Без_подсказки_по_карте_проводной_раньше_Bluetooth()
    {
        // Bluetooth в режиме гарнитуры переводит наушники в моно 8 кГц — его
        // берём последним, даже если он в списке первым.
        AudioDevice? spare = AudioDeviceCatalog.PickSpareCapture([AirPods, LifeChat], [], renderName: null);

        Assert.Equal("lifechat", spare?.Id);
    }

    [Fact]
    public void Замолчавшие_не_берутся_повторно()
    {
        AudioDevice? spare = AudioDeviceCatalog.PickSpareCapture(
            [Realtek, LifeChat],
            ["realtek"],
            "Наушники (Realtek(R) Audio)");

        Assert.Equal("lifechat", spare?.Id);
    }

    [Fact]
    public void Неподключённый_не_берётся()
    {
        AudioDevice unplugged = Realtek with { Availability = AudioDeviceAvailability.Unplugged };

        Assert.Null(AudioDeviceCatalog.PickSpareCapture([unplugged], [], "Наушники (Realtek(R) Audio)"));
    }

    [Theory]
    [InlineData("Наушники (Realtek(R) Audio)", "Realtek(R) Audio")]
    [InlineData("Микрофон гарнитуры (2- Microsoft LifeChat LX-3000)", "2- Microsoft LifeChat LX-3000")]
    [InlineData("Динамики", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Имя_карты_берётся_из_последних_скобок_с_учётом_вложенных(string? endpoint, string? adapter)
        => Assert.Equal(adapter, AudioDeviceCatalog.AdapterOf(endpoint));

    [Theory]
    // Журнал 1 октября 2026: донгл JBL заявляет выход «Динамиками», и по
    // форм-фактору гарнитура в нём не видна.
    [InlineData(AudioTransport.Usb, "Микрофон (JBL Quantum350 Wireless)", AudioTransport.Usb, "Динамики (JBL Quantum350 Wireless)", true)]
    [InlineData(AudioTransport.BluetoothHandsFree, "Головной телефон (AirPods Pro)", AudioTransport.BluetoothHandsFree, "Головной телефон (AirPods Pro)", true)]
    // Микрофон и динамики ноутбука — как раз та пара, между которыми эхо есть.
    [InlineData(AudioTransport.BuiltIn, "Микрофон (Realtek(R) Audio)", AudioTransport.BuiltIn, "Динамики (Realtek(R) Audio)", false)]
    // Микрофон ноутбука при наушниках-донгле — разные устройства.
    [InlineData(AudioTransport.BuiltIn, "Набор микрофонов (Realtek(R) Audio)", AudioTransport.Usb, "Динамики (JBL Quantum350 Wireless)", false)]
    [InlineData(AudioTransport.Usb, "Микрофон (Logitech Webcam)", AudioTransport.Usb, "Динамики (JBL Quantum350 Wireless)", false)]
    public void Гарнитура_опознаётся_по_общей_карте_микрофона_и_выхода(
        AudioTransport captureTransport,
        string captureName,
        AudioTransport renderTransport,
        string renderName,
        bool expected)
        => Assert.Equal(
            expected,
            AudioDeviceCatalog.IsHeadsetPair(captureTransport, captureName, renderTransport, renderName));

    private static AudioDevice Capture(string id, string name, AudioTransport transport) => new(
        id,
        name,
        AudioDeviceDirection.Capture,
        transport,
        TransportName: string.Empty,
        AudioDeviceAvailability.Active,
        Channels: 1,
        SampleRate: 48_000,
        FormatIsLive: true);
}
