namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Кольцо отсчётов.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/SampleRingTests.swift</c>.
/// </summary>
public sealed class SampleRingTests
{
    /// <summary>
    /// Чтение в буфер нужной длины: кольцо пишет ровно так же, как в буфер
    /// вывода, и укорачивать этот путь значит не проверить именно то место, где
    /// ошибка стоит щелчка.
    /// </summary>
    private static (float[] Samples, int Real) Read(SampleRing ring, int count)
    {
        float[] storage = new float[count];
        Array.Fill(storage, float.NaN);
        int real = ring.Read(storage);
        return (storage, real);
    }

    [Fact]
    public void Отдаёт_записанное_в_том_же_порядке()
    {
        SampleRing ring = new(16, 8);
        ring.Write([1, 2, 3, 4]);

        (float[] samples, int real) = Read(ring, 4);
        Assert.Equal<float[]>([1, 2, 3, 4], samples);
        Assert.Equal(4, real);
        Assert.Equal(0, ring.Available);
    }

    [Fact]
    public void Недостающее_добивает_тишиной_а_не_отдаёт_меньше()
    {
        SampleRing ring = new(16, 8);
        ring.Write([1, 2]);

        (float[] samples, int real) = Read(ring, 5);

        // Движок воспримет короткий блок как обрыв.
        Assert.Equal<float[]>([1, 2, 0, 0, 0], samples);

        // По разнице между запрошенным и настоящим и считаются недоборы.
        Assert.Equal(2, real);
    }

    [Fact]
    public void Переживает_переход_через_край_хранилища()
    {
        SampleRing ring = new(8, 4);

        ring.Write([1, 2, 3, 4, 5, 6]);
        Read(ring, 5);

        // Голова уехала на позицию 5, запись пойдёт через край.
        ring.Write([7, 8, 9, 10]);

        (float[] samples, int real) = Read(ring, 5);
        Assert.Equal<float[]>([6, 7, 8, 9, 10], samples);
        Assert.Equal(5, real);
    }

    [Fact]
    public void Не_пишет_сверх_ёмкости()
    {
        SampleRing ring = new(4, 4);

        // Лишнее должно отбрасываться, а не затирать неиграное.
        Assert.Equal(4, ring.Write([1, 2, 3, 4, 5, 6]));
        Assert.Equal(0, ring.FreeSpace);

        (float[] samples, _) = Read(ring, 4);
        Assert.Equal<float[]>([1, 2, 3, 4], samples);
    }

    [Fact]
    public void Недобор_до_цели_показывает_сколько_просить_у_джиттер_буфера()
    {
        SampleRing ring = new(480, 320);
        Assert.Equal(320, ring.Deficit);

        ring.Write(Block(160));
        Assert.Equal(160, ring.Deficit);

        ring.Write(Block(160));
        Assert.Equal(0, ring.Deficit);

        // Сверх цели недобора не бывает.
        ring.Write(Block(160));
        Assert.Equal(0, ring.Deficit);

        static float[] Block(int count)
        {
            float[] samples = new float[count];
            Array.Fill(samples, 0.5f);
            return samples;
        }
    }

    [Fact]
    public void Сброс_возвращает_кольцо_в_исходное_состояние()
    {
        SampleRing ring = new(8, 4);
        ring.Write([1, 2, 3, 4, 5]);
        Read(ring, 2);

        ring.RemoveAll();
        Assert.Equal(0, ring.Available);
        Assert.Equal(8, ring.FreeSpace);

        // После сброса указатели не должны помнить старое место.
        ring.Write([9, 9]);
        (float[] samples, _) = Read(ring, 2);
        Assert.Equal<float[]>([9, 9], samples);
    }
}
