namespace EliteSIP.MediaCore;

/// <summary>
/// Кодеки G.711: µ-law и A-law.
///
/// Алгоритм — референсная реализация из ITU-T G.711 (в виде, известном по
/// public-domain <c>g711.c</c> от Sun Microsystems). Он логарифмический и
/// работает не над полными 16 битами: µ-law квантует 14-битную сетку, A-law —
/// 13-битную, поэтому в кодировщиках стоят сдвиги <c>&gt;&gt; 2</c> и
/// <c>&gt;&gt; 3</c>. Это не потеря точности по недосмотру, а часть стандарта.
///
/// Считает кодек по таблицам, а не по формулам: боевой Asterisk отдаёт
/// <c>(ulaw|alaw|gsm|g726|g722)</c> и выбирает первый, который умеет сам,
/// поэтому каждый разговор идёт на G.711 — это единственный кодек, чей
/// внутренний цикл выполняется постоянно. Референсный кодировщик на каждый
/// отсчёт линейно ищет сегмент по таблице границ; таблица заменяет поиск одним
/// чтением.
///
/// Обоснование перенесено из оригинала целиком: выигрыш там измерялся не
/// скоростью (сорок микросекунд на секунду разговора), а тем, что кадр перестал
/// ходить через промежуточные массивы и цикл кодирования перестал ветвиться.
/// Ровный расход важнее среднего там, где опоздание слышно.
/// </summary>
public static class G711
{
    // Верхние границы сегментов логарифмической шкалы.
    private static readonly int[] MuLawSegmentEnd = [0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF];
    private static readonly int[] ALawSegmentEnd = [0x1F, 0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF];

    private const int MuLawBias = 0x84;
    private const int MuLawClip = 8159;

    /// <summary>
    /// Байт, которым кодируется тишина. Пригодится, когда надо отправить
    /// комфортный шум или заполнить дырку в джиттер-буфере.
    /// </summary>
    public const byte MuLawSilence = 0xFF;

    public const byte ALawSilence = 0xD5;

    private static int Segment(int value, int[] table)
    {
        for (int index = 0; index < table.Length; index++)
        {
            if (value <= table[index])
            {
                return index;
            }
        }

        return table.Length;
    }

    // Референсные формулы.
    //
    // Остаются в коде и остаются доступными: по ним строятся таблицы, и они же
    // служат образцом в тесте. Заменить их таблицами «насовсем» значило бы
    // потерять то, с чем таблицы сверяются, — и первая же опечатка в индексе
    // стала бы невоспроизводимым хрипом в линии.

    internal static byte ReferenceEncodeMuLaw(short sample)
    {
        int value = sample >> 2;
        int mask;
        if (value < 0)
        {
            value = -value;
            mask = 0x7F;
        }
        else
        {
            mask = 0xFF;
        }

        if (value > MuLawClip)
        {
            value = MuLawClip;
        }

        value += MuLawBias >> 2;

        int segment = Segment(value, MuLawSegmentEnd);
        if (segment >= 8)
        {
            return (byte)(0x7F ^ mask);
        }

        int encoded = (segment << 4) | ((value >> (segment + 1)) & 0x0F);
        return (byte)(encoded ^ mask);
    }

    internal static short ReferenceDecodeMuLaw(byte code)
    {
        int value = ~code;
        int magnitude = ((value & 0x0F) << 3) + MuLawBias;
        magnitude <<= (value & 0x70) >> 4;
        int sample = (value & 0x80) != 0 ? MuLawBias - magnitude : magnitude - MuLawBias;
        return unchecked((short)sample);
    }

    internal static byte ReferenceEncodeALaw(short sample)
    {
        int value = sample >> 3;
        int mask;
        if (value >= 0)
        {
            mask = 0xD5;
        }
        else
        {
            mask = 0x55;
            value = -value - 1;
        }

        int segment = Segment(value, ALawSegmentEnd);
        if (segment >= 8)
        {
            return (byte)(0x7F ^ mask);
        }

        int encoded = segment << 4;
        // Первые два сегмента линейные, у них шаг квантования одинаковый.
        encoded |= segment < 2 ? (value >> 1) & 0x0F : (value >> segment) & 0x0F;
        return (byte)(encoded ^ mask);
    }

    internal static short ReferenceDecodeALaw(byte code)
    {
        int value = code ^ 0x55;
        int magnitude = (value & 0x0F) << 4;
        int segment = (value & 0x70) >> 4;
        switch (segment)
        {
            case 0:
                magnitude += 8;
                break;
            case 1:
                magnitude += 0x108;
                break;
            default:
                magnitude += 0x108;
                magnitude <<= segment - 1;
                break;
        }

        return unchecked((short)((value & 0x80) != 0 ? magnitude : -magnitude));
    }

