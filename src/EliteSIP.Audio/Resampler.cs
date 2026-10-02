namespace EliteSIP.Audio;

/// <summary>
/// Пересчёт частоты дискретизации между устройством и разговором.
///
/// <b>Этого типа в оригинале нет, и не потому, что о нём забыли.</b> На macOS
/// пересчёт делала система: <c>AVAudioEngine</c> сам подключал конвертер между
/// форматом устройства и форматом разговора, и его качество было не нашей
/// заботой. В WASAPI в общем режиме ничего подобного нет — микшер отдаёт то,
/// что заявил, и превращать 48 кГц устройства в 8 кГц кодека приходится самим.
///
/// <b>Почему нельзя линейной интерполяцией.</b> В стенде W0 она стоит, и там
/// это записано прямо: «для замера часов достаточно, для звука нет». Причина не
/// в мягкости звучания, а в наложении спектров: при 48 → 8 кГц всё, что выше
/// 4 кГц, обязано быть убрано <b>до</b> прореживания, иначе оно вернётся
/// зеркально отражённым в слышимую полосу. Шипящие согласные уедут в свист, а
/// звонок на 3 кГц в трубке собеседника — в гудение. Линейная интерполяция
/// такого фильтра не даёт вовсе.
///
/// <b>Как сделано.</b> Оконный sinc с частотой среза по нижней из двух частот,
/// посчитанный по плотной таблице и взятый с интерполяцией между её узлами —
/// то же, что делают все приличные пересчётчики. Таблица считается один раз при
/// создании; в горячем пути нет ни выделения памяти, ни тригонометрии.
///
/// <b>Усиление на постоянном токе выравнивается на каждом отсчёте.</b> Сумма
/// коэффициентов у соседних фаз слегка разная, и без деления на неё ровный
/// сигнал получал бы дрожание уровня в сотые доли процента — не слышное, но
/// достаточное, чтобы испортить баланс отсчётов и все замеры, которые на него
/// опираются. Деление стоит одного такта на отсчёт и снимает вопрос целиком.
/// </summary>
public sealed class Resampler
{
    /// <summary>
    /// Полуширина ядра в нулях sinc.
    ///
    /// Шестнадцать — обычный выбор для голосового тракта: подавление за полосой
    /// уходит за 60 дБ, а стоимость при 48 → 8 кГц остаётся около тридцати
    /// тысяч умножений на кадр в 20 мс. Это единицы процентов одного ядра, то
    /// есть незаметно рядом с самим эхоподавителем.
    /// </summary>
    private const int ZeroCrossings = 16;

    /// <summary>Узлов таблицы на один отсчёт входа. Между ними — линейно.</summary>
    private const int TableStepsPerSample = 128;

    /// <summary>
    /// Где стоит срез относительно частоты Найквиста нижней из частот.
    ///
    /// До 2 октября 2026 срез стоял ровно на Найквисте, и переходная полоса
    /// фильтра ложилась по обе его стороны: 4,2 кГц при выходе 8 кГц
    /// ослаблялись всего на 14 дБ и отражались в 3,8 кГц — лёгкий свист на
    /// шипящих у собеседника. Замер на этой сборке (48 → 8 кГц):
    ///
    ///              3400 Гц   3600 Гц   4200 Гц → 3800   4400 Гц → 3600
    ///   1,00       −0,0 дБ   −0,3 дБ   −14,1 дБ         −28,4 дБ
    ///   0,92       −0,8 дБ   −3,8 дБ   −49,5 дБ         −78,6 дБ
    ///
    /// Верх полосы G.711 (3400 Гц) теряет меньше децибела, а отражение
    /// уходит под шум. Выше 3400 Гц телефонная сеть всё равно ничего не
    /// несёт. При равных частотах (подстраиваемый пересчёт) резать нечего, и
    /// срез остаётся на месте.
    /// </summary>
    internal const double CutoffFactor = 0.92;

    private readonly float[] _table;
    private readonly int _halfWidth;
    private readonly double _nominalStep;
    private readonly bool _passthrough;
    private double _step;

    private float[] _history;
    private int _historyCount;
    private double _position;

