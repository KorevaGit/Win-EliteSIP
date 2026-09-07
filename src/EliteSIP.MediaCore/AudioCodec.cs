namespace EliteSIP.MediaCore;

/// <summary>
/// Аудиокодеки, которые предлагаем в SDP.
///
/// G.711 — обязательный минимум: Asterisk поддерживает ulaw и alaw всегда, без
/// транскодинга и без дополнительных модулей, а сам кодек реализуется таблицей.
/// G.722 добавлен ради полосы: 50–7000 Гц вместо 300–3400 при том же битрейте.
///
/// Оговорка, которую стоит знать до того, как радоваться широкой полосе: она
/// работает только там, где широкая полоса есть на всём пути. Лид, звонящий с
/// городского номера, всё равно приедет через G.711, и Asterisk перекодирует.
/// Выигрыш достаётся внутренним звонкам и очередям.
/// </summary>
public enum AudioCodec
{
    /// <summary>G.711 µ-law.</summary>
    Pcmu,
    /// <summary>G.711 A-law.</summary>
    Pcma,
    /// <summary>G.722, широкая полоса.</summary>
    G722,
}

/// <summary>
/// Свойства кодеков.
///
/// В оригинале это вычисляемые свойства самого перечисления — в Swift они
/// живут прямо в <c>enum</c>. В C# перечисление членов не носит, поэтому
/// расширения; набор и значения те же.
/// </summary>
public static class AudioCodecInfo
{
    /// <summary>
    /// Стандартная длительность RTP-пакета. 20 мс — то, что ждёт Asterisk, и
    /// компромисс между задержкой и накладными расходами на заголовки.
    /// </summary>
    public const int DefaultPacketTimeMilliseconds = 20;

    /// <summary>Все кодеки в порядке предпочтения — аналог <c>CaseIterable</c>.</summary>
    public static IReadOnlyList<AudioCodec> All { get; } =
        [AudioCodec.Pcmu, AudioCodec.Pcma, AudioCodec.G722];

