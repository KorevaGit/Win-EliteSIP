namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Описание кодеков.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/AudioCodecTests.swift</c>.
/// </summary>
public sealed class AudioCodecTests
{
    [Fact]
    public void Статические_payload_type_по_RFC_3551()
    {
        Assert.Equal(0, AudioCodec.Pcmu.PayloadType());
        Assert.Equal(8, AudioCodec.Pcma.PayloadType());
        Assert.Equal(AudioCodec.Pcmu, AudioCodecInfo.FromStaticPayloadType(0));
        Assert.Equal(AudioCodec.Pcma, AudioCodecInfo.FromStaticPayloadType(8));
        Assert.Null(AudioCodecInfo.FromStaticPayloadType(101));
    }

    [Fact]
    public void Имена_для_SDP_совпадают_с_ожиданиями_Asterisk()
    {
        Assert.Equal("PCMU", AudioCodec.Pcmu.SdpName());
        Assert.Equal("PCMA", AudioCodec.Pcma.SdpName());
        Assert.Equal("telephone-event", TelephoneEvent.SdpName);
    }

    [Fact]
    public void Раскладка_пакета_20_мс()
    {
        foreach (AudioCodec codec in AudioCodecInfo.All)
        {
            Assert.Equal(8000u, codec.RtpClockRate());
            Assert.Equal(1, codec.ChannelCount());
            Assert.Equal(160u, codec.TimestampIncrement(20));
            Assert.Equal(80u, codec.TimestampIncrement(10));
            Assert.Equal(160, codec.ByteCount(20));
        }

        Assert.Equal(320, AudioCodec.G722.SampleCount(20));
        Assert.Equal(160, AudioCodec.Pcmu.SampleCount(20));
        Assert.Equal(20, AudioCodecInfo.DefaultPacketTimeMilliseconds);
    }

    [Fact]
    public void Настройки_telephone_event()
    {
        // 101 — то, что Asterisk ставит при dtmfmode=rfc2833.
        Assert.Equal(101, TelephoneEvent.DefaultPayloadType);
        Assert.Equal(8000u, TelephoneEvent.ClockRate);
        Assert.Equal("0-16", TelephoneEvent.SupportedEventRange);
    }
}
