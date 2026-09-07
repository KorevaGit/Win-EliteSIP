namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Кодек G.711.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/G711Tests.swift</c>.
/// </summary>
public sealed class G711Tests
{
    public static TheoryData<AudioCodec> Кодеки => new(AudioCodec.Pcmu, AudioCodec.Pcma);

    [Fact]
    public void Тишина_кодируется_каноническими_байтами()
    {
        // 0xFF и 0xD5 — то, чем забивают тишину все реализации G.711.
        // Если здесь разъедется, Asterisk услышит постоянный треск.
        Assert.Equal(0xFF, G711.EncodeMuLaw(0));
        Assert.Equal(0xD5, G711.EncodeALaw(0));
        Assert.Equal(G711.SilenceByte(AudioCodec.Pcmu), G711.EncodeMuLaw(0));
        Assert.Equal(G711.SilenceByte(AudioCodec.Pcma), G711.EncodeALaw(0));
        Assert.Equal(0, G711.DecodeMuLaw(G711.MuLawSilence));

        // В A-law кода точного нуля не существует: 0xD5 и 0x55 — это ±8,
        // ближайшие к нулю уровни шкалы. Проверяем минимальность, не равенство.
        Assert.Equal(8, Math.Abs((int)G711.DecodeALaw(G711.ALawSilence)));
        Assert.Equal(8, Math.Abs((int)G711.DecodeALaw(0x55)));
    }

    [Fact]
    public void Таблицы_совпадают_с_референсными_формулами_на_всей_шкале()
    {
        // Главный риск табличного кодека — не формула, а индекс: сдвиг не той
        // арифметики или смещение мимо на единицу дают правильный звук на одной
        // половине шкалы и хрип на другой. Поэтому сверяем не выборочно, а все
        // 65 536 отсчётов и все 256 кодов.
        for (int raw = short.MinValue; raw <= short.MaxValue; raw++)
        {
            short sample = (short)raw;
            Assert.Equal(G711.ReferenceEncodeMuLaw(sample), G711.EncodeMuLaw(sample));
            Assert.Equal(G711.ReferenceEncodeALaw(sample), G711.EncodeALaw(sample));
        }

        for (int code = 0; code <= byte.MaxValue; code++)
        {
            Assert.Equal(G711.ReferenceDecodeMuLaw((byte)code), G711.DecodeMuLaw((byte)code));
            Assert.Equal(G711.ReferenceDecodeALaw((byte)code), G711.DecodeALaw((byte)code));
        }
    }

    [Theory]
    [MemberData(nameof(Кодеки))]
    public void Кадр_кодируется_сразу_в_байты_и_разбирается_из_них_же(AudioCodec codec)
    {
        // Тракт передаёт кадр байтами в обе стороны, и лишний переход через
        // промежуточный массив на каждом пакете — это выделение памяти на
        // потоке подачи.
        short[] samples = new short[160];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = unchecked((short)((index * 173) - 12_000));
        }

        byte[] encoded = G711.Encode(samples, codec);
        Assert.Equal(160, encoded.Length);