    // Таблицы.
    //
    // Индексом служит не сам отсчёт, а то, что от него остаётся после сдвига, с
    // которого начинается референсный кодировщик: µ-law отбрасывает два младших
    // бита, A-law — три. Поэтому таблицы получаются не по 64 КБ, а по 16 и 8:
    // обе целиком помещаются в кэш первого уровня, и чтение из них стоит
    // дешевле, чем ветвление, которое они заменили.
    //
    // Сдвиг обязательно арифметический — над знаковым типом, а не логический по
    // битовому образцу: у отрицательных отсчётов это разные числа, и на
    // логическом сдвиге таблица разъедется ровно на половине шкалы —
    // собеседник услышит хрип только на громких звуках.

    private const int MuLawIndexBias = 8_192;
    private const int ALawIndexBias = 4_096;

    private static readonly byte[] MuLawEncodeTable = BuildEncodeTable(16_384, MuLawIndexBias, 2, ReferenceEncodeMuLaw);
    private static readonly byte[] ALawEncodeTable = BuildEncodeTable(8_192, ALawIndexBias, 3, ReferenceEncodeALaw);
    private static readonly short[] MuLawDecodeTable = BuildDecodeTable(ReferenceDecodeMuLaw);
    private static readonly short[] ALawDecodeTable = BuildDecodeTable(ReferenceDecodeALaw);

    private static byte[] BuildEncodeTable(int size, int bias, int shift, Func<short, byte> reference)
    {
        byte[] table = new byte[size];
        for (int index = 0; index < size; index++)
        {
            // Обрезание, а не переполнение: крайние индексы соответствуют
            // отсчётам за пределами Int16, и брать их по модулю значило бы
            // положить в таблицу чужой сегмент.
            int sample = Math.Clamp((index - bias) << shift, short.MinValue, short.MaxValue);
            table[index] = reference((short)sample);
        }

        return table;
    }

    private static short[] BuildDecodeTable(Func<byte, short> reference)
    {
        short[] table = new short[256];
        for (int code = 0; code < table.Length; code++)
        {
            table[code] = reference((byte)code);
        }

        return table;
    }

    // Отдельные отсчёты.

    public static byte EncodeMuLaw(short sample) => MuLawEncodeTable[(sample >> 2) + MuLawIndexBias];

    public static short DecodeMuLaw(byte code) => MuLawDecodeTable[code];

    public static byte EncodeALaw(short sample) => ALawEncodeTable[(sample >> 3) + ALawIndexBias];

    public static short DecodeALaw(byte code) => ALawDecodeTable[code];

    // Буферы.
    //
    // Пакет живёт двадцать миллисекунд, и на каждый уходит по одному проходу в
    // каждую сторону. Проход идёт по спанам и выделяет ровно один массив — тот,
    // который уедет в RTP. Лишнее выделение на потоке подачи — это то, что при
    // неудачном стечении обстоятельств слышно как щелчок.

    /// <summary>Кодирует кадр сразу в байты — в том виде, в каком он уедет в RTP.</summary>
    public static byte[] Encode(ReadOnlySpan<short> samples, AudioCodec codec)
    {
        byte[] table;
        int shift;
        int bias;
        switch (codec)
        {
            case AudioCodec.Pcmu:
                table = MuLawEncodeTable;
                shift = 2;
                bias = MuLawIndexBias;
                break;
            case AudioCodec.Pcma:
                table = ALawEncodeTable;
                shift = 3;
                bias = ALawIndexBias;
                break;
            default:
                // Пустота, а не исключение. Сюда не приходят: и
                // AudioFrameEncoder, и SilencePayload разводят G.722 отдельной
                // веткой, а прямых вызовов из боевого кода нет. Но проект уже
                // падал ровно здесь — на живом sipcheck --answer, где G.722 идёт
                // первым, — и цена ошибки не должна быть падением процесса
                // посреди разговора. Пустой кадр слышен как заминка, и это
                // несравнимо дешевле.
                return [];
        }

        byte[] encoded = new byte[samples.Length];
        for (int index = 0; index < samples.Length; index++)
        {
            encoded[index] = table[(samples[index] >> shift) + bias];
        }

        return encoded;
    }

    /// <summary>Разбирает пришедший кадр в отсчёты.</summary>
    public static short[] Decode(ReadOnlySpan<byte> payload, AudioCodec codec)
    {
        short[] table;
        switch (codec)
        {
            case AudioCodec.Pcmu: table = MuLawDecodeTable; break;
            case AudioCodec.Pcma: table = ALawDecodeTable; break;
            // Пустота по той же причине, что и в Encode: тишина вместо крэша.
            default: return [];
        }

        short[] samples = new short[payload.Length];
        for (int index = 0; index < payload.Length; index++)
        {
            samples[index] = table[payload[index]];
        }

        return samples;
    }

    public static byte SilenceByte(AudioCodec codec) => codec switch
    {
        AudioCodec.Pcmu => MuLawSilence,
        AudioCodec.Pcma => ALawSilence,
        // У G.722 постоянного байта тишины нет — это ADPCM с состоянием, и нули
        // дают щелчки, а не тишину. Правильный путь — SilencePayload, который
        // кодирует нули честным кодером. Здесь остаётся байт G.711 как заведомо
        // безвредная заглушка: заявлением о том, что у G.722 тишина такая, он не
        // является, и звук через него не идёт.
        _ => MuLawSilence,
    };
}
