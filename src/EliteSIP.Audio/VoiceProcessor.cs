using SoundFlow.Extensions.WebRtc.Apm;

namespace EliteSIP.Audio;

/// <summary>
/// Обработка голоса: эхоподавление, шумодав, автоматическая регулировка
/// усиления.
///
/// <b>Что здесь заменяет macOS.</b> В оригинале всё это делал один флаг —
/// <c>setVoiceProcessingEnabled(true)</c> на узле ввода-вывода: система сама
/// давала эхоподавитель, шумодав и АРУ, а качество было её заботой. На Windows
/// такого выключателя нет, и обработка своя — WebRTC APM из пакета
/// <c>SoundFlow.Extensions.WebRtc.Apm</c>.
///
/// <b>Замеры W0, из-за которых этот тип выглядит так, а не иначе.</b>
///
/// <list type="number">
/// <item><b>AEC3, а не AECM.</b> Мобильный режим — упрощённый эхоподавитель для
/// телефонов; на модели комнаты он дал 5,5 дБ там, где AEC3 давал 44,8.</item>
/// <item><b>Полная цепочка сильнее одного эхоподавителя на двадцать
/// децибел.</b> С шумодавом и АРУ подавление держится выше 27 дБ на всех
/// проверенных отношениях эха к шуму, вплоть до нулевого, — потому что шумодав
/// убирает то, что осталось после эхоподавителя. Один AEC3 на нулевом отношении
/// даёт 9,9 дБ. Поэтому шумодав включён по умолчанию.
///
/// Выключатель у него всё же есть (с 11 сентября 2026): оператор его просил, и
/// на гарнитуре, где эха нет по построению, цена выключения — только фон
/// комнаты в линии. На динамиках ноутбука выключенный шумодав возвращает
/// ровно те двадцать децибел эха, о которых выше, и об этом говорит пояснение
/// под переключателем.</item>
/// <item><b>АРУ — по настройке и по умолчанию выключена.</b> Продуктовое
/// решение оригинала: на встроенном микрофоне она полезна, на хорошей гарнитуре
/// «дышит». Эхоподавление и шумодав от неё не зависят — это проверено
/// отдельно.</item>
/// <item><b>Порядок вызовов важен.</b> <c>SetPipeline</c> задаёт частоту, на
/// которой APM вообще работает, и должен идти до включения блоков.</item>
/// </list>
///
/// <b>Задержка объявляется числом на каждом кадре, а не один раз.</b> Попытка
/// назвать её константой в W0 дала 37,6 дБ в одном прогоне и 2,7 дБ в следующем
/// на том же значении: она зависит от того, где на старте улеглось кольцо
/// воспроизведения, а это гонка потоков, разная каждый раз. Считает её
/// <see cref="WasapiVoiceAudioEngine"/> — он один знает все слагаемые.
/// </summary>
internal sealed class VoiceProcessor : IDisposable
{
    /// <summary>
    /// Частоты, на которых APM умеет работать. Всё остальное придётся
    /// пересчитывать.
    /// </summary>
    private static readonly int[] SupportedRates = [8000, 16000, 32000, 48000];

    private readonly AudioProcessingModule _apm;
    private readonly StreamConfig _stream;
    private readonly float[][] _nearIn;
    private readonly float[][] _nearOut;
    private readonly float[][] _far;
    private bool _disposed;

    public VoiceProcessor(int sampleRate, bool automaticGainControl, bool noiseSuppression = true)
    {
        if (Array.IndexOf(SupportedRates, sampleRate) < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "APM работает только на 8, 16, 32 или 48 кГц");
        }

        SampleRate = sampleRate;

        // Кадр APM — ровно десять миллисекунд. Это не настройка: библиотека
        // принимает только такой, и весь конвейер тракта считает от него.
        FrameSamples = sampleRate / 100;

        _apm = new AudioProcessingModule();
        using (ApmConfig config = new())
        {
            // Порядок важен: сначала конвейер, потом блоки.
            config.SetPipeline(sampleRate, false, false, DownmixMethod.AverageChannels);

            // mobileMode = false: это AEC3, полноценный.
            config.SetEchoCanceller(true, false);

            config.SetNoiseSuppression(noiseSuppression, NoiseSuppressionLevel.High);
            config.SetHighPassFilter(true);
            config.SetGainController2(automaticGainControl);

            _apm.ApplyConfig(config);
        }

        _apm.Initialize();

        _stream = new StreamConfig(sampleRate, 1);
        _nearIn = [new float[FrameSamples]];
        _nearOut = [new float[FrameSamples]];
        _far = [new float[FrameSamples]];
    }

    public int SampleRate { get; }

    /// <summary>Сколько отсчётов в одном кадре обработки. Всегда десять миллисекунд.</summary>
    public int FrameSamples { get; }

    /// <summary>
    /// Ближайшая частота, на которой APM работает.
    ///
    /// Устройство на 44 100 Гц придётся пересчитывать в 48 000 и обратно; на
    /// 48 000, 16 000 и 8 000 пересчёта не будет вовсе. Гарнитура в режиме связи
    /// работает на 8 кГц, и обрабатывать её на 48 значило бы трижды пересчитать
    /// то, чего в сигнале нет.
    /// </summary>
    public static int NearestSupportedRate(int sampleRate)
    {
        int best = SupportedRates[^1];
        foreach (int rate in SupportedRates)
        {
            if (rate == sampleRate)
            {
                return rate;
            }

            if (rate > sampleRate)
            {
                return rate;
            }

            best = rate;
        }

        return Math.Max(best, SupportedRates[^1]);
    }

    /// <summary>
    /// Показывает эхоподавителю, что мы сейчас проиграем.
    ///
    /// Зовётся до <see cref="Process"/> для того же отрезка времени: APM строит
    /// модель пути от динамика до микрофона, и без опорного сигнала вычитать ему
    /// нечего.
    /// </summary>
    public void AnalyzeReverse(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples)
        {
            throw new ArgumentException($"кадр обязан быть {FrameSamples} отсчётов", nameof(frame));
        }

        frame.CopyTo(_far[0]);
        _apm.AnalyzeReverseStream(_far, _stream);
    }

    /// <summary>Обрабатывает микрофонный кадр.</summary>
    /// <param name="near">Что услышал микрофон.</param>
    /// <param name="destination">Куда положить обработанное.</param>
    /// <param name="delayMilliseconds">
    /// Насколько опорный сигнал опережает микрофонный. Считается на каждом
    /// кадре — см. описание типа.
    /// </param>
    public void Process(ReadOnlySpan<float> near, Span<float> destination, int delayMilliseconds)
    {
        if (near.Length != FrameSamples)
        {
            throw new ArgumentException($"кадр обязан быть {FrameSamples} отсчётов", nameof(near));
        }

        near.CopyTo(_nearIn[0]);

        // Отрицательная задержка бессмысленна, а слишком большая APM всё равно
        // обрежет. Ограничение здесь, чтобы в журнал уходило то же число, что
        // ушло в библиотеку.
        _apm.SetStreamDelayMs(Math.Clamp(delayMilliseconds, 0, 500));
        _apm.ProcessStream(_nearIn, _stream, _stream, _nearOut);

        _nearOut[0].AsSpan(0, FrameSamples).CopyTo(destination);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        _apm.Dispose();
    }
}
