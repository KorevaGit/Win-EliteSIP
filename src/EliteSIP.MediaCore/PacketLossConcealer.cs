namespace EliteSIP.MediaCore;

/// <summary>
/// Сокрытие потерь в области отсчётов — по мотивам ITU-T G.711 Appendix I.
///
/// <b>Зачем отдельный тип.</b> Раньше дырка затыкалась повтором
/// <i>закодированного</i> кадра с затуханием 0,6 за кадр. Для одиночной потери
/// этого хватало, но повтор двадцатимиллисекундного куска — это повтор куска,
/// никак не связанного с периодом голоса: шов приходится на случайную фазу
/// основного тона, и на слух получается «бульканье», а не продолжение звука.
/// Дальше третьего кадра затухание уводило всё в тишину, то есть на 60 мс
/// потери оператор просто терял слог.
///
/// Appendix I решает это иначе: находит период основного тона в уже
/// прозвучавшем и продолжает сигнал ровно этим периодом. Шов ложится в фазу,
/// голос сохраняет и высоту, и тембр, и короткая потеря перестаёт читаться на
/// слух вообще.
///
/// <b>Почему это работа под G.711.</b> Приложение I написано именно для него, и
/// не случайно: у G.711 нет собственного состояния, повторять нечего, кроме
/// самого звука, — а восстановить утраченный кадр по предыдущим для кодека без
/// предсказателя можно только так.
///
/// Работает над отсчётами, поэтому кодеку безразличен и одинаково годится
/// G.722 — там он тоже лучше повтора, хотя декодер после дырки всё равно
/// приходит в себя сам.
///
/// <b>Задержки не добавляет.</b> Канонический Appendix I задерживает весь тракт
/// на 3,75 мс, чтобы сшивать возврат к настоящему звуку с обеих сторон. Мы
/// платить задержкой не готовы — при норме G.114 в 150 мс на весь путь каждая
/// миллисекунда на счету, — поэтому возврат сшивается только вперёд: синтез
/// продолжается ещё несколько миллисекунд и смешивается с началом пришедшего
/// кадра. Щелчок это убирает так же, а стоит нисколько.
///
/// Тип синхронный и без зависимостей от сети и звуковой карты: проверяется
/// целиком тестом.
/// </summary>
public sealed class PacketLossConcealer
{
    // Настройки шкалы.
    //
    // Все пороги заданы в миллисекундах и переводятся в отсчёты по частоте
    // кодека. Иначе на G.722 период основного тона искался бы вдвое ниже, чем
    // надо, и мужской голос синтезировался бы женским.

    /// <summary>
    /// Сколько прозвучавшего держим для анализа. 48 мс — из Appendix I: это
    /// чуть больше двух самых длинных периодов основного тона.
    /// </summary>
    private readonly int _historyCount;

    /// <summary>
    /// Самый короткий период основного тона, который ищем: 400 Гц. Выше — уже
    /// не тон голоса, а шипящая.
    /// </summary>
    private readonly int _minimumPeriod;

    /// <summary>Самый длинный: 66 Гц. Ниже мужской голос не опускается.</summary>
    private readonly int _maximumPeriod;

    /// <summary>Длина сшивки на возврате к настоящему звуку.</summary>
    private readonly int _recoveryBlend;

    /// <summary>
    /// Сколько отсчётов синтез звучит в полную силу, прежде чем начать
    /// затухать. До 10 мс повтор периода неотличим от продолжения речи.
    /// </summary>
    private readonly int _fullGainSamples;

    /// <summary>
    /// Сколько занимает уход в тишину. К 60 мс потери продолжать нечего: голос
    /// за это время успевает смениться, и синтез из правдоподобного становится
    /// гудком.
    /// </summary>
    private readonly int _fadeSamples;

    /// <summary>
    /// Кольцо прозвучавшего. Пишется на каждом настоящем кадре, читается на
    /// первом потерянном.
    /// </summary>
    private readonly short[] _history;

    private int _historyFilled;

    /// <summary>
    /// Продолжение сигнала — копия последнего периода, из которой синтез
    /// вычитывается по кругу.
    /// </summary>
    private short[] _pattern = [];

    private int _patternCursor;

    /// <summary>
    /// Сколько отсчётов уже синтезировано в текущей серии. По нему считается
    /// затухание, поэтому счётчик именно в отсчётах, а не в кадрах: кадр может
    /// быть любой длины.
    /// </summary>
    private int _concealedSamples;

    /// <summary>Синтез, оставшийся на сшивку с первым настоящим кадром после потери.</summary>
    private short[] _pendingBlend = [];

    public PacketLossConcealer(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _historyCount = Samples(48);
        _minimumPeriod = Samples(2.5);
        _maximumPeriod = Samples(15);
        _recoveryBlend = Samples(2);
        _fullGainSamples = Samples(10);
        _fadeSamples = Samples(50);
        _history = new short[_historyCount];

        int Samples(double milliseconds) =>
            Math.Max(1, (int)Math.Round(sampleRate * milliseconds / 1000, MidpointRounding.AwayFromZero));
    }

    public PacketLossConcealer(AudioCodec codec)
        : this((int)codec.SampleRate())
    {
    }

    /// <summary>
    /// Период, которым продолжаем сигнал, в отсчётах. Ищется один раз на серию
    /// потерь: искать заново на каждом кадре значит менять высоту голоса
    /// посреди дырки. Ноль — серии нет.
    ///
    /// Наружу открыт для диагностики и для теста: «синтез звучит похоже» —
    /// свойство, которое легко проходит и при вдвое ошибочном периоде, а ошибка
    /// вдвое — это голос, поднявшийся на октаву.
    /// </summary>
    public int EstimatedPeriod { get; private set; }

