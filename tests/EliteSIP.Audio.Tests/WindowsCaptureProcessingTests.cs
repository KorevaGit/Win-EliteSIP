namespace EliteSIP.Audio.Tests;

/// <summary>
/// Когда микрофон открывается без обработки Windows (RAW).
/// </summary>
public sealed class WindowsCaptureProcessingTests
{
    [Theory]
    // USB- или Bluetooth-гарнитура: микрофон и выход — одна карта.
    [InlineData(AudioTransport.Usb, true, false, true)]
    [InlineData(AudioTransport.BluetoothHandsFree, true, true, true)]
    // Проводная гарнитура в разъёме: выход — наушники, микрофон USB или разъём.
    [InlineData(AudioTransport.Usb, false, true, true)]
    [InlineData(AudioTransport.Other, false, true, true)]
    // Наушники, а микрофон — «набор микрофонов» ноутбука: луч ему нужен.
    [InlineData(AudioTransport.BuiltIn, false, true, false)]
    // Ноутбук целиком: динамики и встроенный микрофон.
    [InlineData(AudioTransport.BuiltIn, false, false, false)]
    // USB-микрофон или камера при колонках: эхо есть, обработка остаётся.
    [InlineData(AudioTransport.Usb, false, false, false)]
    public void Авто_выключает_обработку_только_на_гарнитуре(
        AudioTransport capture,
        bool headsetPair,
        bool echoFree,
        bool expectedRaw)
        => Assert.Equal(
            expectedRaw,
            WindowsCaptureProcessingPolicy.WantsRaw(WindowsCaptureProcessing.Auto, capture, headsetPair, echoFree));

    [Fact]
    public void Ручное_положение_главнее_устройств()
    {
        Assert.False(WindowsCaptureProcessingPolicy.WantsRaw(WindowsCaptureProcessing.On, AudioTransport.Usb, true, true));
        Assert.True(WindowsCaptureProcessingPolicy.WantsRaw(WindowsCaptureProcessing.Off, AudioTransport.BuiltIn, false, false));
    }

    [Fact]
    public void По_умолчанию_авто()
        => Assert.Equal(WindowsCaptureProcessing.Auto, new VoiceAudioConfiguration().WindowsProcessing);
}
