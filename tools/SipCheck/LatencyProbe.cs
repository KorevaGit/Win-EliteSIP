using System.Globalization;
using EliteSIP.MediaCore;

namespace EliteSIP.Tools.SipCheck;

/// <summary>
/// Замер задержки разговора звуком, прошедшим весь путь.
///
/// <b>Зачем, если сессия и так складывает свои буферы.</b> Сумма буферов —
/// это наша оценка самих себя: она считается из наших же счётчиков и повторяет
/// наши же заблуждения. Здесь меряется по-другому: то, что мы отправили в
/// линию, сравнивается с тем, что вернулось из неё, и сдвиг между ними и есть
/// задержка. Обе огибающие сняты с одних часов — часов звуковой карты, —
/// поэтому сравнение честное.
///
/// <b>Что именно измеряется.</b> Звонок делается на эхо-номер лаборатории
/// (650): он возвращает нам наш же звук. Полученная петля — это путь «наш
/// кодер → сеть → сервер → сеть → наш джиттер-буфер», без микрофона и динамика.
/// Задержка устройств живёт в разборе от сессии, и сложение двух чисел даёт
/// полную картину рот-в-ухо, у которой обе половины измерены независимо.
///
/// Метод годится и без лаборатории: любой эхо-сервис даст то же самое.
/// </summary>
internal sealed class LatencyProbe
{
    /// <summary>Длина окна корреляции. Почему короткое — см. Measure.</summary>
    private const int WindowSeconds = 4;

    /// <summary>Где брать окна: в начале разговора, в середине и ближе к концу.</summary>
    private static readonly double[] WindowPositions = [0.2, 0.5, 0.8];

    /// <summary>
    /// Дальше какой задержки не ищем.
    ///
    /// Секунда — это уже не задержка, а поломка, и искать её среди случайных
    /// совпадений незачем: чем шире окно поиска, тем выше случайный пик.
    /// </summary>
    private const int MaximumLagMilliseconds = 1000;

    private readonly AudioFrameDecoder _decoder;
    private readonly int _sampleRate;
    private readonly int _samplesPerStep;
    private readonly Lock _gate = new();

    private readonly List<float> _sent = [];
    private readonly List<float> _received = [];

    /// <summary>
    /// Общие часы обеих половин.
    ///
    /// <b>Без них замер неверен, и первый живой прогон это показал.</b> Два ряда
    /// чисел начинаются в разные моменты: приём начинает накапливаться тогда,
    /// когда пришёл первый кадр. На эхо-номере лаборатории сервер сначала
    /// выжидает секунду (<c>Wait(1)</c> в плане набора) и всё это время наш звук
    /// выбрасывает — значит, в принятом недостаёт ровно секунды начала.
    /// Сравнение по номерам элементов дало на этом сдвиг −1020 мс при пике 0,98:
    /// совпадение идеальное, число бессмысленное.
    ///
    /// Поэтому у каждой половины запоминается момент её первого отсчёта, и
    /// сдвиг индексов переводится во время сложением с разницей этих моментов.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private double? _sentStartedAt;
    private double? _receivedStartedAt;

    /// <summary>Остатки, не набравшие полный шаг огибающей.</summary>
    private readonly List<short> _sentTail = [];
    private readonly List<short> _receivedTail = [];

    public LatencyProbe(AudioCodec codec)
    {
        _decoder = new AudioFrameDecoder(codec);
        _sampleRate = (int)codec.SampleRate();
        _samplesPerStep = AudioDelayEstimator.SamplesPerStep(_sampleRate);
    }

    /// <summary>Хватит ли уже накопленного, чтобы считать.</summary>
    public bool HasEnough
    {
        get
        {
            lock (_gate)
            {
                return _sent.Count * AudioDelayEstimator.DefaultStepMilliseconds >= (WindowSeconds * 1000) + MaximumLagMilliseconds
                    && _received.Count >= _sent.Count;
            }
        }
    }

    /// <summary>Кадр, ушедший в линию. Приходит с потока захвата — не задерживать.</summary>
    public void NoteSent(ReadOnlyMemory<byte> payload) =>
        Append(_sentTail, _sent, _decoder.Decode(payload.Span), outgoing: true);

