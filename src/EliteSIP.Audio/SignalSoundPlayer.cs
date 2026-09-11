using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EliteSIP.Audio;

/// <summary>
/// Служебные звуки: гудки звонящему, рингтон входящего и короткие подсказки —
/// нажатие клавиши набора и отбой.
///
/// <b>Почему это не тракт разговора.</b> Тракт занят кодеком, RTP и
/// эхоподавлением, поднимается на ответ и владеет одним устройством. Гудки
/// нужны раньше, чем он поднимается, а рингтон — на устройстве, которое
/// оператор выбрал отдельно, ровно затем, чтобы услышать звонок, когда
/// гарнитура лежит на столе. Пропускать служебные звуки через тракт значило бы
/// поднимать разговор до разговора и терять этот выбор.
///
/// <b>Почему гудки рисуются, а не принимаются.</b> АТС может прислать
/// «ранние медиа» — свои гудки в RTP до ответа, — но принимать их некому:
/// медиасессия заводится на 200 OK, и до него звук из сети слушать нечем. Пока
/// это так, тишина в трубке после «Позвонить» — это тишина, а не гудки, и
/// оператор в ней слышит сломанную программу. Тон рисуется здесь: 425 Гц,
/// секунда через четыре — то, что российская телефонная сеть называет КПВ.
/// </summary>
public sealed class SignalSoundPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<string>? _log;

    private WasapiPlayer? _output;

    /// <summary>Источник, если он владеет файлом. Снимается вместе с выводом.</summary>
    private IDisposable? _sourceOwner;

    private bool _disposed;

    public SignalSoundPlayer(Action<string>? log = null) => _log = log;

    /// <summary>Играют ли гудки или рингтон прямо сейчас.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _output is not null;
            }
        }
    }

    /// <summary>Гудки звонящему. Повторный вызов ничего не меняет.</summary>
    public void StartRingback(double volume = 0.35)
        => Start(new RingbackProvider(volume), deviceId: null, "гудки");

    /// <summary>
    /// Рингтон входящего.
    /// </summary>
    ///
    /// <param name="filePath">Свой файл или <c>null</c> — рисованный звонок.</param>
    /// <param name="volume">Громкость от нуля до единицы.</param>
    /// <param name="deviceId">
    /// Устройство вывода или <c>null</c> — системное. Разница здесь не
    /// косметическая: на устройстве разговора звонок слышно только в надетой
    /// гарнитуре.
    /// </param>
    public void StartRingtone(string? filePath, double volume, string? deviceId)
    {
        ISampleProvider provider;

        if (filePath is { Length: > 0 } path && File.Exists(path))
        {
            try
            {
                // Файл читается целиком и зацикливается: `AudioFileReader`
                // сам по себе доиграет до конца и замолчит, а звонок обязан
                // звонить, пока трубку не сняли.
                provider = new LoopingFileProvider(path, volume);
            }
            catch (Exception error) when (error is IOException or FormatException
                                              or ArgumentException)
            {
                // Файл был и испортился, или это не звук вовсе. Молчать нельзя:
                // тишина вместо звонка — это пропущенный вызов.
                _log?.Invoke($"рингтон «{path}» не читается ({error.Message}) — звоню стандартным");
                provider = new RingtoneProvider(volume);
            }
        }
        else
        {
            provider = new RingtoneProvider(volume);
        }

        Start(provider, deviceId, "рингтон");
    }

    /// <summary>Снимает то, что играет. Тишина — обычное состояние, молча.</summary>
    public void Stop()
    {
        WasapiPlayer? going;
        IDisposable? owner;

        lock (_gate)
        {
            going = _output;
            owner = _sourceOwner;
            _output = null;
            _sourceOwner = null;
        }

        if (going is null)
        {
            return;
        }

        try
        {
            going.Stop();
        }
        catch (Exception error) when (error is InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
        {
            // Устройство исчезло вместе со звуком — снимать уже нечего.
        }
        finally
        {
            going.Dispose();
            owner?.Dispose();
        }
    }

    private void Start(ISampleProvider provider, string? deviceId, string what)
    {
        lock (_gate)
        {
            if (_disposed || _output is not null)
            {
                return;
            }
        }

        try
        {
            WasapiPlayer output = Open(deviceId);
            output.Init(provider);
            output.Play();

            lock (_gate)
            {
                // Пока мы открывали устройство, звонок мог кончиться: открытие
                // WASAPI занимает десятки миллисекунд, а трубку снимают быстрее.
                if (_disposed)
                {
                    output.Dispose();
                    (provider as IDisposable)?.Dispose();
                    return;
                }

                _output = output;
                _sourceOwner = provider as IDisposable;
            }
        }
        catch (Exception error)
        {
            // Отсутствие служебного звука не должно ронять звонок: без гудка
            // разговор состоится, без исключения в потоке сигнализации — нет.
            _log?.Invoke($"{what} не играют: {error.Message}");
        }
    }

    private static WasapiPlayer Open(string? deviceId)
    {
        WasapiPlayerBuilder builder = new WasapiPlayerBuilder()
            .WithSharedMode()

            // Сотня миллисекунд, а не низкая задержка: служебному звуку
            // задержка безразлична, а низкая берётся не даром — MMCSS и
            // мелкий буфер здесь заняли бы то, что нужно разговору.
            .WithLatency(100)
            .WithEventSync();

        if (Device(deviceId) is { } device)
        {
            builder = builder.WithDevice(device);
        }

        return builder.Build();
    }

    /// <summary>Устройство по имени. <c>null</c> — играть системным.</summary>
    private static MMDevice? Device(string? deviceId)
    {
        if (deviceId is not { Length: > 0 })
        {
            return null;
        }

        using MMDeviceEnumerator enumerator = new();
        try
        {
            MMDevice device = enumerator.GetDevice(deviceId);

            // Устройства может уже не быть — гарнитуру выдернули между
            // настройкой и звонком. Звонить системным лучше, чем не звонить.
            return device.State is DeviceState.Active ? device : null;
        }
        catch (Exception error) when (error is ArgumentException
                                          or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Звук нажатия клавиши набора. Клавиши вне набора молчат.</summary>
    /// <param name="deviceId">Устройство разговора или <c>null</c> — системное.</param>
    public void PlayKeyTone(char key, string? deviceId)
    {
        if (KeyToneProvider.For(key) is { } tone)
        {
            PlayCue(tone, deviceId);
        }
    }

    /// <summary>Звук завершения разговора.</summary>
    /// <param name="deviceId">Устройство разговора или <c>null</c> — системное.</param>
    public void PlayHangUp(string? deviceId) => PlayCue(new HangUpToneProvider(), deviceId);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Stop();

        lock (_cueGate)
        {
            CloseCuesLocked();
        }
    }

    // MARK: - Подсказки: нажатие клавиши, отбой

    /// <summary>
    /// Сколько держать устройство подсказок открытым после последней.
    ///
    /// Открытие потока WASAPI стоит десятков миллисекунд, и на каждое нажатие
    /// клавиши это слышно как запаздывание звука за пальцем. Держать его вечно
    /// тоже нельзя: открытый вывод не даёт Bluetooth-гарнитуре уйти в сон.
    /// Три секунды покрывают набор номера целиком.
    /// </summary>
    private static readonly TimeSpan CueIdleClose = TimeSpan.FromSeconds(3);

    private readonly object _cueGate = new();
    private WasapiPlayer? _cueOutput;
    private MixingSampleProvider? _cueMixer;
    private string? _cueDeviceId;
    private Timer? _cueIdle;

    /// <summary>
    /// Играет короткий звук поверх того, что уже играет.
    ///
    /// Отдельно от гудков и рингтона: у тех вывод один на звук и второй не
    /// начинается, пока играет первый, а подсказки идут пачкой — пять цифр за
    /// секунду — и обязаны накладываться. Поэтому здесь смеситель на одном
    /// выводе, а не вывод на каждую подсказку.
    /// </summary>
    private void PlayCue(ISampleProvider cue, string? deviceId)
    {
        lock (_cueGate)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            try
            {
                if (_cueOutput is null || _cueDeviceId != deviceId)
                {
                    CloseCuesLocked();

                    // ReadFully: смеситель без входов отдаёт тишину, а не
                    // короткий кадр, — иначе вывод остановится после первой
                    // же подсказки.
                    MixingSampleProvider mixer = new(cue.WaveFormat) { ReadFully = true };
                    WasapiPlayer output = Open(deviceId);
                    output.Init(mixer);
                    output.Play();

                    _cueMixer = mixer;
                    _cueOutput = output;
                    _cueDeviceId = deviceId;
                }

                _cueMixer!.AddMixerInput(cue);

                _cueIdle?.Dispose();
                _cueIdle = new Timer(
                    _ =>
                    {
                        lock (_cueGate)
                        {
                            CloseCuesLocked();
                        }
                    },
                    state: null,
                    dueTime: CueIdleClose,
                    period: Timeout.InfiniteTimeSpan);
            }
            catch (Exception error)
            {
                // Подсказка — не то, из-за чего можно уронить набор номера.
                _log?.Invoke($"звук подсказки не играет: {error.Message}");
                CloseCuesLocked();
            }
        }
    }

    private void CloseCuesLocked()
    {
        _cueIdle?.Dispose();
        _cueIdle = null;

        WasapiPlayer? output = _cueOutput;
        _cueOutput = null;
        _cueMixer = null;
        _cueDeviceId = null;

        if (output is null)
        {
            return;
        }

        try
        {
            output.Stop();
        }
        catch (Exception error) when (error is InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
        {
            // Устройство исчезло — останавливать нечего.
        }
        finally
        {
            output.Dispose();
        }
    }


    /// <summary>Общее у всех рисованных сигналов: моно, 48 кГц.</summary>
    private abstract class ToneProvider(double volume) : ISampleProvider
    {
        protected const int Rate = 48_000;

        private readonly float _volume = (float)Math.Clamp(volume, 0, 1);

        private long _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        /// <summary>Отсчёт сигнала до громкости. <c>null</c> — сигнал кончился.</summary>
        protected abstract double? ValueAt(long sample);

        public int Read(Span<float> buffer)
        {
            for (int index = 0; index < buffer.Length; index++)
            {
                if (ValueAt(_position) is not double value)
                {
                    // Короткий кадр — это конец: смеситель снимет источник.
                    return index;
                }

                buffer[index] = (float)(value * _volume);
                _position++;
            }

            return buffer.Length;
        }

        /// <summary>Сумма синусоид в момент <paramref name="sample"/>.</summary>
        protected static double Partials(long sample, ReadOnlySpan<(double Hertz, double Amplitude)> partials)
        {
            double value = 0;
            foreach ((double hertz, double amplitude) in partials)
            {
                value += amplitude * Math.Sin(2 * Math.PI * hertz * sample / Rate);
            }

            return value;
        }

        /// <summary>
        /// Плавные края тона длиной <paramref name="length"/>: обрыв синусоиды
        /// на ненулевой фазе слышен как щелчок, и он раздражает сильнее самого
        /// звука.
        /// </summary>
        protected static double Envelope(long position, long length, long ramp)
            => Math.Min(1.0, Math.Min((double)position / ramp, (double)(length - position) / ramp));
    }

    /// <summary>КПВ: 425 Гц, секунда звучания через четыре секунды тишины.</summary>
    private sealed class RingbackProvider(double volume) : ToneProvider(volume)
    {
        private const int PeriodSamples = Rate * 5;
        private const int ToneSamples = Rate;
        private const int Ramp = Rate / 100;

        protected override double? ValueAt(long sample)
        {
            long inPeriod = sample % PeriodSamples;
            return inPeriod >= ToneSamples
                ? 0
                : Math.Sin(2 * Math.PI * 425 * sample / Rate) * Envelope(inPeriod, ToneSamples, Ramp);
        }
    }

    /// <summary>
    /// Стандартный звонок — тот же, что в macOS-версии: два коротких тона и
    /// длинная пауза.
    ///
    /// До 11 сентября 2026 здесь стояла своя трель — чистые 880 и 660 Гц
    /// попеременно, две секунды подряд из пяти, на полной громкости настройки.
    /// Оператор назвал её «очень громкой и противной» и попросил звонок, как
    /// на Mac. Причины оригинала те же и здесь: пауза длинная, потому что
    /// рингтон звучит на фоне работы в CRM и непрерывная трель мешает думать;
    /// две гармоники вместо одной, потому что чистая синусоида на колонках
    /// ноутбука звучит как неисправность; общий уровень — половина шкалы.
    /// </summary>
    private sealed class RingtoneProvider(double volume) : ToneProvider(volume)
    {
        private const int ToneSamples = Rate * 4 / 10;
        private const int GapSamples = Rate * 2 / 10;
        private const int SilenceSamples = Rate * 2;
        private const int PeriodSamples = (ToneSamples * 2) + GapSamples + SilenceSamples;
        private const int Ramp = Rate * 12 / 1000;

        /// <summary>Ре и ля пятой октавы — из оригинала.</summary>
        private static readonly (double, double)[] Chord = [(587.33, 0.6), (880.00, 0.4)];

        protected override double? ValueAt(long sample)
        {
            long inPeriod = sample % PeriodSamples;
            long inTone = inPeriod < ToneSamples
                ? inPeriod
                : inPeriod - ToneSamples - GapSamples;

            if (inTone < 0 || inTone >= ToneSamples)
            {
                return 0;
            }

            return Partials(inTone, Chord) * Envelope(inTone, ToneSamples, Ramp) * 0.5;
        }
    }

    /// <summary>
    /// Нажатие клавиши при наборе номера: пара частот DTMF этой клавиши,
    /// коротко и едва слышно.
    ///
    /// Частоты DTMF, а не щелчок: так звучит набор на любом телефоне, и ухо
    /// узнаёт его, не отвлекаясь. Уровень около −26 дБ от полной шкалы —
    /// подтверждение нажатия, а не сигнал.
    /// </summary>
    private sealed class KeyToneProvider((double Hertz, double Amplitude)[] pair) : ToneProvider(1)
    {
        private const int LengthSamples = Rate * 70 / 1000;
        private const int Ramp = Rate * 8 / 1000;

        protected override double? ValueAt(long sample) => sample >= LengthSamples
            ? null
            : Partials(sample, pair) * Envelope(sample, LengthSamples, Ramp);

        public static KeyToneProvider? For(char key)
        {
            // Строки и столбцы клавиатуры DTMF (ITU-T Q.23).
            (int row, int column) = key switch
            {
                '1' => (0, 0), '2' => (0, 1), '3' => (0, 2),
                '4' => (1, 0), '5' => (1, 1), '6' => (1, 2),
                '7' => (2, 0), '8' => (2, 1), '9' => (2, 2),
                '*' => (3, 0), '0' or '+' => (3, 1), '#' => (3, 2),
                _ => (-1, -1),
            };

            if (row < 0)
            {
                return null;
            }

            double[] rows = [697, 770, 852, 941];
            double[] columns = [1209, 1336, 1477];
            return new KeyToneProvider([(rows[row], 0.025), (columns[column], 0.025)]);
        }
    }

    /// <summary>
    /// Отбой: две мягкие ноты вниз, ре и ля, как в звонке, — только тише и
    /// короче. Подтверждает «разговор кончился», не требуя смотреть на экран.
    /// </summary>
    private sealed class HangUpToneProvider() : ToneProvider(1)
    {
        private const int NoteSamples = Rate * 12 / 100;
        private const int Ramp = Rate * 10 / 1000;

        private static readonly (double, double)[] High = [(587.33, 0.07)];
        private static readonly (double, double)[] Low = [(440.00, 0.07)];

        protected override double? ValueAt(long sample)
        {
            if (sample >= NoteSamples * 2)
            {
                return null;
            }

            long inNote = sample % NoteSamples;
            return Partials(inNote, sample < NoteSamples ? High : Low)
                * Envelope(inNote, NoteSamples, Ramp);
        }
    }

    /// <summary>Файл оператора, зацикленный: звонок звонит, пока не снимут.</summary>
    private sealed class LoopingFileProvider : ISampleProvider, IDisposable
    {
        private readonly AudioFileReader _reader;
        private readonly ISampleProvider _source;

        public LoopingFileProvider(string path, double volume)
        {
            _reader = new AudioFileReader(path) { Volume = (float)Math.Clamp(volume, 0, 1) };

            // В моно, если файл стерео: устройство звонка может быть каким
            // угодно, а сводить каналы умеет NAudio, а не мы.
            _source = _reader.WaveFormat.Channels > 1
                ? new StereoToMonoSampleProvider(_reader)
                : _reader;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            int filled = 0;

            while (filled < buffer.Length)
            {
                int read = _source.Read(buffer[filled..]);
                if (read == 0)
                {
                    if (_reader.Position == 0)
                    {
                        // Файл пуст: крутить его в пустом цикле — это занятый
                        // поток звука и тишина в динамике.
                        break;
                    }

                    _reader.Position = 0;
                    continue;
                }

                filled += read;
            }

            // Хвост добивается тишиной, а не коротким кадром: короткий кадр
            // WASAPI понимает как «источник кончился» и останавливает вывод.
            buffer[filled..].Clear();

            return buffer.Length;
        }

        public void Dispose() => _reader.Dispose();
    }
}
