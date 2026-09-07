namespace AudioProbe;

/// <summary>
/// Голос «дальней стороны» — то, что в разговоре звучит из наушников и
/// возвращается в микрофон эхом.
///
/// Синтез, а не файл, по той же причине, по которой в оригинале синтезировался
/// рингтон: чужой файл тянет за собой лицензию, а свой надо рисовать. Здесь
/// добавляется третья причина — измерительная: сигнал должен быть
/// воспроизводимым от прогона к прогону, иначе два замера эхоподавления не
/// сравнить между собой.
///
/// Спектр ограничен телефонной полосой 300–3400 Гц: эхоподавитель настраивается
/// на то, что реально придёт из АТС, а не на белый шум во всей полосе.
/// Чередование «звучит — молчит» нужно, чтобы отдельно померить остаток эха и
/// собственный шум тракта.
/// </summary>
internal sealed class FarEndSource
{
    private const double OnSeconds = 1.0;
    private const double OffSeconds = 0.5;

    /// <summary>
    /// Уровень намеренно умеренный. Громче — и замер поедет: микрофон уйдёт в
    /// ограничение, а нелинейное искажение эхоподавитель не давит по
    /// построению, он линейный.
    /// </summary>
    private readonly float _amplitude;

    private readonly int _sampleRate;
    private readonly Random _random;
    private long _position;

    // Однополюсные фильтры: два верхних среза и один нижний дают грубую
    // телефонную полосу. Для измерительного сигнала этого достаточно.
    private float _lowState;
    private float _highState;

    public FarEndSource(int sampleRate, float amplitude = 0.15f, int seed = 20260904)
    {
        _sampleRate = sampleRate;
        _amplitude = amplitude;
        _random = new Random(seed);
    }

    /// <summary>Звучит ли дальняя сторона прямо сейчас.</summary>
    public bool IsActive(long position)
    {
        double cycle = OnSeconds + OffSeconds;
        double t = position / (double)_sampleRate % cycle;
        return t < OnSeconds;
    }

    public bool IsActiveNow => IsActive(_position);

    public void Fill(Span<float> frame)
    {
        double lowCut = 300.0;
        double highCut = 3400.0;
        float lowAlpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * highCut / _sampleRate));
        float highAlpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * lowCut / _sampleRate));

        for (int i = 0; i < frame.Length; i++)
        {
            float noise = (float)((_random.NextDouble() * 2.0) - 1.0);

            _lowState += lowAlpha * (noise - _lowState);
            _highState += highAlpha * (_lowState - _highState);
            float band = _lowState - _highState;

            frame[i] = IsActive(_position) ? band * _amplitude : 0f;
            _position++;
        }
    }
}
