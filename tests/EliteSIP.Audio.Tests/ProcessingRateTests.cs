namespace EliteSIP.Audio.Tests;

/// <summary>
/// Частота, на которой работает обработка голоса.
///
/// Вынесено из <c>WasapiVoiceAudioEngineTests</c> 20 октября 2026: выбор
/// частоты — свойство WebRTC APM, а не WASAPI, и любой движок устройства
/// обязан выбирать её так же.
/// </summary>
public sealed class ProcessingRateTests
{
    [Theory]
    [InlineData(8000, 8000)]
    [InlineData(16000, 16000)]
    [InlineData(32000, 32000)]
    [InlineData(48000, 48000)]
    public void Поддерживаемая_частота_остаётся_собой(int deviceRate, int expected)
    {
        // Гарнитура в режиме связи работает на 8 кГц. Обрабатывать её на 48
        // значило бы трижды пересчитать то, чего в сигнале нет.
        Assert.Equal(expected, VoiceProcessor.NearestSupportedRate(deviceRate));
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(22050, 32000)]
    [InlineData(11025, 16000)]
    [InlineData(96000, 48000)]
    public void Неподдерживаемая_частота_поднимается_до_ближайшей(int deviceRate, int expected)
    {
        // Вверх, а не вниз: понижение резало бы полосу до обработки, то есть
        // выбрасывало бы часть разговора ещё до эхоподавителя.
        Assert.Equal(expected, VoiceProcessor.NearestSupportedRate(deviceRate));
    }
}