    /// <summary>
    /// Принимает прозвучавший кадр.
    ///
    /// Возвращает его же — но если перед ним была потеря, начало кадра сшито с
    /// хвостом синтеза. Возврат, а не изменение на месте: вызывающему видно,
    /// что отдавать в кольцо надо именно результат.
    /// </summary>
    public short[] Receive(ReadOnlySpan<short> samples)
    {
        short[] result = samples.ToArray();

        if (_pendingBlend.Length > 0)
        {
            // Линейная сшивка: синтез уходит, настоящий звук приходит. Оба
            // сигнала в фазе — синтез продолжает ровно тот же период, — поэтому
            // двух миллисекунд достаточно, чтобы шва не было слышно.
            int count = Math.Min(_pendingBlend.Length, result.Length);
            for (int index = 0; index < count; index++)
            {
                float weight = (float)(index + 1) / (count + 1);
                float blended = (_pendingBlend[index] * (1 - weight)) + (result[index] * weight);
                result[index] = Clamp(blended);
            }

            _pendingBlend = [];
        }

        Remember(result);
        EstimatedPeriod = 0;
        _pattern = [];
        _concealedSamples = 0;
        return result;
    }

    /// <summary>Синтезирует кадр взамен потерянного.</summary>
    public short[] Conceal(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        // Пока кольцо не заполнено целиком, продолжать нечего: корреляция
        // против ещё не записанных нулей нашла бы период где угодно. Это первые
        // 48 мс разговора — потеря в них закрывается тишиной, и это честнее
        // синтеза из ничего.
        if (_historyFilled < _historyCount)
        {
            _concealedSamples += count;
            return new short[count];
        }

        if (_pattern.Length == 0)
        {
            EstimatedPeriod = EstimatePeriod();
            _pattern = _history[^EstimatedPeriod..];
            _patternCursor = 0;
        }

        short[] output = new short[count];
        for (int index = 0; index < count; index++)
        {
            float gain = Attenuation(_concealedSamples + index);
            output[index] = Clamp(_pattern[_patternCursor] * gain);
            _patternCursor = (_patternCursor + 1) % _pattern.Length;
        }

        _concealedSamples += count;

        // Хвост на сшивку берём из продолжения того же периода, а не из уже
        // отданного куска: тогда на возврате синтез и настоящий звук идут в
        // одной фазе, и складывать их можно напрямую.
        _pendingBlend = new short[_recoveryBlend];
        for (int offset = 0; offset < _recoveryBlend; offset++)
        {
            float gain = Attenuation(_concealedSamples + offset);
            _pendingBlend[offset] = Clamp(_pattern[(_patternCursor + offset) % _pattern.Length] * gain);
        }

        Remember(output);
        return output;
    }

    public void Reset()
    {
        Array.Clear(_history);
        _historyFilled = 0;
        EstimatedPeriod = 0;
        _pattern = [];
        _patternCursor = 0;
        _concealedSamples = 0;
        _pendingBlend = [];
    }

    private static short Clamp(float value) =>
        (short)Math.Clamp((int)MathF.Round(value, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);

    /// <summary>Кладёт кадр в кольцо прозвучавшего.</summary>
    private void Remember(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        if (samples.Length >= _historyCount)
        {
            samples[^_historyCount..].CopyTo(_history);
            _historyFilled = _historyCount;
            return;
        }

        _history.AsSpan(samples.Length).CopyTo(_history);
        samples.CopyTo(_history.AsSpan(_historyCount - samples.Length));
        _historyFilled = Math.Min(_historyFilled + samples.Length, _historyCount);
    }

    /// <summary>Громкость синтеза на заданном отсчёте серии.</summary>
    private float Attenuation(int offset)
    {
        if (offset < _fullGainSamples)
        {
            return 1;
        }

        int faded = offset - _fullGainSamples;
        return faded >= _fadeSamples ? 0 : 1 - ((float)faded / _fadeSamples);
    }

    /// <summary>
    /// Ищет период основного тона нормированной взаимной корреляцией.
    ///
    /// Сравнивается последний кусок длиной с самый длинный допустимый период —
    /// с такими же кусками, отстоящими назад на все допустимые периоды.
    /// Нормировка на энергию обязательна: без неё побеждает не самый похожий
    /// кусок, а самый громкий, и на затухающем звуке период всегда получается
    /// минимальным.
    ///
    /// Окно взято длиной с максимальный период, а не с минимальный: короткое
    /// окно одинаково хорошо ложится и на период, и на его половину, а ошибка
    /// вдвое — это голос, поднявшийся на октаву.
    /// </summary>
    private int EstimatePeriod()
    {
        int windowCount = _maximumPeriod;
        int bestLag = _maximumPeriod;
        float bestScore = float.NegativeInfinity;
        int end = _history.Length;

        for (int lag = _minimumPeriod; lag <= _maximumPeriod; lag++)
        {
            int start = end - windowCount - lag;
            if (start < 0)
            {
                continue;
            }

            float correlation = 0;
            float energy = 0;
            for (int offset = 0; offset < windowCount; offset++)
            {
                float recent = _history[end - windowCount + offset];
                float past = _history[start + offset];
                correlation += recent * past;
                energy += past * past;
            }

            float score = energy > 0 ? correlation / MathF.Sqrt(energy) : 0;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        return bestLag;
    }
}
