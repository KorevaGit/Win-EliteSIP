namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Пакеты RTP.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/RTPPacketTests.swift</c>.
/// </summary>
public sealed class RtpPacketTests
{
    /// <summary>
    /// Заголовок настоящего пакета: V=2, PT=0 (PCMU), seq=1000, ts=160000,
    /// ssrc=0x12345678.
    /// </summary>
    private static byte[] SampleHeader =>
        [0x80, 0x00, 0x03, 0xE8, 0x00, 0x02, 0x71, 0x00, 0x12, 0x34, 0x56, 0x78];

    [Fact]
    public void Разбирает_пакет_с_фиксированным_заголовком()
    {
        byte[] silence = new byte[160];
        Array.Fill(silence, G711.MuLawSilence);
        byte[] bytes = [.. SampleHeader, .. silence];

        RtpPacket packet = RtpPacket.Parse(bytes);
        Assert.Equal(0, packet.PayloadType);
        Assert.Equal(1000, packet.SequenceNumber);
        Assert.Equal(160_000u, packet.Timestamp);
        Assert.Equal(0x1234_5678u, packet.Ssrc);
        Assert.False(packet.Marker);
        Assert.Empty(packet.Csrcs);
        Assert.Equal(160, packet.Payload.Length);
    }

    [Fact]
    public void Round_trip_байт_в_байт()
    {
        RtpPacket packet = new(
            payloadType: 8,
            sequenceNumber: 65_535,
            timestamp: 4_294_967_295,
            ssrc: 0xDEAD_BEEF,
            payload: new byte[] { 1, 2, 3, 4 },
            marker: true);

        byte[] encoded = packet.Encoded();
        Assert.Equal(RtpPacket.HeaderByteCount + 4, encoded.Length);
        Assert.Equal(packet, RtpPacket.Parse(encoded));
    }

    [Fact]
    public void Маркер_и_payload_type_не_путаются_между_собой()
    {
        // Маркер живёт в старшем бите того же байта, что и payload type.
        // Классическая ошибка — прочитать PT как 0x80|PT.
        RtpPacket marked = new(101, 1, 0, 1, ReadOnlyMemory<byte>.Empty, marker: true);
        RtpPacket decoded = RtpPacket.Parse(marked.Encoded());
        Assert.Equal(101, decoded.PayloadType);
        Assert.True(decoded.Marker);

        RtpPacket plain = new(101, 1, 0, 1, ReadOnlyMemory<byte>.Empty);
        Assert.False(RtpPacket.Parse(plain.Encoded()).Marker);
    }

    [Fact]
    public void Отбрасывает_дополнение()
    {
        byte[] bytes =
        [
            .. SampleHeader,
            0xAA, 0xBB,        // полезная нагрузка
            0x00, 0x00, 0x03,  // 3 байта дополнения
        ];
        bytes[0] |= 0b0010_0000; // флаг P

        RtpPacket packet = RtpPacket.Parse(bytes);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, packet.Payload.ToArray());
    }

    [Fact]
    public void Перешагивает_расширение_заголовка()
    {
        byte[] bytes =
        [
            .. SampleHeader,
            0xBE, 0xDE, 0x00, 0x01, // профиль + длина 1 слово
            0x01, 0x02, 0x03, 0x04, // само расширение
            0x11, 0x22,             // полезная нагрузка
        ];
        bytes[0] |= 0b0001_0000; // флаг X

        // Если расширение не перешагнуть, первые байты звука окажутся мусором.
        RtpPacket packet = RtpPacket.Parse(bytes);
        Assert.Equal(new byte[] { 0x11, 0x22 }, packet.Payload.ToArray());
    }

    [Fact]
    public void Читает_список_CSRC()
    {
        byte[] bytes =
        [
            .. SampleHeader,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x02,
            0xFF,
        ];
        bytes[0] |= 2; // CC = 2

        RtpPacket packet = RtpPacket.Parse(bytes);
        Assert.Equal([1u, 2u], packet.Csrcs);
        Assert.Equal(new byte[] { 0xFF }, packet.Payload.ToArray());
    }

    [Fact]
    public void Мусор_отвергается_с_понятной_ошибкой()
    {
        RtpParseException tooShort = Assert.Throws<RtpParseException>(
            () => RtpPacket.Parse(new byte[] { 0x80, 0x00, 0x00, 0x00 }));
        Assert.Equal(RtpParseFailure.TooShort, tooShort.Failure);
        Assert.Equal(4, tooShort.Detail);

        byte[] wrongVersion = SampleHeader;
        wrongVersion[0] = 0x40; // V = 1
        RtpParseException version = Assert.Throws<RtpParseException>(() => RtpPacket.Parse(wrongVersion));
        Assert.Equal(RtpParseFailure.UnsupportedVersion, version.Failure);
        Assert.Equal(1, version.Detail);

        byte[] truncatedCsrc = SampleHeader;
        truncatedCsrc[0] |= 3; // обещаны три CSRC, а их нет
        Assert.Equal(
            RtpParseFailure.TruncatedCsrc,
            Assert.Throws<RtpParseException>(() => RtpPacket.Parse(truncatedCsrc)).Failure);
    }
}

