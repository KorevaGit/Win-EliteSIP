namespace EliteSIP.Audio;

/// <summary>
/// Подавление голосов вокруг: всё, что заметно тише голоса самого оператора,
/// приглушается.
/// </summary>
///
/// <remarks>
/// <b>Зачем, если есть шумодав.</b> Шумодав WebRTC — спектральный: он
/// оценивает ровный фон (вентилятор, гул, шипение) и вычитает его. Речь он
/// пропускает по построению, и чья она — ему всё равно. Поэтому соседи по
/// кабинету шли в линию «очень отчётливо», хотя шум комнаты убирался: жалоба
/// 2 октября 2026 на USB-гарнитуре. Ни один уровень шумодава этого не
/// исправит — сильный только сильнее ест тихие слоги самого оператора.
///
/// <b>Чем отличаются голоса.</b> Расстоянием до микрофона. Штанга гарнитуры
/// стоит в двух-трёх сантиметрах от рта, сосед — в метре-двух: по закону
/// обратных квадратов это 25–35 дБ разницы на входе. Тракт следит за уровнем
/// речи оператора, и всё, что тише его на <see cref="MarginDb"/> и больше,
/// считается чужим и приглушается — плавно, расширителем, а не обрывом. Пока
/// оператор говорит, чужие голоса закрыты его голосом и так; слышны они были
/// именно в паузах, и в паузах блок их и убирает.
///
/// <b>Где не поможет.</b> Микрофон ноутбука стоит от оператора в полуметре, а
/// от соседа в двух метрах — разница всего около 12 дБ, меньше запаса. Там
/// блок приглушит дальние и тихие голоса, а громкий сосед рядом пройдёт.
/// Отделять голоса по содержанию, а не по уровню, умеют только нейросетевые
/// шумодавы; это отдельная работа, записанная в аудите 2 октября.
///
/// <b>Как не съесть самого оператора.</b>
/// <list type="bullet">
/// <item>Уровень оператора — самые громкие устойчивые отрезки речи: 95-й
/// процентиль уровней речевых кадров за последние две секунды речи. В
/// гарнитуре громче всех почти всегда сам оператор. Вверх оценка идёт сразу,
/// вниз — медленно (<see cref="VoiceDecayDbPerFrame"/>): пока оператор
/// слушает клиента, а говорит сосед, оценка не сползает к соседу. Щелчок —
/// один-два кадра из двухсот — до процентиля не дотягивает.
///
/// До 0.1.78 уровнем считалась первая услышанная речь, подтягиваемая
/// медленно. Тестовая группа, 17 октября 2026: за соседним столом
/// непрерывно играло видео, оно зазвучало раньше оператора, и блок принял
/// его за оператора (оценка −55 дБ при голосе около −44) — голоса вокруг
/// не приглушались ни разу.</item>
/// <item>Пока голос не услышан (треть секунды речи), блок не делает
/// ничего.</item>
/// <item>Открывается сразу, в том же кадре; закрывается после
/// <see cref="HoldFrames"/> тишины и плавно — окончания слов и тихие слоги
/// между громкими не срезаются.</item>
/// <item>Глубина ограничена <see cref="DepthDb"/>: остаток фона в паузах
/// лучше, чем мёртвая тишина, которая на слух читается как обрыв связи.</item>
/// </list>
///
/// Тип чистый и без замков, как <see cref="SpeechGainControl"/>: вход — кадры,
/// выход — те же кадры, проверяется без звуковой карты.
/// </remarks>
internal sealed class BackgroundVoiceGate
{
    /// <summary>Насколько тише голоса оператора начинается чужое, дБ.</summary>
    public const double MarginDb = 16;

    /// <summary>Сколько вправе убрать у чужого, дБ.</summary>
    public const double DepthDb = 24;

    /// <summary>
    /// Крутизна расширителя: ниже порога каждый децибел входа становится
    /// четырьмя на выходе. Сосед на 25 дБ тише оператора уходит на всю
    /// глубину, а тихий слог у самого порога — на три-шесть децибел.
    /// </summary>
    private const double Ratio = 4;

    /// <summary>Сколько кадров держать открытым после последнего громкого: 250 мс.</summary>
    private const int HoldFrames = 25;

    /// <summary>Скорость закрытия, дБ за кадр: вся глубина — за четверть секунды.</summary>
    private const double ReleaseDbPerFrame = 1.0;

    /// <summary>Сколько речевых кадров нужно услышать, прежде чем приглушать: треть секунды.</summary>
    private const int EstablishFrames = 33;

    /// <summary>Сколько последних речевых кадров помнится для оценки: две секунды речи.</summary>
    private const int VoiceWindowFrames = 200;

    /// <summary>Какая доля речевых кадров громче оценки: пять процентов.</summary>
    private const double VoicePercentile = 0.95;

