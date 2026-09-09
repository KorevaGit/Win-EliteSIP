using System.Security.Cryptography;

namespace EliteSIP.MediaCore;

/// <summary>Отчего пакет SRTP не прошёл.</summary>
public enum SrtpFailure
{
    /// <summary>Пакет короче заголовка RTP и тега аутентификации.</summary>
    PacketTooShort,

    /// <summary>Повреждён заголовок защищённого RTP-пакета.</summary>
    MalformedHeader,

    /// <summary>Пакет не прошёл проверку подлинности.</summary>
    AuthenticationFailed,

    /// <summary>Повторно полученный пакет отброшен.</summary>
    ReplayedPacket,
}

/// <summary>Отказ SRTP.</summary>
///
/// <remarks>
/// Исключением, а не возвращаемым значением, по правилу порта; причина остаётся
/// кодом. Разбирать её по коду приходится в одном месте — там, где решают,
/// писать ли строку в журнал: подмена и повтор говорят о разном, а выглядят
/// одинаково.
///
/// Отказа «криптопровайдер вернул ошибку» здесь нет, в отличие от оригинала: в
/// .NET AES и HMAC не возвращают статусов, они бросают сами.
/// </remarks>
public sealed class SrtpException : Exception
{
    public SrtpException(SrtpFailure failure)
        : base(TextOf(failure)) => Failure = failure;

    /// <summary>Обязателен по правилам анализатора; в продукте не применяется.</summary>
    public SrtpException()
        : this(SrtpFailure.PacketTooShort)
    {
    }

    /// <summary>Обязателен по правилам анализатора; в продукте не применяется.</summary>
    public SrtpException(string message)
        : base(message) => Failure = SrtpFailure.PacketTooShort;

    /// <summary>Обязателен по правилам анализатора; в продукте не применяется.</summary>
    public SrtpException(string message, Exception innerException)
        : base(message, innerException) => Failure = SrtpFailure.PacketTooShort;

    public SrtpFailure Failure { get; }

    // не переводится: отказы SRTP читают в журнале.
    private static string TextOf(SrtpFailure failure) => failure switch
    {
        SrtpFailure.PacketTooShort => "Пакет SRTP короче заголовка RTP и тега аутентификации.",
        SrtpFailure.MalformedHeader => "Повреждён заголовок защищённого RTP-пакета.",
        SrtpFailure.AuthenticationFailed => "Пакет SRTP не прошёл проверку подлинности.",
        SrtpFailure.ReplayedPacket => "Повторно полученный пакет SRTP отброшен.",
        _ => "Пакет SRTP отброшен.",
    };
}

/// <summary>
/// Состояние одного направления SRTP-потока по RFC 3711.
/// </summary>
///
/// <remarks>
/// Для разговора создаются два независимых контекста: исходящий из нашего ключа
/// SDES и входящий из ключа Asterisk. Смешивать их нельзя — у направлений разные
/// ключи, счётчик оборотов и окно защиты от повторов.
///
/// Класс не потокобезопасен и не должен быть: у каждого направления свой поток
/// вызовов, а общая блокировка на оба означала бы, что приём ждёт отправки.
/// Владеет им <see cref="RtpSession"/>, и он же отвечает за то, чтобы в один
/// контекст не заходили с двух сторон.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Security",
    "CA5350:Не используйте слабые алгоритмы шифрования",
    Justification =
        "HMAC-SHA1 — не наш выбор, а профиль AES_CM_128_HMAC_SHA1_80 из RFC 3711: " +
        "им подписывает пакеты Asterisk, и заменить его на стороне клиента нельзя. " +
        "Тег усечён до 80 бит тем же профилем. Ослабление здесь мнимое: подпись " +
        "живёт один пакет, а известные атаки на SHA-1 — это коллизии, которые в " +
        "HMAC не применимы.")]
public sealed class SrtpContext
{
    private const int TagByteCount = 10;
    private const int ReplayWindowDepth = 64;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _authenticationKey;
    private readonly byte[] _saltKey;

    private uint _outboundRolloverCounter;
    private ushort? _lastOutboundSequence;