    /// <summary>Отсчёты, пришедшие из линии.</summary>
    public void NoteReceived(short[] samples) => Append(_receivedTail, _received, samples, outgoing: false);

    /// <summary>
    /// Средняя громкость обеих половин замера.
    ///
    /// Печатается при неудаче, и это первое, что надо знать: молчит ли наша
    /// сторона (в линию ничего не уходит) или чужая (вернулось пусто). Без этих
    /// двух чисел «не нашлась» — это приговор без улик.
    /// </summary>
    public (double Sent, double Received) Levels
    {
        get
        {
            lock (_gate)
            {
                return (Average(_sent), Average(_received));
            }
        }
    }

    /// <summary>
    /// Считает задержку по накопленному.
    ///
    /// <b>Считается по коротким окнам, а не по всему разговору, и это не
    /// экономия.</b> Отправка тактуется часами захвата, воспроизведение —
    /// часами вывода, и это разные кварцы: замер W4 давал между ними до сотни
    /// ppm. На сорока пяти секундах сотня ppm — это четыре с половиной
    /// миллисекунды расхождения, а вместе с подстройкой темпа воспроизведения
    /// набегает и больше. Единого сдвига на таком окне уже не существует, и
    /// корреляция честно не находит ничего: первый живой прогон дал пик 0,10
    /// при полностью исправном эхе.
    ///
    /// Окно в четыре секунды короче, чем время заметного ухода, и длиннее, чем
    /// самый длинный слог проверочного сигнала. Окон берётся три, в разных
    /// местах разговора: одно может попасть на паузу или на всплеск джиттера, а
    /// три подряд — вряд ли. Отдаётся самое уверенное.
    /// </summary>
    public AudioDelayEstimate? Measure()
    {
        float[] sent;
        float[] received;
        lock (_gate)
        {
            sent = [.. _sent];
            received = [.. _received];
        }

        int windowSteps = WindowSeconds * 1000 / AudioDelayEstimator.DefaultStepMilliseconds;
        int lagSteps = MaximumLagMilliseconds / AudioDelayEstimator.DefaultStepMilliseconds;

        // Длины двух половин не совпадают и не обязаны: отправляет захват,
        // принимает вывод, и на конце разговора у одного из них всегда на
        // несколько кадров больше. Сравнивать можно ровно там, где есть обе.
        int available = Math.Min(sent.Length, received.Length);
        if (available < windowSteps + lagSteps)
        {
            return null;
        }

        // Разница моментов, с которых начались обе половины. Сдвиг индексов сам
        // по себе про время не говорит ничего: см. поле с часами.
        double offset;
        lock (_gate)
        {
            if (_sentStartedAt is not double sentStart || _receivedStartedAt is not double receivedStart)
            {
                return null;
            }

            offset = receivedStart - sentStart;
        }

        AudioDelayEstimate? best = null;
        foreach (double position in WindowPositions)
        {
            int start = (int)((available - windowSteps - lagSteps) * position);
            AudioDelayEstimate? found = AudioDelayEstimator.Estimate(
                sent.AsSpan(start, windowSteps + lagSteps),
                received.AsSpan(start, windowSteps + lagSteps),
                AudioDelayEstimator.DefaultStepMilliseconds,
                MaximumLagMilliseconds);

            if (found is not null && (best is null || found.Confidence > best.Confidence))
            {
                best = found with { Milliseconds = found.Milliseconds + offset };
            }
        }

        return best;
    }

    /// <summary>
    /// Складывает обе огибающие в файл — по столбцу на каждую.
    ///
    /// Пишется при неудачном замере, и это не отладочный остаток. «Не нашлась»
    /// без данных — приговор без улик: по двум столбцам видно за минуту, что
    /// именно не совпало, а восстановить их потом неоткуда. Файл текстовый
    /// намеренно: открывается чем угодно, вплоть до электронной таблицы.
    /// </summary>
    public string Dump(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        float[] sent;
        float[] received;
        lock (_gate)
        {
            sent = [.. _sent];
            received = [.. _received];
        }

        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"latency-{DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)}.csv");

