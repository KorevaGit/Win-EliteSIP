namespace AudioProbe;

/// <summary>
/// Кольцо отсчётов между захватом и воспроизведением.
///
/// Один пишущий поток и один читающий, поэтому замок берётся на короткое
/// копирование и не держится ни на одном вызове WASAPI. Очередь с блокировкой
/// здесь не годится: читатель обязан вернуться к устройству за период, и
/// ожидание на пустой очереди означало бы пропущенный такт вместо честной
/// тишины.
///
/// В оригинале ту же роль играл <c>SampleRing</c> из MediaCore.
/// </summary>
internal sealed class MonoRing
{
    private readonly float[] _buffer;
    private readonly Lock _gate = new();
    private int _read;
    private int _write;
    private int _count;

    public MonoRing(int capacity)
    {
        _buffer = new float[capacity];
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Пишет всё или ничего. Частичная запись означала бы рваный кадр, а его
    /// потом не отличить от потери на устройстве.
    /// </summary>
    public bool Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (_count + samples.Length > _buffer.Length)
            {
                return false;
            }

            for (int i = 0; i < samples.Length; i++)
            {
                _buffer[_write] = samples[i];
                _write = (_write + 1) % _buffer.Length;
            }

            _count += samples.Length;
            return true;
        }
    }

    /// <summary>
    /// Читает сколько есть и говорит сколько прочитал. Недостачу заполняет
    /// вызывающий: только он знает, тишиной или сокрытием потерь.
    /// </summary>
    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            int take = Math.Min(destination.Length, _count);
            for (int i = 0; i < take; i++)
            {
                destination[i] = _buffer[_read];
                _read = (_read + 1) % _buffer.Length;
            }

            _count -= take;
            return take;
        }
    }
}