    private ulong? _highestInboundIndex;
    private ulong _replayWindow;

    public SrtpContext(SrtpMasterKey masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);

        var key = masterKey.Bytes.Span[..16].ToArray();
        var salt = masterKey.Bytes.Span[16..].ToArray();

        _encryptionKey = Derive(label: 0x00, byteCount: 16, key, salt);
        _authenticationKey = Derive(label: 0x01, byteCount: 20, key, salt);
        _saltKey = Derive(label: 0x02, byteCount: 14, key, salt);
    }

    /// <summary>
    /// Выведенные сеансовые ключи. Открыто только проверкам.
    /// </summary>
    ///
    /// <remarks>
    /// Ключи сверяются с векторами приложения B.3 RFC 3711 — это единственный
    /// способ убедиться, что вывод сошёлся с чужой реализацией, не поднимая
    /// разговора. Наружу пакета ключевой материал не выходит.
    /// </remarks>
    internal (byte[] Encryption, byte[] Authentication, byte[] Salt) DerivedSessionKeys
        => (_encryptionKey, _authenticationKey, _saltKey);

    /// <summary>
    /// Шифрует содержимое пакета, оставляя заголовок открытым, и добавляет
    /// 80-битный тег HMAC-SHA1.
    /// </summary>
    ///
    /// <remarks>
    /// Индекс пакета выводится из номера и счётчика оборотов. Оборот замечается
    /// по прыжку номера из верхней четверти в нижнюю: номер шестнадцатибитный, и
    /// при 20 мс на пакет через ноль он переходит примерно через двадцать две
    /// минуты разговора.
    /// </remarks>
    public byte[] Protect(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (_lastOutboundSequence is > 0xC000 && packet.SequenceNumber < 0x4000)
        {
            unchecked
            {
                _outboundRolloverCounter++;
            }
        }

        _lastOutboundSequence = packet.SequenceNumber;

        var index = ((ulong)_outboundRolloverCounter << 16) | packet.SequenceNumber;
        var plain = packet.Encoded();
        var headerLength = HeaderLength(plain);

        var encryptedPayload = Crypt(plain.AsSpan(headerLength), packet.Ssrc, index);

        // В подпись входит счётчик оборотов, а в пакет — нет: получатель
        // выводит его сам. Это и есть то, что делает подделку номера
        // бессмысленной — угадать надо и номер, и оборот сразу.
        var authenticated = new byte[headerLength + encryptedPayload.Length + 4];
        plain.AsSpan(0, headerLength).CopyTo(authenticated);
        encryptedPayload.CopyTo(authenticated.AsSpan(headerLength));
        WriteBigEndian(authenticated.AsSpan(headerLength + encryptedPayload.Length), _outboundRolloverCounter);

        var tag = HMACSHA1.HashData(_authenticationKey, authenticated).AsSpan(0, TagByteCount);

        var result = new byte[headerLength + encryptedPayload.Length + TagByteCount];
        plain.AsSpan(0, headerLength).CopyTo(result);
        encryptedPayload.CopyTo(result.AsSpan(headerLength));
        tag.CopyTo(result.AsSpan(headerLength + encryptedPayload.Length));

        return result;
    }

    /// <summary>
    /// Сначала проверяет подпись и окно повторов, и только затем расшифровывает.
    /// </summary>
    ///
    /// <remarks>
    /// Порядок обязателен: неподлинные данные никогда не должны попадать ни в
    /// разбор RTP, ни в звуковой тракт. Расшифровать сперва значило бы пропускать
    /// туда всё, что прислали, — а разбор пакета это уже работа с чужими байтами.
    /// </remarks>
    public RtpPacket Unprotect(ReadOnlySpan<byte> protectedPacket)
    {
        if (protectedPacket.Length < RtpPacket.HeaderByteCount + TagByteCount)
        {
            throw new SrtpException(SrtpFailure.PacketTooShort);
        }

        var encrypted = protectedPacket[..^TagByteCount];
        var receivedTag = protectedPacket[^TagByteCount..];

        var sequence = (ushort)((encrypted[2] << 8) | encrypted[3]);
        var ssrc = ((uint)encrypted[8] << 24) | ((uint)encrypted[9] << 16)
            | ((uint)encrypted[10] << 8) | encrypted[11];

        var index = EstimatedInboundIndex(sequence);
        var rolloverCounter = (uint)(index >> 16);

        var authenticated = new byte[encrypted.Length + 4];
        encrypted.CopyTo(authenticated);
        WriteBigEndian(authenticated.AsSpan(encrypted.Length), rolloverCounter);

        var expectedTag = HMACSHA1.HashData(_authenticationKey, authenticated).AsSpan(0, TagByteCount);
        if (!CryptographicOperations.FixedTimeEquals(receivedTag, expectedTag))
        {
            throw new SrtpException(SrtpFailure.AuthenticationFailed);
        }

        if (IsReplay(index))
        {
            throw new SrtpException(SrtpFailure.ReplayedPacket);
        }

        var headerLength = HeaderLength(encrypted);
        var payload = Crypt(encrypted[headerLength..], ssrc, index);

        var plain = new byte[headerLength + payload.Length];
        encrypted[..headerLength].CopyTo(plain);
        payload.CopyTo(plain.AsSpan(headerLength));

        var packet = RtpPacket.Parse(plain);

        // Окно двигается последним: пакет, не разобравшийся как RTP, не должен
        // закрывать дорогу своему честному повтору.
        Accept(index);

        return packet;
    }

    // MARK: - Индекс и защита от повторов

    /// <summary>
    /// Какому обороту принадлежит пришедший номер.
    /// </summary>
    ///
    /// <remarks>
    /// Оценка по RFC 3711 §3.3.1: получатель не знает счётчика оборотов
    /// отправителя и выводит его из того, насколько далеко номер отстоит от
    /// последнего принятого. Наивное «номер меньше — значит повтор» ломается
    /// ровно на границе: опоздавший 65535 после принятого 0 — это прошлый
    /// оборот, а не повтор, и расшифровать его надо старым индексом.
    /// </remarks>
    private ulong EstimatedInboundIndex(ushort sequence)
    {
        if (_highestInboundIndex is not { } highest)
        {
            return sequence;
        }

        var localSequence = (ushort)highest;
        var guessed = (uint)(highest >> 16);

        if (localSequence < 0x8000)
        {
            if (sequence - localSequence > 0x8000 && guessed > 0)
            {
                guessed--;
            }
        }
        else if (localSequence - 0x8000 > sequence)
        {
            unchecked
            {
                guessed++;
            }
        }

        return ((ulong)guessed << 16) | sequence;
    }

    private bool IsReplay(ulong index)
    {
        if (_highestInboundIndex is not { } highest || index > highest)
        {
            return false;
        }

        var distance = highest - index;

        // Всё, что старше глубины окна, считается повтором без разбора. Это
        // требование RFC 3711 §3.3.2, а не наша экономия: помнить всю историю
        // разговора нельзя, а принимать что попало из далёкого прошлого — значит
        // открыть дорогу повторной отправке.
        return distance >= ReplayWindowDepth || (_replayWindow & (1UL << (int)distance)) != 0;
    }

    private void Accept(ulong index)
    {
        if (_highestInboundIndex is not { } highest)
        {
            _highestInboundIndex = index;
            _replayWindow = 1;
            return;
        }

        if (index <= highest)
        {
            _replayWindow |= 1UL << (int)(highest - index);
            return;
        }

        var shift = index - highest;
        _replayWindow = shift >= ReplayWindowDepth ? 1 : (_replayWindow << (int)shift) | 1;
        _highestInboundIndex = index;
    }

    // MARK: - Криптография RFC 3711

    /// <summary>
    /// AES-CM: то же преобразование на шифрование и на расшифровку.
    /// </summary>
    ///
    /// <remarks>
    /// Счётчик собирается из сеансовой соли, SSRC и индекса пакета — и это
    /// единственное, что не даёт двум пакетам одного потока шифроваться одной
    /// гаммой. Повторить счётчик в потоковом шифре значит выдать оба
    /// открытых текста разом.
    /// </remarks>
    private byte[] Crypt(ReadOnlySpan<byte> input, uint ssrc, ulong packetIndex)
    {
        Span<byte> counter = stackalloc byte[16];
        _saltKey.CopyTo(counter);
        counter[14] = 0;
        counter[15] = 0;

        Span<byte> ssrcBytes = stackalloc byte[4];
        WriteBigEndian(ssrcBytes, ssrc);
        for (var offset = 0; offset < 4; offset++)
        {
            counter[4 + offset] ^= ssrcBytes[offset];
        }

        for (var offset = 0; offset < 6; offset++)
        {
            counter[8 + offset] ^= (byte)(packetIndex >> ((5 - offset) * 8));
        }

        return AesCounterMode(input, _encryptionKey, counter);
    }

    /// <summary>
    /// Вывод сеансового ключа из мастер-ключа: та же гамма AES-CM, только
    /// счётчик собирается из мастер-соли и метки.
    /// </summary>
    private static byte[] Derive(byte label, int byteCount, byte[] key, byte[] salt)
    {
        Span<byte> counter = stackalloc byte[16];
        salt.CopyTo(counter);
        counter[14] = 0;
        counter[15] = 0;
        counter[7] ^= label;

        return AesCounterMode(new byte[byteCount], key, counter);
    }

    /// <summary>
    /// Режим счётчика поверх ECB.
    /// </summary>
    ///
    /// <remarks>
    /// <b>ECB здесь не ошибка, а способ получить блочное шифрование.</b> В .NET
    /// нет AES-CTR — есть CBC, ECB и GCM, — а CTR это и есть «зашифровать
    /// счётчик и сложить с текстом по модулю два». Шифруется только счётчик,
    /// никогда не открытый текст, поэтому известная слабость ECB (одинаковые
    /// блоки дают одинаковый шифротекст) здесь не возникает: счётчик на каждый
    /// блок свой.
    /// </remarks>
    private static byte[] AesCounterMode(ReadOnlySpan<byte> input, byte[] key, Span<byte> counter)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var output = new byte[input.Length];
        Span<byte> stream = stackalloc byte[16];

        for (var offset = 0; offset < input.Length; offset += 16)
        {
            aes.EncryptEcb(counter, stream, PaddingMode.None);

            var length = Math.Min(16, input.Length - offset);
            for (var index = 0; index < length; index++)
            {
                output[offset + index] = (byte)(input[offset + index] ^ stream[index]);
            }

            IncrementCounter(counter);
        }

        return output;
    }

    private static void IncrementCounter(Span<byte> counter)
    {
        for (var index = counter.Length - 1; index >= 0; index--)
        {
            if (++counter[index] != 0)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Длина заголовка RTP вместе со списком источников и расширением.
    /// </summary>
    ///
    /// <remarks>
    /// Считается по самим байтам, а не по разобранному пакету: разбирать надо
    /// после проверки подписи, а знать, где кончается открытый заголовок, — до.
    /// </remarks>
    private static int HeaderLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < RtpPacket.HeaderByteCount)
        {
            throw new SrtpException(SrtpFailure.PacketTooShort);
        }

        if (data[0] >> 6 != RtpPacket.Version)
        {
            throw new SrtpException(SrtpFailure.MalformedHeader);
        }

        var length = RtpPacket.HeaderByteCount + ((data[0] & 0x0F) * 4);
        if (data.Length < length)
        {
            throw new SrtpException(SrtpFailure.MalformedHeader);
        }

        if ((data[0] & 0x10) != 0)
        {
            if (data.Length < length + 4)
            {
                throw new SrtpException(SrtpFailure.MalformedHeader);
            }

            var words = (data[length + 2] << 8) | data[length + 3];
            length += 4 + (words * 4);

            if (data.Length < length)
            {
                throw new SrtpException(SrtpFailure.MalformedHeader);
            }
        }

        return length;
    }

    private static void WriteBigEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }
}
