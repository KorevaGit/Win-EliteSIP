namespace EliteSIP.MediaCore;

/// <summary>
/// Проверочный сигнал вместо микрофона: тон, включающийся слогами.
///
/// <b>Зачем он нужен именно такой.</b> Задержка меряется сравнением
/// отправленного с вернувшимся (<see cref="AudioDelayEstimator"/>), а сравнение
/// идёт по огибающей — по тому, как меняется громкость. Значит, сигнал обязан
/// громкостью меняться, и меняться неповторяющимся образом: ровный тон
/// одинаково хорошо совпадает сам с собой при любом сдвиге, и задержка по нему
/// не определяется вовсе.
///
/// Отсюда устройство: тон телефонной полосы, включаемый слогами разной длины и
/// громкости по заданному зерну. Зерно фиксировано, поэтому два прогона
/// сравнимы между собой, а «слоги» дают ту же картину, что живая речь, — на
/// которой измерение и должно работать.
///
/// Пригодится и за пределами замера: проверка «меня слышно?» без звонка коллеге
/// (<c>VoiceSelfTest</c> оригинала, этап W8) устроена ровно так же — в линию
/// уходит известный сигнал, а не то, что услышал микрофон.
/// </summary>
public sealed class AudioTestSignal
{
    /// <summary>
    /// Частота тона.
    ///
    /// 800 Гц — середина телефонной полосы: и G.711, и G.722 передают её без
    /// потерь, и никакой шумодав не примет её за фон.
    /// </summary>
    public const int ToneHertz = 800;

    /// <summary>Громкость: две трети шкалы. Выше — риск ограничения на чужой АРУ.</summary>
    private const double Amplitude = 0.66;

    private readonly AudioFrameEncoder _encoder;
    private readonly int _sampleRate;
    private readonly int _samplesPerFrame;
    private readonly Random _random;
    private readonly short[] _frame;

    private long _position;
    private int _remainingInSegment;
    private double _segmentLevel;

    public AudioTestSignal(
        AudioCodec codec = AudioCodec.Pcmu,
        int packetTimeMilliseconds = AudioCodecInfo.DefaultPacketTimeMilliseconds,
        int seed = 20260908)
    {
        _encoder = new AudioFrameEncoder(codec);
        _sampleRate = (int)codec.SampleRate();
        _samplesPerFrame = codec.SampleCount(packetTimeMilliseconds);
        _random = new Random(seed);
        _frame = new short[_samplesPerFrame];
    }

    /// <summary>Готовый кадр в кодеке разговора.</summary>
    public ReadOnlyMemory<byte> NextFrame()
    {
        for (int index = 0; index < _samplesPerFrame; index++)
        {
            if (_remainingInSegment <= 0)
            {
                StartSegment();
            }

            double phase = 2 * Math.PI * ToneHertz * _position / _sampleRate;
            _frame[index] = (short)(Math.Sin(phase) * _segmentLevel * Amplitude * short.MaxValue);

            _position++;
            _remainingInSegment--;
        }

        return _encoder.Encode(_frame);
    }

    /// <summary>
    /// Начинает очередной слог.
    ///
    /// Длина от 80 до 400 мс, громкость либо от четверти до полной, либо ноль:
    /// паузы обязательны — без них огибающая перестаёт быть узнаваемой, и
    /// корреляция снова не находит сдвиг.
    /// </summary>
    private void StartSegment()
    {
        int milliseconds = _random.Next(80, 400);
        _remainingInSegment = _sampleRate * milliseconds / 1000;
        _segmentLevel = _random.Next(0, 4) == 0 ? 0 : 0.25 + (_random.NextDouble() * 0.75);
    }
}