/// <summary>События telephone-event.</summary>
public sealed class TelephoneEventTests
{
    [Fact]
    public void Round_trip_полезной_нагрузки()
    {
        TelephoneEventPayload payload = new(5, duration: 1600, isEnd: true, volume: 10);
        TelephoneEventPayload decoded = Assert.NotNull(TelephoneEventPayload.Parse(payload.Encoded()));
        Assert.Equal(payload, decoded);
        Assert.Equal(TelephoneEventPayload.ByteCount, payload.Encoded().Length);
    }

    [Fact]
    public void Флаг_конца_и_громкость_лежат_в_одном_байте_и_не_мешают_друг_другу()
    {
        TelephoneEventPayload notEnded = new(1, duration: 160, isEnd: false, volume: 63);
        TelephoneEventPayload decoded = Assert.NotNull(TelephoneEventPayload.Parse(notEnded.Encoded()));
        Assert.False(decoded.IsEnd);
        Assert.Equal(63, decoded.Volume);

        TelephoneEventPayload ended = new(1, duration: 160, isEnd: true, volume: 0);
        Assert.True(Assert.NotNull(TelephoneEventPayload.Parse(ended.Encoded())).IsEnd);
    }

    [Fact]
    public void Громкость_не_выходит_за_пределы_шести_бит() =>
        Assert.Equal(63, new TelephoneEventPayload(0, duration: 0, volume: 200).Volume);

    [Fact]
    public void Коды_событий_по_RFC_4733()
    {
        Assert.Equal<byte?>(0, TelephoneEventPayload.EventCode('0'));
        Assert.Equal<byte?>(9, TelephoneEventPayload.EventCode('9'));
        Assert.Equal<byte?>(10, TelephoneEventPayload.EventCode('*'));
        Assert.Equal<byte?>(11, TelephoneEventPayload.EventCode('#'));
        Assert.Equal<byte?>(12, TelephoneEventPayload.EventCode('A'));
        Assert.Equal<byte?>(15, TelephoneEventPayload.EventCode('D'));
        Assert.Null(TelephoneEventPayload.EventCode('Z'));

        // Обратное преобразование пригодится для входящего DTMF.
        foreach (char character in "0123456789*#ABCD")
        {
            byte? code = TelephoneEventPayload.EventCode(character);
            Assert.Equal(character, code is null ? null : TelephoneEventPayload.Character(code.Value));
        }
    }

    [Fact]
    public void Весь_набор_кодов_из_нашего_предложения_разбирается()
    {
        // В SDP мы объявляем 0-16, значит все эти коды должны быть осмысленными.
        for (byte code = 0; code <= 15; code++)
        {
            Assert.NotNull(TelephoneEventPayload.Character(code));
        }
    }
}
