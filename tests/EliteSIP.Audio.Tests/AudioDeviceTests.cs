using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Опознание устройства: чем подключено, что это значит и как читается в
/// журнале.
///
/// Проверяется без звуковой карты намеренно — устройств в CI нет. Всё, что
/// требует железа, закрывается матрицей устройств из W0 и приёмкой этапа; сюда
/// вынесено ровно то, что можно ошибиться молча: разбор имени перечислителя.
///
/// Значения перечислителей и форм-факторов — не из документации, а с этой
/// машины: они сняты прогоном по всем конечным точкам, включая невоткнутые.
/// </summary>
public sealed class AudioDeviceTests
{
    [Theory]
    [InlineData("HDAUDIO", AudioTransport.BuiltIn)]
    [InlineData("USB", AudioTransport.Usb)]
    [InlineData("BTHENUM", AudioTransport.Bluetooth)]
    [InlineData("BTHHFENUM", AudioTransport.BluetoothHandsFree)]
    [InlineData("SWD", AudioTransport.Virtual)]
    [InlineData("НЕЧТО", AudioTransport.Other)]
    [InlineData("", AudioTransport.Other)]
    public void Шина_опознаётся_по_имени_перечислителя(string enumeratorName, AudioTransport expected)
    {
        Assert.Equal(expected, AudioDeviceCatalog.Classify(enumeratorName, formFactor: -1));
    }

    [Fact]
    public void Регистр_имени_перечислителя_не_важен()
    {
        Assert.Equal(AudioTransport.BluetoothHandsFree, AudioDeviceCatalog.Classify("bthhfenum", -1));
        Assert.Equal(AudioTransport.Usb, AudioDeviceCatalog.Classify("Usb", -1));
    }

    [Fact]
    public void Стерео_и_режим_связи_у_одной_гарнитуры_различаются()
    {
        // Это главная проверка файла. У AirPods Pro на этой машине две
        // конечные точки: «Stereo» через BTHENUM и «Hands-Free AG Audio»
        // через BTHHFENUM. Первая — 44 100 Гц стерео, вторая — 8000 Гц моно.
        // Спутать их значит открыть микрофон и молча испортить звук во всей
        // системе, а заодно согласовать G.722 там, где его никто не услышит.
        Assert.Equal(AudioTransport.Bluetooth, AudioDeviceCatalog.Classify("BTHENUM", -1));
        Assert.Equal(AudioTransport.BluetoothHandsFree, AudioDeviceCatalog.Classify("BTHHFENUM", -1));
    }

    [Fact]
    public void Видеовыход_опознаётся_форм_фактором_а_не_шиной()
    {
        // Перечислитель у HDMI тот же HDAUDIO, что у встроенной карты: по нему
        // одно неотличимо от другого. Различает только форм-фактор.
        Assert.Equal(AudioTransport.Hdmi, AudioDeviceCatalog.Classify("HDAUDIO", formFactor: 9));
        Assert.Equal(AudioTransport.BuiltIn, AudioDeviceCatalog.Classify("HDAUDIO", formFactor: 1));
    }

    [Fact]
    public void Форм_фактор_не_различает_гарнитуры()
    {
        // Записано проверкой, потому что форм-фактор напрашивается первым и
        // выглядит подходящим. У проводной LifeChat и у AirPods он одинаковый
        // (5, Headset), и опираться на него значило бы не отличить USB от
        // Bluetooth ровно там, где разница решает.
        const int Headset = 5;
        Assert.Equal(AudioTransport.Usb, AudioDeviceCatalog.Classify("USB", Headset));
        Assert.Equal(AudioTransport.BluetoothHandsFree, AudioDeviceCatalog.Classify("BTHHFENUM", Headset));
    }

    [Theory]
    [InlineData(8000, true)]
    [InlineData(16000, false)]
    [InlineData(44100, false)]
    [InlineData(0, false)]
    public void Узкая_полоса_считается_по_частоте_устройства(int sampleRate, bool expected)
    {
        // Ноль — «частота неизвестна», а не «узкая полоса»: объявить разговор
        // узкополосным из-за неудавшегося чтения значило бы отказаться от
        // G.722 на исправной гарнитуре.
        Assert.Equal(expected, Device(sampleRate: sampleRate).IsNarrowband);
    }

    [Fact]
    public void Режим_гарнитуры_виден_по_шине()
    {
        Assert.True(Device(transport: AudioTransport.BluetoothHandsFree).SwitchesToHeadsetMode);
        Assert.False(Device(transport: AudioTransport.Bluetooth).SwitchesToHeadsetMode);
        Assert.False(Device(transport: AudioTransport.Usb).SwitchesToHeadsetMode);
    }

    [Fact]
    public void Сводка_называет_источник_частоты()
    {
        // «Заявлено» значит, что число взято из хранилища свойств, то есть
        // описывает прошлое подключение. В отчёте это должно быть видно: иначе
        // 8000 Гц у отключённой гарнитуры прочтут как факт о будущем звонке.
        Assert.Contains("48000 Гц", Device(formatIsLive: true).Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("заявлено", Device(formatIsLive: true).Summary, StringComparison.Ordinal);
        Assert.Contains("(заявлено)", Device(formatIsLive: false).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Сводка_молчит_про_состояние_только_у_действующего()
    {
        Assert.DoesNotContain("не воткнуто", Device().Summary, StringComparison.Ordinal);
        Assert.Contains(
            "не воткнуто",
            Device(availability: AudioDeviceAvailability.Unplugged).Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Незнакомая_шина_попадает_в_сводку_как_есть()
    {
        // Чтобы разбор незнакомого случая начинался с имени, а не с догадки.
        AudioDevice device = Device(transport: AudioTransport.Other) with { TransportName = "НЕЧТО" };
        Assert.Contains("иное (НЕЧТО)", device.Summary, StringComparison.Ordinal);
    }

    private static AudioDevice Device(
        AudioTransport transport = AudioTransport.Usb,
        AudioDeviceAvailability availability = AudioDeviceAvailability.Active,
        int sampleRate = 48000,
        bool formatIsLive = true) =>
        new(
            Id: "{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}",
            Name: "Гарнитура",
            Direction: AudioDeviceDirection.Capture,
            Transport: transport,
            TransportName: "USB",
            Availability: availability,
            Channels: 1,
            SampleRate: sampleRate,
            FormatIsLive: formatIsLive);
}
