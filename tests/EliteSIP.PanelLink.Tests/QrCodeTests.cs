using System.Text;

namespace EliteSIP.PanelLink.Tests;

/// <summary>
/// QR-код строки привязки.
/// </summary>
///
/// <remarks>
/// Векторы Reed–Solomon и битов формата — опубликованные (ISO/IEC 18004 и
/// разбор thonky.com «HELLO WORLD» 1-M). Остальное — обратное чтение готового
/// кода отдельным кодом: биты формата вычитываются из матрицы, маска
/// снимается, змейка читается заново, блоки раскладываются обратно, и данные с
/// коррекцией обязаны сойтись. Последняя проверка — телефоном по экрану — за
/// человеком.
/// </remarks>
public sealed class QrCodeTests
{
    private const string PairLink =
        "elitesip://pair?s=0123456789abcdef0123456789abcdef&f=0123456789abcdef";

    [Fact]
    public void Reed_Solomon_даёт_опубликованные_байты_коррекции()
    {
        byte[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];

        Assert.Equal(
            new byte[] { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 },
            QrCode.ReedSolomon(data, 10));
    }

    [Theory]
    [InlineData(0, "101010000010010")]
    [InlineData(1, "101000100100101")]
    [InlineData(2, "101111001111100")]
    [InlineData(3, "101101101001011")]
    [InlineData(4, "100010111111001")]
    [InlineData(5, "100000011001110")]
    [InlineData(6, "100111110010111")]
    [InlineData(7, "100101010100000")]
    public void Биты_формата_уровня_M_совпадают_с_таблицей(int mask, string expected)
        => Assert.Equal(Convert.ToInt32(expected, 2), QrCode.FormatBits(mask));

    [Theory]
    [InlineData(1, 16)]
    [InlineData(4, 64)]
    [InlineData(5, 86)]
    [InlineData(7, 124)]
    [InlineData(10, 216)]
    public void Ёмкость_версий_на_уровне_M(int version, int dataCodewords)
        => Assert.Equal(dataCodewords, QrCode.DataCodewords(version));

    [Fact]
    public void Строка_привязки_ложится_в_пятую_версию_со_служебными_узорами()
    {
        var qr = QrCode.Encode(PairLink);

        Assert.Equal(5, qr.Version);
        Assert.Equal(37, qr.Size);

        // Поисковые узоры в трёх углах: тёмная рамка 7×7 и светлый зазор.
        foreach (var (x, y) in new[] { (0, 0), (qr.Size - 7, 0), (0, qr.Size - 7) })
        {
            Assert.True(qr[x, y]);
            Assert.True(qr[x + 6, y + 6]);
            Assert.False(qr[x + 1, y + 1]);
            Assert.True(qr[x + 3, y + 3]);
        }

        // Полосы синхронизации чередуются.
        for (var i = 8; i < qr.Size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, qr[i, 6]);
            Assert.Equal(i % 2 == 0, qr[6, i]);
        }

