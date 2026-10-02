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
/// <item><b>Шумодав не убирает чужие голоса — и не должен.</b> Он вычитает
/// ровный фон, а речь пропускает любую. Голоса вокруг приглушает отдельный
/// блок, <see cref="BackgroundVoiceGate"/>, — по расстоянию до микрофона, а не
/// по спектру (жалоба 2 октября 2026).</item>
/// </list>
///
/// Цепочка: эхоподавитель → фильтр низких → шумодав (всё внутри APM) →
/// подавление голосов вокруг → АРУ. АРУ последняя, чтобы мерить голос
/// оператора, а не то, что убрали до неё.
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

    /// <summary>АРУ. <c>null</c> — выключена.</summary>
    private readonly SpeechGainControl? _gainControl;

    /// <summary>Подавление голосов вокруг. <c>null</c> — выключено.</summary>
    private readonly BackgroundVoiceGate? _voiceGate;

    /// <summary>Фон до обработки и после — доказательство, что она вообще работает.</summary>
    private readonly NoiseFloor _floorIn = new();
    private readonly NoiseFloor _floorOut = new();
    private long _frames;
    private long _gatedFrames;
    private long _failedFrames;
    private ApmError _lastError;
    private readonly string? _setupError;
    private readonly StreamConfig _stream;
    private readonly float[][] _nearIn;
    private readonly float[][] _nearOut;
    private readonly float[][] _far;
    private bool _disposed;

    /// <param name="sampleRate">Частота обработки: 8, 16, 32 или 48 кГц.</param>
    /// <param name="automaticGainControl">Своя АРУ после обработки.</param>
    /// <param name="noiseSuppression">Шумодав.</param>
    /// <param name="echoCancellation">
    /// Эхоподавление. Выключается, когда вывод — наушники или гарнитура: пути
    /// от динамика до микрофона там нет, вычитать нечего, а подавитель эха на
    /// одновременной речи всё равно приседает голос оператора — это то самое
    /// «падение громкости» из жалобы 11 сентября 2026. Вместе с ним смягчается
    /// и шумодав: высокий уровень был нужен, чтобы добирать остаток эха
    /// (замеры W0 в описании типа), а без эха он только съедает тихие слоги.
    /// </param>
    /// <param name="backgroundVoiceSuppression">
    /// Подавление голосов вокруг (<see cref="BackgroundVoiceGate"/>). По
    /// умолчанию здесь выключено, чтобы стенд эхоподавления мерил то же, что
    /// мерил на W0; тракт передаёт настройку явно.
    /// </param>
    public VoiceProcessor(
        int sampleRate,
        bool automaticGainControl,
        bool noiseSuppression = true,
        bool echoCancellation = true,
        bool backgroundVoiceSuppression = false)
    {
        if (Array.IndexOf(SupportedRates, sampleRate) < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "APM работает только на 8, 16, 32 или 48 кГц");
        }

        SampleRate = sampleRate;
        _gainControl = automaticGainControl ? new SpeechGainControl() : null;
        _voiceGate = backgroundVoiceSuppression ? new BackgroundVoiceGate() : null;
        SuppressionLevel = noiseSuppression
            ? (echoCancellation ? NoiseSuppressionLevel.High : NoiseSuppressionLevel.Moderate)
            : null;

        // Кадр APM — ровно десять миллисекунд. Это не настройка: библиотека
        // принимает только такой, и весь конвейер тракта считает от него.
        FrameSamples = sampleRate / 100;

        _apm = new AudioProcessingModule();
        using (ApmConfig config = new())
        {
            // Порядок важен: сначала конвейер, потом блоки.
            config.SetPipeline(sampleRate, false, false, DownmixMethod.AverageChannels);

            // mobileMode = false: это AEC3, полноценный.
            config.SetEchoCanceller(echoCancellation, false);

            config.SetNoiseSuppression(
                noiseSuppression,
                SuppressionLevel ?? NoiseSuppressionLevel.Moderate);
            config.SetHighPassFilter(true);
            // Регуляторы усиления WebRTC выключены: АРУ своя, после обработки
            // (см. SpeechGainControl — там и замеры, почему).
            config.SetGainController1(false, GainControlMode.AdaptiveDigital, 3, 9, true);
            config.SetGainController2(false);

            // Отказ настройки значит, что APM работает с умолчаниями — без
            // шумодава и эхоподавителя. Звонок от этого не ломается, поэтому
            // не исключение, а строка в итоге (см. Summary).
            ApmError configured = _apm.ApplyConfig(config);
            if (configured != ApmError.NoError)
            {
                _setupError = $"настройка: {configured}";
            }
        }

        ApmError initialized = _apm.Initialize();
        if (initialized != ApmError.NoError)
        {
            _setupError = (_setupError is null ? string.Empty : _setupError + ", ") + $"запуск: {initialized}";
        }

        _stream = new StreamConfig(sampleRate, 1);
        _nearIn = [new float[FrameSamples]];
        _nearOut = [new float[FrameSamples]];
        _far = [new float[FrameSamples]];
    }

    public int SampleRate { get; }

    /// <summary>Уровень шумодава, переданный в APM. <c>null</c> — выключен.</summary>
    public NoiseSuppressionLevel? SuppressionLevel { get; }

    /// <summary>Работает ли подавление голосов вокруг.</summary>
    public bool SuppressesBackgroundVoices => _voiceGate is not null;

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
        _floorIn.Observe(near);

        // Отрицательная задержка бессмысленна, а слишком большая APM всё равно
        // обрежет. Ограничение здесь, чтобы в журнал уходило то же число, что
        // ушло в библиотеку.
        _apm.SetStreamDelayMs(Math.Clamp(delayMilliseconds, 0, 500));
        ApmError result = _apm.ProcessStream(_nearIn, _stream, _stream, _nearOut);

        // Код возврата до 2 октября 2026 не проверялся нигде: откажи библиотека
        // на каком-то устройстве, обработка молча не шла бы, и узнать об этом
        // было нечем. Теперь отказ считается и виден в итоге звонка, а в линию
        // уходит необработанный голос — тишина вместо него хуже.
        if (result != ApmError.NoError)
        {
            _failedFrames++;
            _lastError = result;
            near.CopyTo(_nearOut[0]);
        }

        Span<float> processed = _nearOut[0].AsSpan(0, FrameSamples);

        // Голоса вокруг — после шумодава: по очищенному сигналу уровень
        // оператора меряется точнее, а фон не держит расширитель открытым.
        if (_voiceGate is not null)
        {
            _voiceGate.Process(processed);
            if (_voiceGate.GainDb <= -6)
            {
                _gatedFrames++;
            }
        }

        // АРУ — после всего остального, а не до: так она меряет голос, а не
        // эхо, шум и соседей, и не вытягивает то, что убрали до неё.
        _gainControl?.Process(processed);

        _floorOut.Observe(processed);
        _frames++;

        processed.CopyTo(destination);
    }

    /// <summary>Прибавка АРУ сейчас, дБ. Ноль, пока АРУ выключена.</summary>
    public double AutomaticGainDb => _gainControl?.GainDb ?? 0;

    /// <summary>Кадры, на которых APM вернула ошибку и голос ушёл необработанным.</summary>
    public long FailedFrames => _failedFrames;

    /// <summary>
    /// Что обработка сделала с этим микрофоном — одной строкой для итога
    /// звонка.
    /// </summary>
    ///
    /// <remarks>
    /// Ответ на вопрос «шумодав вообще работает на этой гарнитуре?»: фон
    /// микрофона до обработки и в линии после неё. Если они совпадают при
    /// включённом шумодаве — обработка не идёт, и это видно по журналу, а не
    /// на слух у клиента. Числа пишет поток захвата, читает журнал; гонка стоит
    /// одного неточного числа, как у пиков тракта.
    /// </remarks>
    public string Summary
    {
        get
        {
            System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
            long frames = Volatile.Read(ref _frames);
            if (frames == 0)
            {
                return "обработка: ни одного кадра";
            }

            string text = string.Create(
                invariant,
                $"обработка: {frames / 100.0:0} с, фон микрофона {Level(_floorIn.Db)} → в линию {Level(_floorOut.Db)}");

            if (_voiceGate is not null)
            {
                text += _voiceGate.VoiceDb is double voice
                    ? string.Create(
                        invariant,
                        $", голос оператора {voice:0} дБ, голоса вокруг приглушались {100.0 * Volatile.Read(ref _gatedFrames) / frames:0} % времени")
                    : ", голос оператора не услышан — голоса вокруг не приглушались";
            }

            if (_setupError is not null)
            {
                text += $", ОШИБКА APM ({_setupError}) — шумодав и эхоподавитель могли не работать";
            }

            long failed = Volatile.Read(ref _failedFrames);
            if (failed > 0)
            {
                text += string.Create(invariant, $", ОШИБКА APM {_lastError} на {failed} кадрах — голос шёл без обработки");
            }

            return text;

            string Level(double db) => db <= -120 ? "тишина" : string.Create(invariant, $"{db:0} дБ");
        }
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

    /// <summary>
    /// Уровень фона: десятый процентиль уровней кадров за всё время.
    /// </summary>
    ///
    /// <remarks>
    /// Процентиль, а не нижняя огибающая: один кадр цифрового нуля на старте
    /// устройства уронил бы огибающую на −180 дБ, и выбиралась бы она оттуда
    /// минутами. Десятая доля кадров — это паузы, даже если оператор говорит
    /// большую часть разговора.
    /// </remarks>
    private sealed class NoiseFloor
    {
        private const int Floor = -120;
        private readonly long[] _bins = new long[-Floor + 1];
        private long _count;

        public void Observe(ReadOnlySpan<float> frame)
        {
            double energy = 0;
            foreach (float sample in frame)
            {
                energy += sample * sample;
            }

            double rms = Math.Sqrt(energy / Math.Max(1, frame.Length));
            int db = rms <= 1e-9 ? Floor : (int)Math.Round(20 * Math.Log10(rms));
            _bins[Math.Clamp(db, Floor, 0) - Floor]++;
            _count++;
        }

        public double Db
        {
            get
            {
                long wanted = Volatile.Read(ref _count) / 10;
                long seen = 0;
                for (int i = 0; i < _bins.Length; i++)
                {
                    seen += _bins[i];
                    if (seen > wanted)
                    {
                        return i + Floor;
                    }
                }

                return Floor;
            }
        }
    }
}