        // Срез из середины большего буфера начинается не с нуля: разбор обязан
        // считать смещение, а не индексы исходного хранилища.
        byte[] padded = [0xAA, 0xBB, .. encoded, 0xCC];
        ReadOnlySpan<byte> slice = padded.AsSpan(2, encoded.Length);
        Assert.Equal(G711.Decode(encoded, codec), G711.Decode(slice, codec));
    }

    [Theory]
    [MemberData(nameof(Кодеки))]
    public void Кодирование_устойчиво_повторный_проход_не_смещает_значение(AudioCodec codec)
    {
        // Проверяем decode → encode → decode, а не побайтовое равенство:
        // у µ-law есть два кода нуля (0x7F и 0xFF), и после первого прохода
        // «минус ноль» законно превращается в обычный ноль. Значение при этом
        // не меняется — именно это и важно, чтобы звук не дрейфовал.
        for (int raw = 0; raw <= byte.MaxValue; raw++)
        {
            short decoded = G711.Decode([(byte)raw], codec)[0];
            byte reencoded = G711.Encode([decoded], codec)[0];
            short again = G711.Decode([reencoded], codec)[0];
            Assert.Equal(decoded, again);
        }
    }

    [Theory]
    [MemberData(nameof(Кодеки))]
    public void Каждый_код_декодируется_в_своё_значение(AudioCodec codec)
    {
        HashSet<short> unique = [];
        for (int raw = 0; raw <= byte.MaxValue; raw++)
        {
            unique.Add(G711.Decode([(byte)raw], codec)[0]);
        }

        // 256 кодов на 255 значений: одно значение (ноль) занято дважды.
        Assert.True(unique.Count >= 255, $"кодек теряет уровни: уникальных значений {unique.Count}");
    }

    [Theory]
    [MemberData(nameof(Кодеки))]
    public void Знак_сохраняется(AudioCodec codec)
    {
        for (int magnitude = 64; magnitude <= 32000; magnitude += 337)
        {
            short positive = G711.Decode(G711.Encode([(short)magnitude], codec), codec)[0];
            short negative = G711.Decode(G711.Encode([(short)-magnitude], codec), codec)[0];
            Assert.True(positive > 0, $"{magnitude} потерял знак");
            Assert.True(negative < 0, $"-{magnitude} потерял знак");
        }
    }

    [Theory]
    [MemberData(nameof(Кодеки))]
    public void Монотонность_рост_входа_не_даёт_падения_выхода(AudioCodec codec)
    {
        short previous = short.MinValue;
        for (int value = -32768; value <= 32767; value += 97)
        {
            short decoded = G711.Decode(G711.Encode([(short)value], codec), codec)[0];
            Assert.True(decoded >= previous, $"провал монотонности на {value}");
            previous = decoded;
        }
    }

    [Theory]
    [MemberData(nameof(Кодеки))]
    public void Отношение_сигнал_шум_на_синусе_не_хуже_30_дБ(AudioCodec codec)
    {
        // Практический критерий качества: G.711 даёт около 38 дБ. Порог 30
        // ловит перепутанные сдвиги и сегменты, но не срабатывает на законной
        // логарифмической ошибке квантования.
        const int SampleCount = 8000;
        double amplitude = 0.8 * short.MaxValue;
        short[] original = new short[SampleCount];
        for (int index = 0; index < SampleCount; index++)
        {
            double phase = 2 * Math.PI * 1000 * index / 8000;
            original[index] = (short)(amplitude * Math.Sin(phase));
        }

        short[] restored = G711.Decode(G711.Encode(original, codec), codec);

        double signalEnergy = 0;
        double noiseEnergy = 0;
        for (int index = 0; index < original.Length; index++)
        {
            double clean = original[index];
            double error = restored[index] - clean;
            signalEnergy += clean * clean;
            noiseEnergy += error * error;
        }

        double snr = 10 * Math.Log10(signalEnergy / noiseEnergy);
        Assert.True(snr > 30, $"SNR {codec.SdpName()} = {snr:F1} дБ");
    }

    [Theory]
    [MemberData(nameof(Кодеки))]
    public void Крайние_значения_не_переполняются(AudioCodec codec)
    {
        // short.MinValue нельзя просто отрицать — это классический источник
        // краха в кодировщиках G.711.
        short[] extremes = [short.MinValue, short.MinValue + 1, -1, 0, 1, short.MaxValue - 1, short.MaxValue];
        foreach (short sample in extremes)
        {
            byte[] encoded = G711.Encode([sample], codec);
            Assert.Single(encoded);
            short decoded = G711.Decode(encoded, codec)[0];
            Assert.True(decoded != 0 || sample == 0 || Math.Abs((int)sample) < 8, $"потеряли {sample}");
        }
    }

    [Fact]
    public void Длина_буфера_сохраняется()
    {
        short[] samples = new short[160];
        Array.Fill(samples, (short)1234);

        foreach (AudioCodec codec in new[] { AudioCodec.Pcmu, AudioCodec.Pcma })
        {
            byte[] encoded = G711.Encode(samples, codec);
            Assert.Equal(160, encoded.Length);
            Assert.Equal(codec.ByteCount(AudioCodecInfo.DefaultPacketTimeMilliseconds), encoded.Length);
            Assert.Equal(samples.Length, G711.Decode(encoded, codec).Length);
        }

        Assert.Empty(G711.Encode([], AudioCodec.Pcmu));
        Assert.Empty(G711.Decode([], AudioCodec.Pcma));
    }
}

/// <summary>
/// Кодек не роняет процесс на чужом кодеке.
///
/// <c>G711.Encode/Decode/SilenceByte</c> на G.722 в оригинале сначала падали.
/// Из боевого кода они недостижимы — <see cref="AudioFrameEncoder"/> и
/// <c>SilencePayload</c> разводят G.722 отдельной веткой, — но проект уже падал
/// ровно здесь: живой <c>sipcheck --answer</c> на лабе, где G.722 идёт первым.
/// Цена ошибки не должна быть крэшем посреди разговора.
/// </summary>
public sealed class G711ForeignCodecTests
{
    [Fact]
    public void Кодирование_G722_через_таблицы_G711_отдаёт_пустоту_а_не_падает() =>
        Assert.Empty(G711.Encode([0, 1000, -1000], AudioCodec.G722));

    [Fact]
    public void Декодирование_G722_через_таблицы_G711_отдаёт_пустоту_а_не_падает() =>
        Assert.Empty(G711.Decode([0x01, 0x02, 0x03], AudioCodec.G722));

    [Fact]
    public void Байт_тишины_для_G722_не_падает_и_не_идёт_в_звук()
    {
        // Значение здесь — заглушка, а не утверждение про G.722: настоящую
        // тишину для него кодирует SilencePayload честным кодером, и именно
        // этот путь проходит боевой код.
        _ = G711.SilenceByte(AudioCodec.G722);

        byte[] payload = AudioCodec.G722.SilencePayload(20);
        Assert.Equal(AudioCodec.G722.ByteCount(20), payload.Length);
    }
}
