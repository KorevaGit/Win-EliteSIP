using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Тракт-заглушка: считает вызовы и умеет отказать в запуске.
///
/// Существует затем, что настоящий тракт без звуковой карты не заводится — и
/// именно поэтому шину в оригинале нельзя было проверить вовсе. Здесь она
/// проверяется целиком, кроме одного: что настоящее закрытие потоков WASAPI
/// действительно возвращает гарнитуру из режима связи. Это замер на живом
/// железе, он в приёмке этапа.
/// </summary>
internal sealed class FakeVoiceAudioEngine : IVoiceAudioEngine
{
    public VoiceAudioHandlers Handlers { get; set; } = VoiceAudioHandlers.None;

    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int RestartCount { get; private set; }
    public int DisposeCount { get; private set; }
    public VoiceAudioConfiguration? LastConfiguration { get; private set; }
    public string? LastRestartReason { get; private set; }

    /// <summary>Отказать в запуске — так выглядит устройство, занятое монопольно.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Порядок вызовов: захват обязан останавливать до перестройки.</summary>
    public List<string> Log { get; } = [];

    public void Reconfigure(VoiceAudioConfiguration configuration)
    {
        LastConfiguration = configuration;
        Log.Add("reconfigure");
    }

    public void Start()
    {
        Log.Add("start");
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        StartCount++;
    }

    public void Stop()
    {
        StopCount++;
        Log.Add("stop");
    }

    public void Restart(string reason)
    {
        RestartCount++;
        LastRestartReason = reason;
        Log.Add("restart");
    }

    public void Dispose()
    {
        DisposeCount++;
        Log.Add("dispose");
    }
}

/// <summary>
/// Ручной планировщик отсрочки.
///
/// Отложенное освобождение устройства — главное решение шины, и проверять его
/// настоящим ожиданием секунды значило бы не проверять: тест либо стоит
/// секунду на каждый случай, либо мигает.
/// </summary>
internal sealed class ManualScheduler
{
    private readonly List<Entry> _pending = [];

    public TimeSpan? LastDelay { get; private set; }

    /// <summary>Сколько работ ждёт своего часа.</summary>
    public int PendingCount => _pending.Count(e => !e.Cancelled);

    /// <summary>Сколько работ было отменено.</summary>
    public int CancelledCount { get; private set; }

    public IDisposable Schedule(TimeSpan delay, Action work)
    {
        LastDelay = delay;
        Entry entry = new(work, this);
        _pending.Add(entry);
        return entry;
    }

    /// <summary>Доводит время до срока: выполняет всё, что не отменили.</summary>
    public void Fire()
    {
        Entry[] due = [.. _pending];
        _pending.Clear();
        foreach (Entry entry in due)
        {
            if (!entry.Cancelled)
            {
                entry.Work();
            }
        }
    }

    /// <summary>
    /// Выполняет работу, даже если её отменили.
    ///
    /// Так ведут себя и таймер, и очередь оригинала: отмена не останавливает
    /// то, что уже началось. Ради этого случая в шине стоит повторная проверка
    /// владения, и без такого способа её было бы нечем проверить.
    /// </summary>
    public void FireEvenIfCancelled()
    {
        Entry[] due = [.. _pending];
        _pending.Clear();
        foreach (Entry entry in due)
        {
            entry.Work();
        }
    }

    private sealed class Entry(Action work, ManualScheduler owner) : IDisposable
    {
        public Action Work { get; } = work;
        public bool Cancelled { get; private set; }

        public void Dispose()
        {
            if (Cancelled)
            {
                return;
            }

            Cancelled = true;
            owner.CancelledCount++;
        }
    }
}