    /// <summary>
    /// Статический payload type по RFC 3551 — для этих трёх он фиксированный,
    /// договариваться о нём в SDP не нужно.
    /// </summary>
    public static byte PayloadType(this AudioCodec codec) => codec switch
    {
        AudioCodec.Pcmu => 0,
        AudioCodec.Pcma => 8,
        AudioCodec.G722 => 9,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    /// <summary>Имя для строки <c>a=rtpmap</c>.</summary>
    public static string SdpName(this AudioCodec codec) => codec switch
    {
        AudioCodec.Pcmu => "PCMU",
        AudioCodec.Pcma => "PCMA",
        AudioCodec.G722 => "G722",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    /// <summary>
    /// Частота часов RTP — то, в чём считаются метки времени в пакетах.
    ///
    /// У G.722 она равна 8000, хотя звук он оцифровывает на 16 000. Это не
    /// опечатка и не наша вольность: ошибка допущена в RFC 1890 и осознанно
    /// оставлена в RFC 3551 §4.5.2 ради совместимости с уже написанным. Кто про
    /// неё не знает, наращивает метку времени вдвое быстрее нужного, и
    /// собеседник слышит либо ускоренную речь, либо тишину — в зависимости от
    /// того, насколько строг его джиттер-буфер.
    /// </summary>
    public static uint RtpClockRate(this AudioCodec codec) => 8000;

    /// <summary>
    /// Настоящая частота дискретизации звука. Именно она задаёт формат
    /// аудиотракта.
    /// </summary>
    public static uint SampleRate(this AudioCodec codec) => codec switch
    {
        AudioCodec.Pcmu or AudioCodec.Pcma => 8000,
        AudioCodec.G722 => 16000,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    public static int ChannelCount(this AudioCodec codec) => 1;

    /// <summary>Широкая полоса — для показа в интерфейсе и в журнале.</summary>
    public static bool IsWideband(this AudioCodec codec) => codec.SampleRate() > 8000;

    /// <summary>Сколько отсчётов звука укладывается в пакет заданной длительности.</summary>
    public static int SampleCount(this AudioCodec codec, int packetTimeMilliseconds) =>
        (int)codec.SampleRate() * packetTimeMilliseconds / 1000;

    /// <summary>
    /// На сколько растёт метка времени RTP за один пакет.
    ///
    /// Отдельно от <see cref="SampleCount"/>, потому что у G.722 это разные
    /// числа: 160 против 320. Смешать их — самая частая ошибка в реализациях
    /// G.722.
    /// </summary>
    public static uint TimestampIncrement(this AudioCodec codec, int packetTimeMilliseconds) =>
        codec.RtpClockRate() * (uint)packetTimeMilliseconds / 1000;

    /// <summary>Сколько байт занимает пакет заданной длительности.</summary>
    public static int ByteCount(this AudioCodec codec, int packetTimeMilliseconds) => codec switch
    {
        // Байт на отсчёт.
        AudioCodec.Pcmu or AudioCodec.Pcma => codec.SampleCount(packetTimeMilliseconds),
        // Байт на пару отсчётов: шесть бит нижней полосы плюс два верхней.
        AudioCodec.G722 => codec.SampleCount(packetTimeMilliseconds) / 2,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    /// <summary>Кодек по статическому payload type, либо <c>null</c> для чужого.</summary>
    public static AudioCodec? FromStaticPayloadType(byte payloadType) => payloadType switch
    {
        0 => AudioCodec.Pcmu,
        8 => AudioCodec.Pcma,
        9 => AudioCodec.G722,
        _ => null,
    };

    /// <summary>
    /// Молчащий кадр в этом кодеке.
    ///
    /// Нужен ровно в одном месте — на первую же дыру, когда повторять ещё
    /// нечего. У G.711 это таблица, у G.722 приходится честно закодировать
    /// нули: постоянного «байта тишины» у него нет, состояние в начале потока
    /// известно, и результат получается воспроизводимым.
    /// </summary>
    public static byte[] SilencePayload(this AudioCodec codec, int packetTimeMilliseconds)
    {
        if (codec is AudioCodec.Pcmu or AudioCodec.Pcma)
        {
            byte[] payload = new byte[codec.ByteCount(packetTimeMilliseconds)];
            Array.Fill(payload, G711.SilenceByte(codec));
            return payload;
        }

        AudioFrameEncoder encoder = new(codec);
        return encoder.Encode(new short[codec.SampleCount(packetTimeMilliseconds)]);
    }
}

/// <summary>
/// Кодер разговора.
///
/// Существует затем, чтобы разница между кодеками без состояния (G.711) и с
/// состоянием (G.722) не расползлась по аудиотракту. Экземпляр принадлежит
/// одному разговору и одному потоку: у G.722 предсказатель обязан идти ровно по
/// той же траектории, что у собеседника, и параллельный доступ эту траекторию
/// ломает.
/// </summary>
public sealed class AudioFrameEncoder(AudioCodec codec)
{
    private readonly G722.Encoder _g722 = new();

    public AudioCodec Codec { get; } = codec;

    public byte[] Encode(ReadOnlySpan<short> samples) => Codec is AudioCodec.G722
        ? _g722.Encode(samples)
        : G711.Encode(samples, Codec);
}

/// <summary>Декодер разговора. Всё, что сказано про кодер, верно и здесь.</summary>
public sealed class AudioFrameDecoder(AudioCodec codec)
{
    private readonly G722.Decoder _g722 = new();

    public AudioCodec Codec { get; } = codec;

    public short[] Decode(ReadOnlySpan<byte> payload) => Codec is AudioCodec.G722
        ? _g722.Decode(payload)
        : G711.Decode(payload, Codec);
}

/// <summary>
/// RFC 4733 telephone-event — так DTMF уезжает внутри RTP.
///
/// Это не аудиокодек: событие не несёт звук, поэтому в <see cref="AudioCodec"/>
/// ему места нет. Payload type динамический, 101 — то, что использует Asterisk
/// по умолчанию при <c>dtmfmode=rfc2833</c>, но в SDP его всё равно надо
/// согласовывать.
/// </summary>
public static class TelephoneEvent
{
    public const byte DefaultPayloadType = 101;
    public const string SdpName = "telephone-event";
    public const uint ClockRate = 8000;

    /// <summary>
    /// Набор событий, которые может прислать или принять телефон:
    /// цифры, звёздочка, решётка и A–D.
    /// </summary>
    public const string SupportedEventRange = "0-16";
}
