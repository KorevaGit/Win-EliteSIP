using System.Text;

namespace EliteSIP.PanelLink;

/// <summary>
/// QR-код для строки привязки: <c>elitesip://pair?s=…&amp;f=…</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// Свой кодировщик, а не пакет: строка одна и короткая (около семидесяти байт,
/// версия 5), а правило проекта — держать свой код там, где его немного и где
/// любой сбой должен читаться. Поддержано ровно нужное: байтовый режим,
/// уровень коррекции M, версии 1–10 (до 213 байт).
/// </para>
/// <para>
/// Алгоритм — по ISO/IEC 18004 в изложении Project Nayuki: сборка данных,
/// Reed–Solomon по блокам, чередование, служебные узоры, укладка змейкой,
/// выбор маски по штрафам. Уровень M — компромисс: экран не мнётся и не
/// пачкается, а код на нём должен читаться с метра телефоном.
/// </para>
/// </remarks>
public sealed class QrCode
{
    private const int MaxVersion = 10;

    // Уровень M, версии 1–10 (индекс 0 не используется).
    private static readonly int[] EccPerBlock = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly int[] BlockCount = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    private QrCode(int version)
    {
        Version = version;
        Size = (version * 4) + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
    }

    /// <summary>Версия: 1–10.</summary>
    public int Version { get; }

    /// <summary>Сторона в модулях, без поля.</summary>
    public int Size { get; }

    /// <summary>Выбранная маска: 0–7.</summary>
    public int Mask { get; private set; }

    /// <summary>Тёмный ли модуль. Вне кода — светлый.</summary>
    public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && _modules[y, x];

    /// <summary>Кодирует строку в UTF-8 байтовым режимом.</summary>
    public static QrCode Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encode(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Кодирует байты.</summary>
    /// <param name="data">Данные.</param>
    /// <param name="forcedMask">Маска для проверок; <c>null</c> — выбрать по штрафам.</param>
    public static QrCode Encode(byte[] data, int? forcedMask = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        var version = 1;
        while (version <= MaxVersion && DataCodewords(version) * 8 < 4 + CountBits(version) + (data.Length * 8))
        {
            version++;
        }

        if (version > MaxVersion)
        {
            throw new ArgumentException($"строка длиннее, чем помещается в версию {MaxVersion}", nameof(data));
        }

        var bits = new List<bool>();
        Append(bits, 0b0100, 4);
        Append(bits, data.Length, CountBits(version));
        foreach (var value in data)
        {
            Append(bits, value, 8);
        }

        var capacity = DataCodewords(version) * 8;
        Append(bits, 0, Math.Min(4, capacity - bits.Count));
        Append(bits, 0, (8 - (bits.Count % 8)) % 8);

        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
        {
            Append(bits, pad, 8);
        }

        var codewords = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
        }

        var qr = new QrCode(version);
        qr.DrawFunctionPatterns();
        qr.DrawCodewords(AddEccAndInterleave(version, codewords));
        qr.ChooseMask(forcedMask);
        return qr;
    }

    /// <summary>Сколько байт данных вмещает версия на уровне M.</summary>
    internal static int DataCodewords(int version)
        => (RawDataModules(version) / 8) - (EccPerBlock[version] * BlockCount[version]);

