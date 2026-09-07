namespace EliteSIP.MediaCore;

/// <summary>
/// G.722 — субполосный АДИКМ по рекомендации ITU-T.
///
/// Зачем он нужен: полоса 50–7000 Гц вместо 300–3400 у G.711 при том же
/// битрейте 64 кбит/с. Разница на слух — та же, что между телефоном и радио:
/// возвращаются шипящие и различимость близких согласных.
///
/// Как устроен. Входные 16 кГц квадратурный фильтр делит на две полосы по
/// 8 кГц: нижнюю 0–4 кГц и верхнюю 4–8 кГц. Нижняя кодируется шестью битами на
/// отсчёт, верхняя двумя. На каждые два входных отсчёта выходит один байт —
/// отсюда и 64 кбит/с, и то, что пакет в 20 мс занимает те же 160 байт, что у
/// G.711.
///
/// <b>Кодек с состоянием, в отличие от G.711.</b> Предсказатель и квантователь
/// подстраиваются на каждом отсчёте, и кодер с декодером обязаны идти по одной
/// и той же траектории. Практические следствия: экземпляр кодера привязан к
/// разговору и не переживает его; декодировать кадры не подряд или пропустив
/// один — значит разойтись с отправителем и получить хрип, который сам собой
/// затухает лишь через десятки миллисекунд. Поэтому потерянный кадр всё равно
/// надо чем-то заполнять, а не пропускать.
///
/// Реализация следует блочной структуре рекомендации, названия блоков (UPPOL2,
/// FILTEZ, SCALEL и прочие) сохранены в комментариях — иначе этот код
/// невозможно сверить с текстом стандарта.
/// </summary>
public static class G722
{
    /// <summary>
    /// Коэффициенты квадратурного зеркального фильтра, 24 отвода. Симметричны,
    /// поэтому хранится половина.
    ///
    /// Развязка чётных и нечётных отводов — <c>x[2i]</c> с прямым индексом
    /// коэффициента, <c>x[2i+1]</c> с обратным — выглядит произвольной, и
    /// перепутать её ничего не стоит. Перепутанная не ломает кодек заметно:
    /// полоса ниже 1 кГц проходит почти без потерь, зато на 3 и 5 кГц
    /// появляется провал в 13,8 дБ. На слух это «глухой» голос, а не поломка,
    /// поэтому проверяется отдельным тестом на ровность восстановления.
    /// </summary>
    private static readonly int[] QmfCoefficients =
        [3, -11, 12, 32, -210, 951, 3876, -805, 362, -156, 53, -11];

    // Таблицы квантователей и адаптации из рекомендации. Смысла по отдельности
    // не имеют, менять нельзя.

    private static readonly int[] Q6 =
    [
        0, 35, 72, 110, 150, 190, 233, 276,
        323, 370, 422, 473, 530, 587, 650, 714,
        786, 858, 940, 1023, 1121, 1219, 1339, 1458,
        1612, 1765, 1980, 2195, 2557, 2919, 0, 0,
    ];

    private static readonly int[] Iln =
    [
        0, 63, 62, 31, 30, 29, 28, 27,
        26, 25, 24, 23, 22, 21, 20, 19,
        18, 17, 16, 15, 14, 13, 12, 11,
        10, 9, 8, 7, 6, 5, 4, 0,
    ];

    private static readonly int[] Ilp =
    [
        0, 61, 60, 59, 58, 57, 56, 55,
        54, 53, 52, 51, 50, 49, 48, 47,
        46, 45, 44, 43, 42, 41, 40, 39,
        38, 37, 36, 35, 34, 33, 32, 0,
    ];

    private static readonly int[] Wl = [-60, -30, 58, 172, 334, 538, 1198, 3042];

    private static readonly int[] Rl42 = [0, 7, 6, 5, 4, 3, 2, 1, 7, 6, 5, 4, 3, 2, 1, 0];

