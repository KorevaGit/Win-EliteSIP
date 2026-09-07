namespace EliteSIP.Audio;

/// <summary>
/// Сходится ли баланс отсчётов на всём пути от устройства до сети.
///
/// <b>Правило записано в W0, и оно оплачено.</b> Стенд две недели показывал
/// «уход часов 2065 ± 2,3 ppm» на проводной гарнитуре, где кварц на вход и
/// выход физически один. Число выглядело убедительно — с честной оценкой
/// погрешности по тысяче проб — ровно до тех пор, пока его не свели с разницей
/// счётчиков. Причина оказалась в двух строках: из кольца читалось
/// <c>toWrite + 2</c>, выводилось <c>toWrite</c>, лишние два отсчёта на каждом
/// такте выбрасывались. Двести отсчётов в секунду — это и есть 2053 ppm.
///
/// Важнее самой ошибки то, как она звучит: выброшенный отсчёт — это разрыв
/// волны, то есть щелчок сто раз в секунду. В разговоре его списали бы на кодек
/// или на сеть, и искали бы не там.
///
/// Отсюда правило: <b>сколько устройство отдало — столько прошло обработку и
/// столько ушло в сеть, до единицы.</b> Проверка дешёвая, считается на
/// счётчиках и ловит целый класс ошибок, не слышимых на коротком прогоне.
///
/// <b>Чего этот тип не делает.</b> Он не ищет причину и не чинит. Он отвечает
/// на один вопрос — сходится или нет, — и его ответ идёт в диагностику вместе с
/// числами. Разбираться по числам будет человек.
/// </summary>
public sealed class SampleBalance
{
    /// <summary>
    /// Во сколько раз остаток может законно превысить кадр.
    ///
    /// Не два, а четыре, и число из замера, а не из осторожности. Устройство
    /// отдаёт пакеты пачками: одно пробуждение приносит два-три пакета, и все
    /// они превращаются в отсчёты до того, как кодер соберёт первый кадр. На
    /// первом прогоне тракта на живом железе наибольший остаток вышел 304
    /// отсчёта при кадре в 160 — то есть предел в два кадра проходился впритык
    /// и однажды не прошёл бы вовсе.
    ///
    /// Утечку, ради которой всё считается, это не прячет: она растёт без
    /// предела, а не упирается в четыре кадра.
    /// </summary>
    public const int PendingBurstFactor = 4;

    private readonly double _ratio;
    private readonly int _samplesPerFrame;
    private readonly long _conversionAllowance;

    private long _captured;
    private long _converted;
    private long _encodedFrames;
    private long _maximumPending;

    /// <param name="conversionRatio">
    /// Сколько отсчётов разговора приходится на один отсчёт устройства —
    /// <see cref="Resampler.Ratio"/>.
    /// </param>
    /// <param name="samplesPerFrame">Отсчётов в кадре кодека.</param>
    /// <param name="conversionAllowance">
    /// Сколько отсчётов законно висит внутри пересчёта — его задержка, см.
    /// <see cref="Resampler.LatencySamples"/>. Без этой поправки исправный
    /// тракт объявлялся бы разошедшимся на первой же секунде.
    /// </param>
    public SampleBalance(double conversionRatio, int samplesPerFrame, int conversionAllowance)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(conversionRatio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samplesPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(conversionAllowance);

        _ratio = conversionRatio;
        _samplesPerFrame = samplesPerFrame;

        // Плюс один — на округление: ожидаемое число считается умножением, и
        // ровно на границе оно может разойтись с целым на единицу.
        _conversionAllowance = conversionAllowance + 1;
    }

    /// <summary>Сколько отсчётов отдало устройство.</summary>
    public long Captured => Interlocked.Read(ref _captured);

    /// <summary>Сколько отсчётов вышло из пересчёта частоты.</summary>
    public long Converted => Interlocked.Read(ref _converted);

    /// <summary>Сколько кадров ушло в сеть.</summary>
    public long EncodedFrames => Interlocked.Read(ref _encodedFrames);

    /// <summary>Сколько отсчётов разговора должно было получиться из захваченного.</summary>
    public long ExpectedConverted => (long)Math.Round(Captured * _ratio);

    /// <summary>
    /// На сколько пересчёт разошёлся с ожидаемым.
    ///
    /// Отрицательное — часть отсчётов ещё внутри фильтра, это норма.
    /// Положительное больше единицы — отсчёты <b>изобретены</b>, и это уже
    /// ошибка: взять их неоткуда.
    /// </summary>
    public long ConversionDiscrepancy => Converted - ExpectedConverted;

    /// <summary>
    /// Сколько отсчётов лежит между пересчётом и кодером.
    ///
    /// Здоровое значение — меньше кадра: кодер забирает кадр целиком, как
    /// только он набрался. Растущее значение и есть тот самый случай из W0:
    /// отсчёты приходят, но до сети не доезжают.
    /// </summary>
    public long Pending => Converted - (EncodedFrames * _samplesPerFrame);

    /// <summary>Наибольшее <see cref="Pending"/> за всё время. Именно оно и растёт при утечке.</summary>
    public long MaximumPending => Interlocked.Read(ref _maximumPending);

    /// <summary>Сходится ли баланс.</summary>
    public bool IsBalanced =>
        ConversionDiscrepancy <= 1
        && ConversionDiscrepancy >= -_conversionAllowance
        && Pending >= 0
        && MaximumPending < _samplesPerFrame * PendingBurstFactor;

    public void NoteCaptured(int samples) => Interlocked.Add(ref _captured, samples);

    public void NoteConverted(int samples)
    {
        Interlocked.Add(ref _converted, samples);
        TrackPending();
    }

    public void NoteEncodedFrame()
    {
        Interlocked.Increment(ref _encodedFrames);
        TrackPending();
    }

    /// <summary>Обнуляет счёт. Зовётся при пересборке тракта.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _captured, 0);
        Interlocked.Exchange(ref _converted, 0);
        Interlocked.Exchange(ref _encodedFrames, 0);
        Interlocked.Exchange(ref _maximumPending, 0);
    }

    /// <summary>
    /// Строка для диагностики.
    ///
    /// Печатает и приговор, и числа, на которых он построен. Урок W0: приговор
    /// стенда был неверен ровно на лучшем результате, и заметить это можно было
    /// только сверив его с таблицей рядом.
    /// </summary>
    public string Summary =>
        $"баланс: {(IsBalanced ? "сходится" : "РАЗОШЁЛСЯ")}; "
        + $"захвачено {Captured}, пересчитано {Converted} (ожидалось {ExpectedConverted}, "
        + $"расхождение {ConversionDiscrepancy:+#;-#;0}), "
        + $"кадров {EncodedFrames}, не закодировано {Pending} (максимум {MaximumPending})";

    private void TrackPending()
    {
        long pending = Pending;
        long seen = Interlocked.Read(ref _maximumPending);
        while (pending > seen)
        {
            long previous = Interlocked.CompareExchange(ref _maximumPending, pending, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }
}