    /// <summary>Reed–Solomon: байты коррекции для блока данных.</summary>
    internal static byte[] ReedSolomon(byte[] data, int degree)
    {
        var divisor = Generator(degree);
        var result = new byte[degree];

        foreach (var value in data)
        {
            var factor = (byte)(value ^ result[0]);
            Array.Copy(result, 1, result, 0, degree - 1);
            result[degree - 1] = 0;

            for (var i = 0; i < degree; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    /// <summary>15 бит формата: уровень M и маска, с BCH и маской 0x5412.</summary>
    internal static int FormatBits(int mask)
    {
        var data = mask; // уровень M кодируется нулями
        var remainder = data;
        for (var i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        return ((data << 10) | remainder) ^ 0x5412;
    }

    private static int CountBits(int version) => version < 10 ? 8 : 16;

    private static int RawDataModules(int version)
    {
        var result = ((16 * version) + 128) * version + 64;
        if (version >= 2)
        {
            var alignments = (version / 7) + 2;
            result -= ((25 * alignments) - 10) * alignments - 55;
            if (version >= 7)
            {
                result -= 36;
            }
        }

        return result;
    }

    private static void Append(List<bool> bits, int value, int length)
    {
        for (var i = length - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }

    private static byte[] AddEccAndInterleave(int version, byte[] data)
    {
        var blocks = BlockCount[version];
        var ecc = EccPerBlock[version];
        var raw = RawDataModules(version) / 8;
        var shortBlocks = blocks - (raw % blocks);
        var shortLength = raw / blocks;

        var assembled = new List<byte[]>();
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var length = shortLength - ecc + (i < shortBlocks ? 0 : 1);
            var block = data.AsSpan(k, length).ToArray();
            k += length;

            var full = new byte[shortLength + 1];
            block.CopyTo(full, 0);
            ReedSolomon(block, ecc).CopyTo(full, full.Length - ecc);
            assembled.Add(full);
        }

        var result = new List<byte>(raw);
        for (var i = 0; i < assembled[0].Length; i++)
        {
            for (var j = 0; j < assembled.Count; j++)
            {
                // В коротких блоках нет байта данных на позиции shortLength-ecc.
                if (i != shortLength - ecc || j >= shortBlocks)
                {
                    result.Add(assembled[j][i]);
                }
            }
        }

        return [.. result];
    }

    private static byte[] Generator(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;

        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    private static byte Multiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    private static int[] AlignmentPositions(int version)
    {
        if (version == 1)
        {
            return [];
        }

        var count = (version / 7) + 2;
        var step = ((version * 8) + (count * 3) + 5) / ((count * 4) - 4) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, position = (version * 4) + 10; i >= 1; i--, position -= step)
        {
            result[i] = position;
        }

        return result;
    }

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var positions = AlignmentPositions(Version);
        for (var i = 0; i < positions.Length; i++)
        {
            for (var j = 0; j < positions.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == positions.Length - 1) || (i == positions.Length - 1 && j == 0))
                {
                    continue;
                }

                DrawAlignment(positions[i], positions[j]);
            }
        }

        DrawFormatBits(0);
        DrawVersion();
    }

    private void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                var xx = x + dx;
                var yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                {
                    SetFunction(xx, yy, distance != 2 && distance != 4);
                }
            }
        }
    }

    private void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    private void DrawFormatBits(int mask)
    {
        var bits = FormatBits(mask);

        for (var i = 0; i <= 5; i++)
        {
            SetFunction(8, i, Bit(bits, i));
        }

        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++)
        {
            SetFunction(14 - i, 8, Bit(bits, i));
        }

        for (var i = 0; i < 8; i++)
        {
            SetFunction(Size - 1 - i, 8, Bit(bits, i));
        }

        for (var i = 8; i < 15; i++)
        {
            SetFunction(8, Size - 15 + i, Bit(bits, i));
        }

        // Тёмный модуль — всегда тёмный.
        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7)
        {
            return;
        }

        var remainder = Version;
        for (var i = 0; i < 12; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        }

        var bits = (Version << 12) | remainder;
        for (var i = 0; i < 18; i++)
        {
            var bit = Bit(bits, i);
            var a = Size - 11 + (i % 3);
            var b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (var vertical = 0; vertical < Size; vertical++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vertical : vertical;

                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
            }
        }
    }

    private void ChooseMask(int? forced)
    {
        var best = forced ?? 0;

        if (forced is null)
        {
            var lowest = int.MaxValue;
            for (var mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormatBits(mask);
                var penalty = Penalty();
                if (penalty < lowest)
                {
                    lowest = penalty;
                    best = mask;
                }

                ApplyMask(mask); // маска — XOR, второй раз снимает её
            }
        }

        Mask = best;
        ApplyMask(best);
        DrawFormatBits(best);
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var invert = mask switch
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

                if (invert && !_isFunction[y, x])
                {
                    _modules[y, x] = !_modules[y, x];
                }
            }
        }
    }

    /// <summary>Штраф маски по четырём правилам стандарта.</summary>
    private int Penalty()
    {
        var result = 0;

        // Правило 1: серии одного цвета длиной от пяти, по строкам и столбцам.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < Size; a++)
            {
                var run = 1;
                for (var b = 1; b < Size; b++)
                {
                    var same = pass == 0 ? _modules[a, b] == _modules[a, b - 1] : _modules[b, a] == _modules[b - 1, a];
                    if (same)
                    {
                        run++;
                        if (run == 5)
                        {
                            result += 3;
                        }
                        else if (run > 5)
                        {
                            result++;
                        }
                    }
                    else
                    {
                        run = 1;
                    }
                }
            }
        }

        // Правило 2: квадраты 2×2 одного цвета.
        for (var y = 0; y < Size - 1; y++)
        {
            for (var x = 0; x < Size - 1; x++)
            {
                var color = _modules[y, x];
                if (color == _modules[y, x + 1] && color == _modules[y + 1, x] && color == _modules[y + 1, x + 1])
                {
                    result += 3;
                }
            }
        }

        // Правило 3: узор, похожий на поисковый (1:1:3:1:1 со светлым краем).
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < Size; a++)
            {
                for (var b = 0; b + 10 < Size; b++)
                {
                    bool At(int k) => pass == 0 ? _modules[a, b + k] : _modules[b + k, a];

                    var core = At(0) && !At(1) && At(2) && At(3) && At(4) && !At(5) && At(6);
                    var before = !At(7) && !At(8) && !At(9) && !At(10);
                    var core2 = At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10);
                    var after = !At(0) && !At(1) && !At(2) && !At(3);

                    if ((core && before) || (core2 && after))
                    {
                        result += 40;
                    }
                }
            }
        }

        // Правило 4: доля тёмных далеко от половины.
        var dark = 0;
        foreach (var module in _modules)
        {
            if (module)
            {
                dark++;
            }
        }

        var total = Size * Size;
        var k2 = ((Math.Abs((dark * 20) - (total * 10)) + total - 1) / total) - 1;
        result += k2 * 10;

        return result;
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;
}
