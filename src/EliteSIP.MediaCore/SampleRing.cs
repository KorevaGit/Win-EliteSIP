namespace EliteSIP.MediaCore;

/// <summary>
/// Кольцо готовых к воспроизведению отсчётов между сетью и звуковой картой.
///
/// Вынесено из движка отдельным типом ради двух вещей. Первая — его можно
/// проверить тестами без звуковой карты, а именно здесь живёт вся арифметика,
/// от которой зависит, будет ли слышен щелчок. Вторая — из потока рендера
/// нельзя ни выделять память, ни брать блокировку надолго, поэтому буфер
/// заведомо фиксированного размера, и это свойство должно быть видно в типе, а
/// не спрятано в комментарии.
///
/// В оригинале это структура с мутирующими методами. Здесь класс: кольцо одно
/// на тракт, и его передают между потоком сети и потоком вывода — значимый тип
/// в этой роли дал бы каждому свою копию, причём молча.
/// </summary>
internal sealed class SampleRing
{
    private readonly float[] _storage;
    private int _readIndex;
    private int _count;

    public SampleRing(int capacity, int targetFill)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetFill, capacity);

        _storage = new float[capacity];
        TargetFill = targetFill;
    }

    /// <summary>
    /// Сколько отсчётов держать наготове. Меньше — риск недобора на любой
    /// неровности, больше — лишняя задержка сверх джиттер-буфера.
    /// </summary>
    public int TargetFill { get; }

    public int Capacity => _storage.Length;

    public int Available => _count;

    public int FreeSpace => _storage.Length - _count;

    /// <summary>Сколько отсчётов не хватает до целевого запаса.</summary>
    public int Deficit => Math.Max(0, TargetFill - _count);

    /// <summary>
    /// Добавляет отсчёты. Возвращает, сколько поместилось.
    ///
    /// Не поместившееся отбрасывается молча: вызывающий и так спрашивает
    /// <see cref="FreeSpace"/> перед тем, как декодировать кадр, а бросать из
    /// звукового тракта нечем и некому.
    /// </summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        int writable = Math.Min(samples.Length, FreeSpace);
        if (writable <= 0)
        {
            return 0;
        }

        int writeIndex = (_readIndex + _count) % _storage.Length;
        int firstChunk = Math.Min(writable, _storage.Length - writeIndex);
        samples[..firstChunk].CopyTo(_storage.AsSpan(writeIndex));
        if (writable > firstChunk)
        {
            samples[firstChunk..writable].CopyTo(_storage);
        }

        _count += writable;
        return writable;
    }

    /// <summary>
    /// Выкладывает <paramref name="destination"/> отсчётов, добивая тишиной.
    ///
    /// Отдать меньше, чем попросили, нельзя: движок воспримет это как обрыв.
    /// Возвращает, сколько из них были настоящим звуком — по разнице и
    /// считаются недоборы.
    /// </summary>
    public int Read(Span<float> destination)
    {
        int readable = Math.Min(destination.Length, _count);

        for (int offset = 0; offset < readable; offset++)
        {
            destination[offset] = _storage[(_readIndex + offset) % _storage.Length];
        }

        destination[readable..].Clear();

        _readIndex = (_readIndex + readable) % _storage.Length;
        _count -= readable;
        return readable;
    }

    /// <summary>
    /// Забирает всё, что накопилось, но не больше <paramref name="maximum"/>.
    ///
    /// Для стороны захвата: там читает обычный поток, а не рендер, и массив ему
    /// удобнее спана. Ограничение сверху есть затем, чтобы опоздавший поток не
    /// выгреб полсекунды разом и не отправил их в сеть пачкой.
    /// </summary>
    public float[] Drain(int maximum)
    {
        int readable = Math.Min(maximum, _count);
        if (readable <= 0)
        {
            return [];
        }

        float[] result = new float[readable];
        Read(result);
        return result;
    }

    public void RemoveAll()
    {
        _readIndex = 0;
        _count = 0;
    }
}
