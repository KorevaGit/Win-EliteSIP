namespace EliteSIP.Audio;

/// <summary>
/// Автоматическая регулировка усиления голоса — своя, поверх обработки WebRTC.
/// </summary>
///
/// <remarks>
/// <b>Почему своя.</b> Регуляторы WebRTC из пакета
/// <c>SoundFlow.Extensions.WebRtc.Apm</c> 1.4.0 не управляются: обёртка
/// передаёт в библиотеку только «включено». Второй регулятор без настроек —
/// один ограничитель; первый в цифровых режимах давал одни и те же +7 дБ при
/// любых числах, в аналоговом — ни разу не сдвинул рекомендацию за двадцать
/// секунд тихого голоса. Замерено 11 сентября 2026 (VoiceProcessorTests); до
/// того выключатель АРУ в настройках не делал ничего, а ручной ползунок при
/// нём гас.
///
/// <b>Как устроена.</b> Каждый кадр (10 мс) меряется уровень. Нижняя огибающая
/// уровня — это шум комнаты; кадр заметно громче неё считается речью. Уровень
/// речи сглаживается, и прибавка тянется к тому, чтобы он встал на
/// <see cref="TargetDb"/>. Тянется только на речи: в паузе прибавка
/// замирает, иначе регулятор вытягивал бы шум — то самое «дыхание», за которое
/// АРУ и не любят. Вверх медленно, вниз быстро: громкий слог обязан
/// приседать сразу, тихий голос может набирать громкость за пару секунд.
///
/// Тип чистый и без замков: вход — кадры, выход — те же кадры, проверяется
/// тестом без звуковой карты.
/// </remarks>
internal sealed class SpeechGainControl
{
    /// <summary>Куда тянуть уровень речи, дБ от полной шкалы (RMS).</summary>
    public const double TargetDb = -22;

    /// <summary>Сколько вправе добавить тихому голосу, дБ.</summary>
    public const double MaximumBoostDb = 24;

    /// <summary>Сколько вправе убрать у громкого микрофона, дБ.</summary>
    public const double MaximumCutDb = 12;

    /// <summary>Скорость подъёма, дБ за кадр: 6 дБ в секунду.</summary>
    private const double RiseDbPerFrame = 0.06;

    /// <summary>Скорость спуска, дБ за кадр: 50 дБ в секунду.</summary>
    private const double FallDbPerFrame = 0.5;

    /// <summary>Насколько кадр должен быть громче шума, чтобы считаться речью.</summary>
    private const double SpeechOverNoiseDb = 9;

    /// <summary>Тише этого не речь, а тишина, сколько бы ни было до шума.</summary>
    private const double SilenceDb = -65;

    /// <summary>Как быстро оценка шума ползёт вверх, дБ за кадр: 1 дБ в секунду.</summary>
    private const double NoiseRiseDbPerFrame = 0.01;

    /// <summary>Сглаживание уровня речи: доля нового кадра.</summary>
    private const double SpeechSmoothing = 0.05;

    /// <summary>С какого уровня начинает мягко сжиматься пик.</summary>
    private const float LimiterKnee = 0.9f;

    /// <summary>Оценка шума, дБ. С нуля: первый же кадр опустит её до себя.</summary>
    private double _noiseDb;
    private double? _speechDb;
    private double _gainDb;
    private float _appliedGain = 1f;

    /// <summary>Прибавка сейчас, дБ.</summary>
    public double GainDb => _gainDb;

    /// <summary>Регулирует кадр на месте.</summary>
    public void Process(Span<float> frame)
    {
        if (frame.IsEmpty)
        {
            return;
        }

        double levelDb = RmsDb(frame);

        // Нижняя огибающая: вниз — сразу, вверх — медленно. Так она держится на
        // шуме комнаты и не забирается на речь, даже если та идёт подряд.
        _noiseDb = levelDb < _noiseDb ? levelDb : _noiseDb + NoiseRiseDbPerFrame;

        bool isSpeech = levelDb > SilenceDb && levelDb > _noiseDb + SpeechOverNoiseDb;
        if (isSpeech)
        {
            _speechDb = _speechDb is double speech
                ? speech + ((levelDb - speech) * SpeechSmoothing)
                : levelDb;

            double wanted = Math.Clamp(TargetDb - _speechDb.Value, -MaximumCutDb, MaximumBoostDb);
            _gainDb = wanted > _gainDb
                ? Math.Min(wanted, _gainDb + RiseDbPerFrame)
                : Math.Max(wanted, _gainDb - FallDbPerFrame);
        }

        // Прибавка меняется плавно внутри кадра: скачок на границе десяти
        // миллисекунд слышен как треск.
        float target = (float)Math.Pow(10, _gainDb / 20);
        float start = _appliedGain;
        float step = (target - start) / frame.Length;

        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] = Limit(frame[i] * (start + (step * (i + 1))));
        }

        _appliedGain = target;
    }

    /// <summary>
    /// Мягкое ограничение пика: до колена — как есть, выше — плавно к единице.
    /// Жёсткий срез после прибавки звучал бы хрипом.
    /// </summary>
    internal static float Limit(float sample)
    {
        float magnitude = Math.Abs(sample);
        if (magnitude <= LimiterKnee)
        {
            return sample;
        }

        float headroom = 1f - LimiterKnee;
        float squeezed = LimiterKnee + (headroom * MathF.Tanh((magnitude - LimiterKnee) / headroom));
        return MathF.CopySign(squeezed, sample);
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
