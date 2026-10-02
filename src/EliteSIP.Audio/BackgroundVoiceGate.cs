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
/// <item>Уровень оператора меряется только по кадрам, близким к нему самому:
/// чужой голос на 20 дБ тише его не опускает, сколько бы сосед ни говорил.
/// Громкий щелчок поднимает его не больше чем на полдецибела за кадр.</item>
/// <item>Пока голос оператора не услышан (первые полсекунды речи), блок не
/// делает ничего.</item>
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

    /// <summary>Сколько кадров голоса оператора нужно услышать, прежде чем приглушать.</summary>
    private const int EstablishFrames = 50;

    /// <summary>Кадр в пределах стольких децибел от голоса оператора — его голос.</summary>
    private const double OwnVoiceBandDb = 8;

    /// <summary>Доля нового кадра в уровне голоса.</summary>
    private const double VoiceSmoothing = 0.04;

    /// <summary>Сколько уровень голоса вправе подняться за кадр, дБ.</summary>
    private const double VoiceRiseLimitDb = 0.5;

    /// <summary>
    /// Как быстро уровень голоса сползает, когда оператор стал говорить тише
    /// (отвёл штангу, убавил вход), дБ за кадр: 1 дБ в секунду.
    /// </summary>
    private const double VoiceDriftDbPerFrame = 0.01;

    /// <summary>Насколько кадр должен быть громче фона, чтобы считаться чьей-то речью.</summary>
    private const double SpeechOverNoiseDb = 10;

    /// <summary>Тише этого не речь, а тишина.</summary>
    private const double SilenceDb = -62;

    /// <summary>Как быстро оценка фона ползёт вверх, дБ за кадр: 1 дБ в секунду.</summary>
    private const double NoiseRiseDbPerFrame = 0.01;

    private double _noiseDb;
    private double? _voiceDb;
    private int _voiceFrames;
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
        if (_voiceDb is not double voice)
        {
            if (isSpeech)
            {
                _voiceDb = levelDb;
                _voiceFrames = 1;
            }

            return;
        }

        if (levelDb > voice - OwnVoiceBandDb)
        {
            // Свой голос: уровень тянется к кадру, но вверх не быстрее
            // ограничения — щелчок по столу не должен закрыть оператора.
            double step = Math.Min((levelDb - voice) * VoiceSmoothing, VoiceRiseLimitDb);
            _voiceDb = voice + step;
            _voiceFrames++;
        }
        else if (isSpeech && levelDb > voice - MarginDb - 4)
        {
            // Речь заметно тише привычного, но у самого порога — так
            // выглядит оператор, отодвинувший штангу. Сползаем к ней
            // медленно. Голоса вдвое дальше порога сюда не попадают и
            // уровень оператора не трогают.
            _voiceDb = voice - VoiceDriftDbPerFrame;
        }

        // До установления первые кадры могли быть чужими: если оператор
        // громче, уровень дотянется до него за десятые доли секунды.
        if (_voiceFrames < EstablishFrames && isSpeech && levelDb > voice)
        {
            _voiceDb = Math.Max(_voiceDb.Value, levelDb - OwnVoiceBandDb / 2);
        }
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