    private static readonly int[] Ilb =
    [
        2048, 2093, 2139, 2186, 2233, 2282, 2332, 2383,
        2435, 2489, 2543, 2599, 2656, 2714, 2774, 2834,
        2896, 2960, 3025, 3091, 3158, 3228, 3298, 3371,
        3444, 3520, 3597, 3676, 3756, 3838, 3922, 4008,
    ];

    private static readonly int[] Qm4 =
    [
        0, -20456, -12896, -8968,
        -6288, -4240, -2584, -1200,
        20456, 12896, 8968, 6288,
        4240, 2584, 1200, 0,
    ];

    private static readonly int[] Qm2 = [-7408, -1616, 7408, 1616];

    private static readonly int[] Qm6 =
    [
        -136, -136, -136, -136,
        -24808, -21904, -19008, -16704,
        -14984, -13512, -12280, -11192,
        -10232, -9360, -8576, -7856,
        -7192, -6576, -6000, -5456,
        -4944, -4464, -4008, -3576,
        -3168, -2776, -2400, -2032,
        -1688, -1360, -1040, -728,
        24808, 21904, 19008, 16704,
        14984, 13512, 12280, 11192,
        10232, 9360, 8576, 7856,
        7192, 6576, 6000, 5456,
        4944, 4464, 4008, 3576,
        3168, 2776, 2400, 2032,
        1688, 1360, 1040, 728,
        432, 136, -432, -136,
    ];

    private static readonly int[] Ihn = [0, 1, 0];
    private static readonly int[] Ihp = [0, 3, 2];
    private static readonly int[] Wh = [0, -214, 798];
    private static readonly int[] Rh2 = [2, 1, 2, 1];

    private static int Saturate(int value)
    {
        if (value > 32767)
        {
            return 32767;
        }

        if (value < -32768)
        {
            return -32768;
        }

        return value;
    }

    /// <summary>Состояние одной полосы: предсказатель и масштаб квантователя.</summary>
    private sealed class Band
    {
        public int S;
        public int Sp;
        public int Sz;
        public int Nb;

        /// <summary>
        /// Начальный масштаб задан рекомендацией. Ноль здесь означал бы, что
        /// первые отсчёты декодируются в тишину независимо от входа.
        /// </summary>
        public int Det = 32;

        private readonly int[] _r = new int[3];
        private readonly int[] _a = new int[3];
        private readonly int[] _ap = new int[3];
        private readonly int[] _p = new int[3];
        private readonly int[] _d = new int[7];
        private readonly int[] _b = new int[7];
        private readonly int[] _bp = new int[7];
        private readonly int[] _sg = new int[7];

        /// <summary>
        /// Блок 4 рекомендации: пересчёт предсказателя по новой невязке. Общий
        /// для обеих полос и для обеих сторон — кодер и декодер обязаны
        /// выполнять его одинаково, иначе их состояния разойдутся.
        /// </summary>
        public void Update(int dx)
        {
            // RECONS и PARREC
            _d[0] = dx;
            _r[0] = Saturate(S + dx);
            _p[0] = Saturate(Sz + dx);

            // UPPOL2
            for (int index = 0; index < 3; index++)
            {
                _sg[index] = _p[index] >> 15;
            }

            int wd1 = Saturate(_a[1] * 4);
            int wd2 = _sg[0] == _sg[1] ? -wd1 : wd1;
            if (wd2 > 32767)
            {
                wd2 = 32767;
            }

            int wd3 = _sg[0] == _sg[2] ? 128 : -128;
            wd3 += wd2 >> 7;
            wd3 += (_a[2] * 32512) >> 15;
            wd3 = Math.Clamp(wd3, -12288, 12288);
            _ap[2] = wd3;

            // UPPOL1
            _sg[0] = _p[0] >> 15;
            _sg[1] = _p[1] >> 15;
            wd1 = _sg[0] == _sg[1] ? 192 : -192;
            wd2 = (_a[1] * 32640) >> 15;
            _ap[1] = Saturate(wd1 + wd2);
            wd3 = Saturate(15360 - _ap[2]);
            _ap[1] = Math.Clamp(_ap[1], -wd3, wd3);

            // UPZERO
            wd1 = dx == 0 ? 0 : 128;
            _sg[0] = dx >> 15;
            for (int index = 1; index < 7; index++)
            {
                _sg[index] = _d[index] >> 15;
                wd2 = _sg[index] == _sg[0] ? wd1 : -wd1;
                wd3 = (_b[index] * 32640) >> 15;
                _bp[index] = Saturate(wd2 + wd3);
            }

            // DELAYA
            for (int index = 6; index > 0; index--)
            {
                _d[index] = _d[index - 1];
                _b[index] = _bp[index];
            }

            for (int index = 2; index > 0; index--)
            {
                _r[index] = _r[index - 1];
                _p[index] = _p[index - 1];
                _a[index] = _ap[index];
            }

            // FILTEP
            wd1 = Saturate(_r[1] + _r[1]);
            wd1 = (_a[1] * wd1) >> 15;
            wd2 = Saturate(_r[2] + _r[2]);
            wd2 = (_a[2] * wd2) >> 15;
            Sp = Saturate(wd1 + wd2);

            // FILTEZ
            int sum = 0;
            for (int index = 6; index > 0; index--)
            {
                int doubled = Saturate(_d[index] + _d[index]);
                sum += (_b[index] * doubled) >> 15;
            }

            Sz = Saturate(sum);

            // PREDIC
            S = Saturate(Sp + Sz);
        }