    /// <summary>
    /// Как быстро оценка голоса спускается, когда громкие отрезки ушли, дБ за
    /// речевой кадр: около 0,3 дБ в секунду чужой речи. Оператор, слушающий
    /// клиента минуту, пока говорит сосед, потеряет в оценке до 18 дБ — и это
    /// предел: дольше молчащего оператора от соседа по уровню не отличить.
    /// </summary>
    private const double VoiceDecayDbPerFrame = 0.003;

    /// <summary>Насколько кадр должен быть громче фона, чтобы считаться чьей-то речью.</summary>
    private const double SpeechOverNoiseDb = 10;

    /// <summary>Тише этого не речь, а тишина.</summary>
    private const double SilenceDb = -62;

    /// <summary>Как быстро оценка фона ползёт вверх, дБ за кадр: 1 дБ в секунду.</summary>
    private const double NoiseRiseDbPerFrame = 0.01;

    private double _noiseDb;
    private double? _voiceDb;
    private int _voiceFrames;

    /// <summary>Уровни последних речевых кадров по кругу, дБ.</summary>
    private readonly double[] _speech = new double[VoiceWindowFrames];
    private readonly double[] _sorted = new double[VoiceWindowFrames];
    private int _speechNext;
    private int _speechCount;
    private int _hold;
    private double _gainDb;
    private float _appliedGain = 1f;

    /// <summary>Уровень голоса оператора, дБ RMS. <c>null</c> — ещё не услышан.</summary>
    public double? VoiceDb => _voiceFrames >= EstablishFrames ? _voiceDb : null;

    /// <summary>Сколько убрано в последнем кадре, дБ (ноль или отрицательное).</summary>
    public double GainDb => _gainDb;

    /// <summary>Приглушает кадр на месте.</summary>
    public void Process(Span<float> frame)
    {
        if (frame.IsEmpty)
        {
            return;
        }

        double levelDb = RmsDb(frame);

        _noiseDb = levelDb < _noiseDb ? levelDb : _noiseDb + NoiseRiseDbPerFrame;
        bool isSpeech = levelDb > SilenceDb && levelDb > _noiseDb + SpeechOverNoiseDb;

        TrackVoice(levelDb, isSpeech);

        double wanted = 0;
        if (VoiceDb is double voice)
        {
            double threshold = voice - MarginDb;
            if (levelDb >= threshold)
            {
                _hold = HoldFrames;
            }
            else if (_hold > 0)
            {
                _hold--;
            }
            else
            {
                wanted = -Math.Min(DepthDb, (threshold - levelDb) * (Ratio - 1));
            }
        }

        // Открывается сразу, закрывается плавно: срезанное начало слова
        // слышно, медленно ушедший фон — нет.
        _gainDb = wanted >= _gainDb ? wanted : Math.Max(wanted, _gainDb - ReleaseDbPerFrame);

        // Прибавка меняется плавно внутри кадра: скачок на границе десяти
        // миллисекунд слышен как щелчок.
        float target = (float)Math.Pow(10, _gainDb / 20);
        float start = _appliedGain;
        if (start == 1f && target == 1f)
        {
            return;
        }

        float step = (target - start) / frame.Length;
        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] *= start + (step * (i + 1));
        }

        _appliedGain = target;
    }

    private void TrackVoice(double levelDb, bool isSpeech)
    {
        if (!isSpeech)
        {
            return;
        }

        _speech[_speechNext] = levelDb;
        _speechNext = (_speechNext + 1) % VoiceWindowFrames;
        _speechCount = Math.Min(_speechCount + 1, VoiceWindowFrames);
        _voiceFrames++;

        // Процентиль по окну: сортировка двухсот чисел на кадр в 10 мс — это
        // микросекунды, а окно должно быть честным, без скользящих оценок,
        // которые и подвели прежнюю версию.
        Array.Copy(_speech, _sorted, _speechCount);
        Array.Sort(_sorted, 0, _speechCount);
        double loud = _sorted[Math.Min(_speechCount - 1, (int)(_speechCount * VoicePercentile))];

        // Вверх — сразу: громкие отрезки и есть оператор. Вниз — медленно:
        // пока он слушает клиента, окно заполняет чужая речь.
        _voiceDb = _voiceDb is double voice && loud < voice
            ? Math.Max(loud, voice - VoiceDecayDbPerFrame)
            : loud;
    }

    private static double RmsDb(ReadOnlySpan<float> frame)
    {
        double energy = 0;
        foreach (float sample in frame)
        {
            energy += sample * sample;
        }

        double rms = Math.Sqrt(energy / frame.Length);
        return rms <= 1e-9 ? -180 : 20 * Math.Log10(rms);
    }
}