        // Тёмный модуль.
        Assert.True(qr[8, qr.Size - 8]);
    }

    [Theory]
    [InlineData(PairLink, null)]
    [InlineData(PairLink, 3)]
    [InlineData("HELLO WORLD", null)]
    [InlineData("elitesip://pair?s=" + "ffffffffffffffffffffffffffffffff" + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", null)]
    public void Готовый_код_читается_обратно(string text, int? mask)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var qr = QrCode.Encode(bytes, mask);

        Assert.Equal(bytes, Reader.Read(qr));
    }

    [Fact]
    public void Версия_7_и_выше_несёт_биты_версии()
    {
        var qr = QrCode.Encode(new string('a', 120));

        Assert.True(qr.Version >= 7);
        Assert.Equal(Encoding.ASCII.GetBytes(new string('a', 120)), Reader.Read(qr));
    }

    [Fact]
    public void Слишком_длинная_строка_отвергается()
        => Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 300)));

    /// <summary>Обратное чтение — своей дорогой, не кодом кодировщика.</summary>
    private static class Reader
    {
        private static readonly int[] EccPerBlock = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
        private static readonly int[] BlockCount = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

        internal static byte[] Read(QrCode qr)
        {
            var size = qr.Size;
            var version = (size - 17) / 4;

            // Формат — из первой копии вокруг левого верхнего поискового узора.
            var format = 0;
            int[][] first =
            [
                [8, 0], [8, 1], [8, 2], [8, 3], [8, 4], [8, 5], [8, 7], [8, 8],
                [7, 8], [5, 8], [4, 8], [3, 8], [2, 8], [1, 8], [0, 8],
            ];
            for (var i = 0; i < 15; i++)
            {
                if (qr[first[i][0], first[i][1]])
                {
                    format |= 1 << i;
                }
            }

            format ^= 0x5412;
            Assert.Equal(0, format >> 13); // уровень M
            var mask = (format >> 10) & 7;
            Assert.Equal(qr.Mask, mask);

            var function = FunctionMap(size, version);

            var bits = new List<bool>();
            for (var right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                {
                    right = 5;
                }

                for (var v = 0; v < size; v++)
                {
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var y = ((right + 1) & 2) == 0 ? size - 1 - v : v;
                        if (!function[y, x])
                        {
                            bits.Add(qr[x, y] ^ Masked(mask, x, y));
                        }
                    }
                }
            }

            var raw = bits.Count / 8;
            var all = new byte[raw];
            for (var i = 0; i < raw * 8; i++)
            {
                if (bits[i])
                {
                    all[i >> 3] |= (byte)(1 << (7 - (i & 7)));
                }
            }

            // Раскладка блоков обратно.
            var blocks = BlockCount[version];
            var ecc = EccPerBlock[version];
            var shortBlocks = blocks - (raw % blocks);
            var shortLength = raw / blocks;
            var lengths = Enumerable.Range(0, blocks).Select(b => shortLength + (b < shortBlocks ? 0 : 1)).ToArray();
            var assembled = lengths.Select(l => new byte[l]).ToArray();

            var k = 0;
            for (var i = 0; i < shortLength + 1; i++)
            {
                for (var b = 0; b < blocks; b++)
                {
                    var dataLength = lengths[b] - ecc;
                    int index;
                    if (i < dataLength)
                    {
                        index = i;
                    }
                    else if (i >= shortLength + 1 - ecc)
                    {
                        index = dataLength + (i - (shortLength + 1 - ecc));
                    }
                    else
                    {
                        continue;
                    }

                    assembled[b][index] = all[k++];
                }
            }

            var data = new List<byte>();
            foreach (var block in assembled)
            {
                var dataPart = block[..^ecc];
                Assert.Equal(block[^ecc..], QrCode.ReedSolomon(dataPart, ecc));
                data.AddRange(dataPart);
            }

            // Байтовый режим: 0100, длина, байты.
            var stream = new List<bool>();
            foreach (var value in data)
            {
                for (var i = 7; i >= 0; i--)
                {
                    stream.Add(((value >> i) & 1) != 0);
                }
            }

            var mode = Take(stream, 0, 4);
            Assert.Equal(0b0100, mode);
            var countBits = version < 10 ? 8 : 16;
            var length = Take(stream, 4, countBits);

            var result = new byte[length];
            for (var i = 0; i < length; i++)
            {
                result[i] = (byte)Take(stream, 4 + countBits + (i * 8), 8);
            }

            return result;
        }

        private static int Take(List<bool> stream, int start, int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | (stream[start + i] ? 1 : 0);
            }

            return value;
        }

        private static bool Masked(int mask, int x, int y) => mask switch
        {
            0 => (x + y) % 2 == 0,
            1 => y % 2 == 0,
            2 => x % 3 == 0,
            3 => (x + y) % 3 == 0,
            4 => ((x / 3) + (y / 2)) % 2 == 0,
            5 => (x * y % 2) + (x * y % 3) == 0,
            6 => ((x * y % 2) + (x * y % 3)) % 2 == 0,
            _ => (((x + y) % 2) + (x * y % 3)) % 2 == 0,
        };

        /// <summary>Где служебные модули — по стандарту, заново.</summary>
        private static bool[,] FunctionMap(int size, int version)
        {
            var map = new bool[size, size];

            void Box(int x0, int y0, int w, int h)
            {
                for (var y = y0; y < y0 + h; y++)
                {
                    for (var x = x0; x < x0 + w; x++)
                    {
                        if (x >= 0 && y >= 0 && x < size && y < size)
                        {
                            map[y, x] = true;
                        }
                    }
                }
            }

            Box(0, 0, 9, 9);
            Box(size - 8, 0, 8, 9);
            Box(0, size - 8, 9, 8);
            Box(6, 0, 1, size);
            Box(0, 6, size, 1);

            int[][] alignment =
            [
                [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34],
                [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
            ];
            var positions = alignment[version];
            foreach (var a in positions)
            {
                foreach (var b in positions)
                {
                    if ((a == 6 && b == 6) || (a == 6 && b == positions[^1]) || (a == positions[^1] && b == 6))
                    {
                        continue;
                    }

                    Box(a - 2, b - 2, 5, 5);
                }
            }

            if (version >= 7)
            {
                Box(size - 11, 0, 3, 6);
                Box(0, size - 11, 6, 3);
            }

            return map;
        }
    }
}