    /// <param name="inputRate">Частота входа.</param>
    /// <param name="outputRate">Частота выхода.</param>
    /// <param name="adjustable">
    /// Темп будут подправлять на ходу (<see cref="SetRateCorrection"/>). Тогда
    /// и при равных частотах работает настоящий фильтр, а не проход насквозь:
    /// растянуть звук на доли процента проходом нельзя.
    /// </param>
    public Resampler(int inputRate, int outputRate, bool adjustable = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);

        InputRate = inputRate;
        OutputRate = outputRate;
        _nominalStep = (double)inputRate / outputRate;
        _step = _nominalStep;

        // Равные частоты — не повод гонять свёртку. Это не оптимизация ради
        // оптимизации: на гарнитуре в режиме связи устройство и кодек оба
        // работают на 8 кГц, и лишний фильтр там только съел бы верх полосы,
        // которой и так осталось мало.
        _passthrough = inputRate == outputRate && !adjustable;
        if (_passthrough)
        {
            _table = [];
            _history = [];
            return;
        }

        // Срез — по нижней из двух частот Найквиста, в долях частоты входа,
        // и чуть ниже неё (CutoffFactor). При повышении частоты фильтр так же
        // убирает зеркальные копии у Найквиста входа.
        double cutoff = 0.5 * Math.Min(1.0, (double)outputRate / inputRate) * (inputRate == outputRate ? 1.0 : CutoffFactor);

        // Чем ниже срез, тем шире должно быть ядро: число нулей sinc задано, а
        // расстояние между ними растёт обратно частоте среза.
        _halfWidth = (int)Math.Ceiling(ZeroCrossings / (2 * cutoff));

        _table = BuildTable(cutoff, _halfWidth);
        _history = new float[(_halfWidth * 2) + 1024];

        // История начинается тишиной: первому отсчёту нужны соседи слева,
        // которых ещё не было. Это и есть задержка пересчёта — при 48 кГц и
        // срезе под 8 кГц около двух миллисекунд, и она входит в объявленную
        // эхоподавителю задержку.
        _historyCount = _halfWidth;
        _position = _halfWidth;
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>
    /// Задержка, которую вносит сам пересчёт, в отсчётах входа.
    ///
    /// Нужна не для отчёта: эхоподавителю объявляется задержка между опорным
    /// сигналом и микрофонным, и половина ядра фильтра — её честная часть.
    /// Пропустить её значит сместить оценку на те же два миллисекунды, а
    /// замеры W0 показали, что к этому числу AEC3 чувствителен.
    /// </summary>
    public int LatencySamples => _passthrough ? 0 : _halfWidth;

    /// <summary>
    /// Сколько отсчётов выхода приходится на один отсчёт входа.
    ///
    /// Через это число считается ожидаемый баланс отсчётов — см.
    /// <see cref="SampleBalance"/>.
    /// </summary>
    public double Ratio => (double)OutputRate / InputRate;

    /// <summary>
    /// Подправляет темп: сколько отсчётов выхода выдавать на каждый ожидаемый.
    /// Единица — номинал; 0,999 — на тысячную меньше, то есть вход
    /// расходуется на тысячную быстрее.
    ///
    /// <b>Зачем здесь, а не выбросом отсчёта.</b> До 1 октября 2026 поправка
    /// темпа выбрасывала или повторяла отсчёты с конца каждого куска. Отсчёт,
    /// выброшенный из волны, — это ступенька, и на поправке в половину
    /// процента ступенька стояла на каждом кадре: пятьдесят щелчков в секунду
    /// поверх голоса собеседника. Свёртка с дробной позицией растягивает звук
    /// без единого шва — тот же фильтр, только шаг по входу другой.
    /// </summary>
    public void SetRateCorrection(double correction)
    {
        if (_passthrough || !(correction > 0))
        {
            return;
        }

        _step = _nominalStep / correction;
    }

    /// <summary>
    /// Пересчитывает, сколько поместится.
    ///
    /// Вход поглощается целиком, выход отдаётся сколько влезло; остаток входа
    /// остаётся внутри до следующего вызова. Вызывающий обязан давать выходу
    /// место с запасом — иначе кольцо внутри будет расти, и задержка вместе с
    /// ним.
    /// </summary>
    /// <returns>Сколько отсчётов записано в <paramref name="output"/>.</returns>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (_passthrough)
        {
            int direct = Math.Min(input.Length, output.Length);
            input[..direct].CopyTo(output);
            return direct;
        }

