using EliteSIP.MediaCore;

namespace EliteSIP.MediaCore.Tests;

/// <summary>SDES и SRTP.</summary>
public sealed class SrtpTests
{
    [Fact]
    public void Круг_SDES_сохраняет_метку_профиль_и_тридцать_байт_ключа()
    {
        var bytes = new byte[SrtpMasterKey.ByteCount];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)index;
        }

        SdesCryptoLine original = new(7, SrtpCryptoSuite.AesCm128HmacSha1Tag80, new SrtpMasterKey(bytes));
        var parsed = SdesCryptoLine.Parse(original.Value);

        Assert.Equal(original, parsed);
        Assert.StartsWith("7 AES_CM_128_HMAC_SHA1_80 inline:", original.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ослабляющие параметры не игнорируются, а отвергают строку целиком.
    /// </summary>
    ///
    /// <remarks>
    /// Пропустить их значило бы согласиться на семантику, которую контекст не
    /// выполняет: <c>UNENCRYPTED_SRTP</c> — это «шифрования нет», и молчаливое
    /// согласие с ним даёт разговор, который считается защищённым и не защищён.
    /// </remarks>
    [Fact]
    public void SDES_отвергает_ослабляющие_и_неподдерживаемые_параметры()
    {
        var key = new SrtpMasterKey(Repeated(1));
        var line = new SdesCryptoLine(key).Value;

        Assert.Null(SdesCryptoLine.Parse(line + " UNENCRYPTED_SRTP"));
        Assert.Null(SdesCryptoLine.Parse(line + "|2^20"));
        Assert.Null(SdesCryptoLine.Parse(
            "0 AES_CM_128_HMAC_SHA1_80 inline:" + Convert.ToBase64String(key.Bytes.Span)));
    }

    /// <summary>
    /// Вывод ключей сверяется с приложением B.3 RFC 3711.
    /// </summary>
    ///
    /// <remarks>
    /// Единственная проверка, которая ловит расхождение с чужой реализацией, не
    /// поднимая разговора: сойдись здесь числа неверно — и Asterisk просто не
    /// услышал бы нас, а разбирать это пришлось бы на живом звонке.
    /// </remarks>
    [Fact]
    public void Вывод_ключей_совпадает_с_приложением_RFC_3711()
    {
        SrtpMasterKey master = new(FromHex(
            "E1F97A0D3E018BE0D64FA32C06DE4139" +
            "0EC675AD498AFEEBB6960B3AABE6"));

        var keys = new SrtpContext(master).DerivedSessionKeys;

        Assert.Equal(FromHex("C61E7A93744F39EE10734AFE3FF7A087"), keys.Encryption);
        Assert.Equal(FromHex("CEBE321F6FF7716B6FD4AB49AF256A156D38BAA4"), keys.Authentication);
        Assert.Equal(FromHex("30CBBC08863D8C85D49DB34A9AE1"), keys.Salt);
    }

    [Fact]
    public void Защищённый_пакет_читается_встречным_контекстом()
    {
        var bytes = new byte[SrtpMasterKey.ByteCount];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)index;
        }

        SrtpMasterKey key = new(bytes);
        SrtpContext sender = new(key);
        SrtpContext receiver = new(key);

        RtpPacket packet = new(
            payloadType: 0,
            sequenceNumber: 65_530,
            timestamp: 0xDECAFBAD,
            ssrc: 0xCAFEBABE,
            marker: true,
            payload: Enumerable.Repeat((byte)0xAB, 160).ToArray());

        var protectedPacket = sender.Protect(packet);
        var plain = packet.Encoded();

        Assert.Equal(plain.Length + 10, protectedPacket.Length);

        // Заголовок открыт — по нему получатель и выводит индекс пакета.
        Assert.Equal(
            plain.AsSpan(0, RtpPacket.HeaderByteCount).ToArray(),
            protectedPacket.AsSpan(0, RtpPacket.HeaderByteCount).ToArray());

        // А содержимое — нет.
        Assert.NotEqual(
            packet.Payload.ToArray(),
            protectedPacket.AsSpan(RtpPacket.HeaderByteCount, packet.Payload.Length).ToArray());

        Assert.Equal(packet, receiver.Unprotect(protectedPacket));
    }

    [Fact]
    public void Подмена_и_повтор_пакета_отбрасываются()
    {
        SrtpMasterKey key = new(Repeated(0x42));
        SrtpContext sender = new(key);
        SrtpContext receiver = new(key);

        RtpPacket packet = new(
            payloadType: 8,
            sequenceNumber: 100,
            timestamp: 160,
            ssrc: 99,
            payload: Enumerable.Repeat((byte)0xD5, 160).ToArray());

        var protectedPacket = sender.Protect(packet);

        var changed = protectedPacket.ToArray();
        changed[20] ^= 1;

        Assert.Equal(
            SrtpFailure.AuthenticationFailed,
            Assert.Throws<SrtpException>(() => receiver.Unprotect(changed)).Failure);

        Assert.Equal(packet, receiver.Unprotect(protectedPacket));

        Assert.Equal(
            SrtpFailure.ReplayedPacket,
            Assert.Throws<SrtpException>(() => receiver.Unprotect(protectedPacket)).Failure);
    }

    [Fact]
    public void Счётчик_оборотов_переживает_переход_номера_через_ноль()
    {
        SrtpMasterKey key = new(Repeated(0x77));
        SrtpContext sender = new(key);
        SrtpContext receiver = new(key);

        foreach (var sequence in new ushort[] { ushort.MaxValue - 1, ushort.MaxValue, 0, 1 })
        {
            RtpPacket packet = new(
                payloadType: 0,
                sequenceNumber: sequence,
                timestamp: sequence,
                ssrc: 1234,
                payload: new byte[] { (byte)sequence });

            Assert.Equal(packet, receiver.Unprotect(sender.Protect(packet)));
        }
    }

    internal static byte[] Repeated(byte value)
        => Enumerable.Repeat(value, SrtpMasterKey.ByteCount).ToArray();

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}