        /// <summary>Блоки 3 нижней полосы: LOGSCL и SCALEL.</summary>
        public void ScaleLow(int index)
        {
            int wd1 = (Nb * 127) >> 7;
            wd1 += Wl[Rl42[index]];
            Nb = Math.Clamp(wd1, 0, 18432);

            int position = (Nb >> 6) & 31;
            int shift = 8 - (Nb >> 11);
            int scaled = shift < 0 ? Ilb[position] << -shift : Ilb[position] >> shift;
            Det = scaled << 2;
        }

        /// <summary>Блоки 3 верхней полосы: LOGSCH и SCALEH.</summary>
        public void ScaleHigh(int index)
        {
            int wd1 = (Nb * 127) >> 7;
            wd1 += Wh[Rh2[index]];
            Nb = Math.Clamp(wd1, 0, 22528);

            int position = (Nb >> 6) & 31;
            int shift = 10 - (Nb >> 11);
            int scaled = shift < 0 ? Ilb[position] << -shift : Ilb[position] >> shift;
            Det = scaled << 2;
        }
    }

    /// <summary>
    /// Кодер G.722. Живёт ровно столько, сколько разговор: состояние
    /// предсказателя нельзя ни переиспользовать между звонками, ни сбросить
    /// посреди потока.
    ///
    /// В оригинале это структура с мутирующим методом; здесь класс. Разница
    /// существенная и в пользу класса: значимый тип с состоянием предсказателя
    /// копировался бы при каждой передаче, и разговор незаметно поехал бы по
    /// двум разным траекториям.
    /// </summary>
    public sealed class Encoder
    {
        private readonly Band _low = new();
        private readonly Band _high = new();

        /// <summary>Окно квадратурного фильтра: 24 последних отсчёта.</summary>
        private readonly int[] _window = new int[24];

        /// <summary>
        /// Кодирует отсчёты 16 кГц. Их количество обязано быть чётным: один байт
        /// приходится ровно на пару.
        /// </summary>
        public byte[] Encode(ReadOnlySpan<short> samples)
        {
            byte[] output = new byte[samples.Length / 2];

            int index = 0;
            int written = 0;
            while (index + 1 < samples.Length)
            {
                // Квадратурный фильтр анализа: пара отсчётов на входе даёт по
                // одному отсчёту в каждой полосе.
                for (int position = 0; position < 22; position++)
                {
                    _window[position] = _window[position + 2];
                }

                _window[22] = samples[index];
                _window[23] = samples[index + 1];
                index += 2;

                int sumEven = 0;
                int sumOdd = 0;
                for (int tap = 0; tap < 12; tap++)
                {
                    sumOdd += _window[2 * tap] * QmfCoefficients[tap];
                    sumEven += _window[(2 * tap) + 1] * QmfCoefficients[11 - tap];
                }

                int xLow = Saturate((sumEven + sumOdd) >> 14);
                int xHigh = Saturate((sumEven - sumOdd) >> 14);

                output[written++] = (byte)EncodePair(xLow, xHigh);
            }

            return output;
        }