        using StreamWriter writer = new(path);
        writer.WriteLine("шаг;отправлено;принято");
        for (int index = 0; index < Math.Max(sent.Length, received.Length); index++)
        {
            string outgoing = index < sent.Length
                ? sent[index].ToString("F0", CultureInfo.InvariantCulture)
                : string.Empty;
            string incoming = index < received.Length
                ? received[index].ToString("F0", CultureInfo.InvariantCulture)
                : string.Empty;
            writer.WriteLine($"{index.ToString(CultureInfo.InvariantCulture)};{outgoing};{incoming}");
        }

        return path;
    }

    /// <summary>Готовит следующий замер, не теряя уже посчитанного.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _sent.Clear();
            _received.Clear();
            _sentTail.Clear();
            _receivedTail.Clear();
        }
    }

    /// <summary>
    /// Печатает итог: измеренная петля рядом с оценкой по буферам.
    ///
    /// Рядом намеренно. Каждое число по отдельности убедительно и может быть
    /// неверным; расходятся они только тогда, когда где-то есть задержка, о
    /// которой мы не знаем, — а это и есть то, ради чего замер делается.
    /// </summary>
    public static void Report(
        AudioDelayEstimate? measured,
        EliteSIP.Audio.MediaLatency estimated,
        (double Sent, double Received) levels)
    {
        ArgumentNullException.ThrowIfNull(estimated);

        Console.WriteLine($"   задержка по буферам: {estimated.Summary}");

        if (measured is null || !measured.IsConfident)
        {
            // Печатается и неуверенный ответ: по нему видно, искали ли вообще.
            // «Пик 0,18» и «нечего сравнивать» — разные неисправности.
            if (measured is null)
            {
                Console.WriteLine("   задержка по звуку: сравнивать нечего — ни одного кадра не собрано");
                return;
            }

            string best = string.Create(
                CultureInfo.InvariantCulture,
                $"лучший пик {measured.Confidence:F2} на {measured.Milliseconds:F0} мс");

            string heard = string.Create(
                CultureInfo.InvariantCulture,
                $"средняя громкость: отправляли {levels.Sent:F0}, приняли {levels.Received:F0}");

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"   задержка по звуку: не нашлась ({best}, нужно от "
                    + $"{AudioDelayEstimator.MinimumConfidence:F2}); {heard}"));
            return;
        }

        // Петля — это путь туда и обратно между нашим кодером и нашим буфером.
        // Половина её — это то, что сеть и сервер добавляют в одну сторону.
        double oneWay = measured.Milliseconds / 2;
        double mouthToEar = estimated.CaptureMilliseconds + estimated.PlaybackMilliseconds + oneWay;

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   задержка по звуку: петля {measured.Milliseconds:F0} мс (совпадение {measured.Confidence:F2}), "
                + $"в одну сторону {oneWay:F0} мс"));

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   рот-в-ухо: {mouthToEar:F0} мс — захват {estimated.CaptureMilliseconds:F0} + "
                + $"путь до собеседника и обратно к нам {oneWay:F0} + вывод {estimated.PlaybackMilliseconds:F0}"));

        // Норма ITU-T G.114: до 150 мс в одну сторону разговор не страдает,
        // после 300 собеседники начинают перебивать друг друга. Порог назван
        // вслух, чтобы число не пришлось нести в справочник.
        Console.WriteLine(mouthToEar switch
        {
            < 150 => "   [v] в норме разговора (ITU-T G.114: до 150 мс)",
            < 300 => "   [!] заметно, но терпимо (ITU-T G.114: 150–300 мс)",
            _ => "   [x] много: на такой задержке собеседники перебивают друг друга",
        });
    }

    private static double Average(List<float> envelope) =>
        envelope.Count == 0 ? 0 : envelope.Sum() / envelope.Count;

    private void Append(List<short> tail, List<float> envelope, short[] samples, bool outgoing)
    {
        lock (_gate)
        {
            if (outgoing)
            {
                _sentStartedAt ??= _clock.Elapsed.TotalMilliseconds;
            }
            else
            {
                _receivedStartedAt ??= _clock.Elapsed.TotalMilliseconds;
            }

            tail.AddRange(samples);

            int steps = tail.Count / _samplesPerStep;
            if (steps == 0)
            {
                return;
            }

            short[] whole = [.. tail.Take(steps * _samplesPerStep)];
            tail.RemoveRange(0, steps * _samplesPerStep);
            envelope.AddRange(AudioDelayEstimator.BuildEnvelope(whole, _samplesPerStep));
        }
    }
}