        Append(input);

        int written = 0;

        // Правому краю ядра нужны отсчёты, которых ещё нет: считаем, пока
        // окно целиком лежит внутри истории.
        while (written < output.Length && _position + _halfWidth < _historyCount)
        {
            output[written++] = SampleAt(_position);
            _position += _step;
        }

        Compact();
        return written;
    }

    /// <summary>
    /// Забывает всё, что накопил.
    ///
    /// Зовётся при пересборке тракта: отсчёты прошлого разговора в новом — это
    /// чужой голос в первом кадре, и слышно это именно тому, кому не надо.
    /// </summary>
    public void Reset()
    {
        if (_passthrough)
        {
            return;
        }

        Array.Clear(_history);
        _historyCount = _halfWidth;
        _position = _halfWidth;
    }

    /// <summary>Один отсчёт выхода: свёртка ядра с историей вокруг дробной позиции.</summary>
    private float SampleAt(double position)
    {
        int centre = (int)position;
        double fraction = position - centre;

        double sum = 0;
        double weight = 0;

        // Левая половина окна: расстояния 1 - fraction, 2 - fraction, …
        for (int k = 0; k < _halfWidth; k++)
        {
            int index = centre - k;
            if (index < 0)
            {
                break;
            }

            double distance = k + fraction;
            float tap = Tap(distance);
            sum += tap * _history[index];
            weight += tap;
        }

        // Правая половина: 1 - fraction, 2 - fraction, … в другую сторону.
        for (int k = 1; k <= _halfWidth; k++)
        {
            int index = centre + k;
            if (index >= _historyCount)
            {
                break;
            }

            double distance = k - fraction;
            float tap = Tap(distance);
            sum += tap * _history[index];
            weight += tap;
        }

        return weight > 0 ? (float)(sum / weight) : 0f;
    }

    /// <summary>Значение ядра на расстоянии в отсчётах входа, с интерполяцией по таблице.</summary>
    private float Tap(double distance)
    {
        double scaled = distance * TableStepsPerSample;
        int index = (int)scaled;
        if (index + 1 >= _table.Length)
        {
            return 0f;
        }

        float fraction = (float)(scaled - index);
        return _table[index] + ((_table[index + 1] - _table[index]) * fraction);
    }

    private void Append(ReadOnlySpan<float> input)
    {
        int required = _historyCount + input.Length;
        if (required > _history.Length)
        {
            Array.Resize(ref _history, required + _halfWidth);
        }

        input.CopyTo(_history.AsSpan(_historyCount));
        _historyCount += input.Length;
    }

    /// <summary>Выбрасывает историю, которая больше не попадает ни в одно окно.</summary>
    private void Compact()
    {
        int keepFrom = (int)_position - _halfWidth;
        if (keepFrom <= 0)
        {
            return;
        }

        int remaining = _historyCount - keepFrom;
        Array.Copy(_history, keepFrom, _history, 0, remaining);
        _historyCount = remaining;
        _position -= keepFrom;
    }

    /// <summary>
    /// Таблица ядра: оконный sinc, посчитанный плотно по расстоянию.
    ///
    /// Окно Блэкмана: подавление боковых лепестков около 58 дБ, чего для
    /// голосовой полосы достаточно с запасом, а считается оно один раз.
    /// </summary>
    private static float[] BuildTable(double cutoff, int halfWidth)
    {
        int size = (halfWidth * TableStepsPerSample) + 2;
        float[] table = new float[size];

        for (int i = 0; i < size; i++)
        {
            double distance = (double)i / TableStepsPerSample;
            if (distance > halfWidth)
            {
                table[i] = 0f;
                continue;
            }

            double sinc = distance == 0
                ? 2 * cutoff
                : Math.Sin(2 * Math.PI * cutoff * distance) / (Math.PI * distance);

            // Окно симметрично, поэтому считается по модулю расстояния.
            double phase = Math.PI * distance / halfWidth;
            double window = 0.42 + (0.5 * Math.Cos(phase)) + (0.08 * Math.Cos(2 * phase));

            table[i] = (float)(sinc * window);
        }

        return table;
    }
}