        private int EncodePair(int xLow, int xHigh)
        {
            // Нижняя полоса: шестибитный квантователь.
            int errorLow = Saturate(xLow - _low.S);
            int magnitude = errorLow >= 0 ? errorLow : -(errorLow + 1);
            int step = 1;
            while (step < 30)
            {
                if (magnitude < (Q6[step] * _low.Det) >> 12)
                {
                    break;
                }

                step++;
            }

            int indexLow = errorLow < 0 ? Iln[step] : Ilp[step];

            int quarter = indexLow >> 2;
            int deltaLow = (_low.Det * Qm4[quarter]) >> 15;
            _low.ScaleLow(quarter);
            _low.Update(deltaLow);

            // Верхняя полоса: двухбитный квантователь.
            int errorHigh = Saturate(xHigh - _high.S);
            int magnitudeHigh = errorHigh >= 0 ? errorHigh : -(errorHigh + 1);
            int threshold = (564 * _high.Det) >> 12;
            int level = magnitudeHigh >= threshold ? 2 : 1;
            int indexHigh = errorHigh < 0 ? Ihn[level] : Ihp[level];

            int deltaHigh = (_high.Det * Qm2[indexHigh]) >> 15;
            _high.ScaleHigh(indexHigh);
            _high.Update(deltaHigh);

            return (indexHigh << 6) | indexLow;
        }
    }

    /// <summary>Декодер G.722. Всё, что сказано про состояние кодера, верно и здесь.</summary>
    public sealed class Decoder
    {
        private readonly Band _low = new();
        private readonly Band _high = new();
        private readonly int[] _window = new int[24];

        public short[] Decode(ReadOnlySpan<byte> payload)
        {
            short[] output = new short[payload.Length * 2];
            int written = 0;

            foreach (byte value in payload)
            {
                int code = value;
                int indexLow = code & 0x3F;
                int indexHigh = (code >> 6) & 0x03;

                // Нижняя полоса восстанавливается по шестибитной таблице, а
                // предсказатель обновляется по четырёхбитной — так в
                // рекомендации, и это не описка: адаптация намеренно грубее
                // восстановления.
                int restoredLow = Math.Clamp(
                    _low.S + ((_low.Det * Qm6[indexLow]) >> 15), -16384, 16383);
                int quarter = indexLow >> 2;
                int deltaLow = (_low.Det * Qm4[quarter]) >> 15;
                _low.ScaleLow(quarter);
                _low.Update(deltaLow);

                int deltaHigh = (_high.Det * Qm2[indexHigh]) >> 15;
                int restoredHigh = Math.Clamp(_high.S + deltaHigh, -16384, 16383);
                _high.ScaleHigh(indexHigh);
                _high.Update(deltaHigh);

                // Квадратурный фильтр синтеза: две полосы обратно в пару
                // отсчётов 16 кГц.
                for (int position = 0; position < 22; position++)
                {
                    _window[position] = _window[position + 2];
                }

                _window[22] = restoredLow + restoredHigh;
                _window[23] = restoredLow - restoredHigh;

                int sumEven = 0;
                int sumOdd = 0;
                for (int tap = 0; tap < 12; tap++)
                {
                    sumOdd += _window[2 * tap] * QmfCoefficients[tap];
                    sumEven += _window[(2 * tap) + 1] * QmfCoefficients[11 - tap];
                }

                output[written++] = (short)Saturate(sumEven >> 11);
                output[written++] = (short)Saturate(sumOdd >> 11);
            }

            return output;
        }
    }
}
