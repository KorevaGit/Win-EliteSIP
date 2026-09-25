using System.Diagnostics;
using System.Runtime.InteropServices;
using EliteSIP.MediaCore;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EliteSIP.Audio;

/// <summary>Тракт не поднялся или развалился.</summary>
public sealed class VoiceAudioException : Exception
{
    public VoiceAudioException(string message)
        : base(message)
    {
    }

    public VoiceAudioException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public VoiceAudioException()
    {
    }
}

/// <summary>
/// Тракт разговора на WASAPI: микрофон, обработка голоса, кодек,
/// воспроизведение.
///
/// <b>Что здесь переносится, а что написано заново.</b> Из оригинала перенесены
/// решения: один тракт на приложение, обработчики под одним замком, политика
/// перезапуска, отдельный поток подачи вместо толчка из потока вывода. Написано
/// заново всё, что на macOS делала система: два независимых потока вместо
/// одного узла ввода-вывода, пересчёт частоты, обработка голоса,
/// и компенсация расхождения часов, которой на агрегатном устройстве не
/// требовалось.
///
/// <b>Три потока, и у каждого своя причина.</b>
///
/// <list type="number">
/// <item><b>Захват.</b> Просыпается по событию устройства — замер W0 показал
/// период 10,00 мс с разбросом ±1,6 мс в худшем случае и без потерь, так что
/// ни <c>Sleep</c>, ни <c>timeBeginPeriod</c> тракту не нужны. Он же гонит весь
/// путь до сети: обработка голоса обязана видеть кадры в том порядке, в каком
/// их отдало устройство.</item>
/// <item><b>Вывод.</b> Отдаёт устройству то, что лежит в кольце. Ничего не
/// решает и никого не ждёт: пропущенный такт здесь — это щелчок.</item>
/// <item><b>Подача.</b> Спрашивает у приложения кадры и наполняет кольцо.
/// Отдельно от вывода намеренно — решение перенесено из оригинала: декодирование
/// и пересчёт на потоке вывода означали бы, что медленный кадр съедает такт
/// устройства.</item>
/// </list>
///
/// <b>Про сырой режим захвата.</b> Он есть в настройках и по умолчанию
/// выключен. Замер W0: с ним уровень микрофона перестал гулять (от −41,5 до
/// −43,1 дБ на девяти прогонах вместо разброса в 26 дБ) — то есть системная
/// обработка действительно была источником нестабильности. Но подавление эха он
/// не улучшил, а выключает он и то полезное, что делает система. Рабочее место
/// оператора — гарнитура, где эха нет по построению, поэтому по умолчанию
/// остаётся системная обработка.
///
/// <b>Про объявленную задержку.</b> Она считается на каждом кадре из того, что
/// видно: запас кольца, буфер устройства, не разобранный захват и объявленная
/// латентность потоков. Константой её объявлять нельзя — замер W0 дал 37,6 дБ в
/// одном прогоне и 2,7 дБ в следующем на одном и том же числе.
///
/// Замер W0 показывал также, что расчёт даёт 60 мс, а пик подавления при
/// переборе пришёлся на 160. <b>Сотню миллисекунд разницы сюда не заложено, и
/// это осознанно:</b> тот перебор делался на тракте, в котором, как выяснилось
/// позже, эха не было вовсе — микрофон гарнитуры стоял на нуле громкости, а
/// динамики на 34%. Значит и «пик на 160 мс» был шумом измерения, и подпирать
/// им продукт нельзя. Настоящая проверка задержки — приёмка на живой машине с
/// громкими динамиками, где эхо заведомо есть.
/// </summary>
public sealed class WasapiVoiceAudioEngine : IVoiceAudioEngine
{
    /// <summary>
    /// Сколько кольцо воспроизведения держит сверх целевого запаса.
    ///
    /// Вчетверо: сеть отдаёт кадры пачками после каждой заминки, и кольцо, в
    /// которое пачка не влезает, теряет её целиком.
    /// </summary>
    private const int RingHeadroomFactor = 4;

    /// <summary>
    /// Затухание последнего отсчёта при недоборе, на отсчёт: около 4 мс до
    /// тишины на 48 кГц. Короче — снова ступенька, длиннее — гул.
    /// </summary>
    private const float TailDecay = 0.995f;

    /// <summary>
    /// Через сколько после подъёма тракта молчащий микрофон признаётся
    /// молчащим. Две с половиной секунды — устройство успевает разогнаться, а
    /// оператор ещё не начал говорить «алло, вы меня слышите?».
    /// </summary>
    private static readonly TimeSpan SilenceWatch = TimeSpan.FromSeconds(2.5);

    /// <summary>Ниже этого пика (−100 дБ) микрофон считается цифровым нулём.</summary>
    private const float SilentPeak = 1e-5f;

    /// <summary>
    /// Микрофоны, замолчавшие за время работы программы. Общие на все тракты:
    /// следующий звонок идёт сразу на рабочий микрофон, а не молчит заново
    /// две с половиной секунды.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> SilentInputs = new();

    /// <summary>
    /// Микрофон, на который тракт переключился сам. Держится, пока жив этот
    /// тракт, — пересборка после смены устройств его не забывает.
    /// </summary>
    private string? _inputOverrideId;

    private string? _inputDeviceId;
    private string? _inputDeviceName;
    private string? _outputDeviceName;

    /// <summary>
    /// Сколько остановка ждёт замок, прежде чем оставить разбор пересборке.
    ///
    /// Две секунды — с запасом на исправное устройство, где вся сборка стоит
    /// 511–626 мс, и заведомо меньше того, на что способен умирающий Bluetooth.
    /// Ждать дольше значит вернуть ту самую задержку отбоя, ради которой всё и
    /// сделано.
    /// </summary>
    private static readonly TimeSpan StopLockWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Сколько раз пробовать поднять тракт при запуске.
    ///
    /// <b>Случай наблюдён, а не выдуман.</b> Сразу после того, как устройство
    /// освободил другой процесс, <c>Initialize</c> вернул
    /// <c>0x8007000E</c> — нехватку ресурсов, — и звонок не поднялся. Через
    /// секунду тот же вызов прошёл. Для оператора это выглядит как «позвонил
    /// сразу после закрытия другой программы, и связи нет»: он не знает, что
    /// надо подождать, и не должен знать.
    ///
    /// Три попытки, а не серия с запасом терпения, как при пересборке: там
    /// разговор уже идёт и его жалко, здесь человек ждёт гудка и лишние
    /// секунды тишины хуже честного отказа.
    /// </summary>
    private const int StartAttempts = 3;

    /// <summary>Пауза между попытками запуска.</summary>
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Отказ, который через секунду может пройти сам.
    ///
    /// Список узкий намеренно: «микрофона нет вовсе» повторять бессмысленно, и
    /// звонок в этом случае обязан отказать сразу, а не заставлять человека
    /// ждать секунду впустую.
    /// </summary>
    private static bool IsTransient(Exception error) => error is COMException com
        && com.HResult is
            unchecked((int)0x8007000E)   // ресурсы: устройство ещё держит другой процесс
            or unchecked((int)0x88890004) // устройство отключено или перенастроено
            or unchecked((int)0x8889000A); // устройство занято монопольно

    private readonly Lock _control = new();
    private readonly Lock _ring = new();

    /// <summary>
    /// Замок блока обработки: его зовут потоки захвата и вывода, а подменяет
    /// переключатель в настройках. Отдельный от <see cref="_control"/>:
    /// потоки звука не должны ждать пересборки тракта, а она держит тот.
    /// </summary>
    private readonly Lock _processing = new();

    /// <summary>Вывод без пути до микрофона — наушники или гарнитура.</summary>
    private bool _echoFree;

    private VoiceAudioConfiguration _configuration;
    private VoiceAudioHandlers _handlers = VoiceAudioHandlers.None;

    private MMDevice? _inputDevice;
    private MMDevice? _outputDevice;
    private AudioClient? _captureClient;
    private AudioClient? _renderClient;
    private EventWaitHandle? _captureReady;
    private EventWaitHandle? _renderReady;
    private CancellationTokenSource? _stop;
    private Thread? _captureThread;
    private Thread? _renderThread;
    private Thread? _feedThread;

    private VoiceProcessor? _processor;
    private Resampler? _captureToProcessing;
    private Resampler? _processingToCodec;
    private Resampler? _codecToRender;
    private Resampler? _renderToProcessing;
    private SampleRing? _playbackRing;
    private AudioFrameEncoder? _encoder;
    private AudioFrameDecoder? _decoder;

    private SampleBalance? _balance;
    private DeviceActivityWatch? _captureActivity;
    private PlaybackRateController? _rateController;
    private readonly ClockDriftEstimator _drift = new();
    private readonly Stopwatch _uptime = new();

    private WaveFormat? _captureFormat;
    private WaveFormat? _renderFormat;

    /// <summary>Разрядность устройств. Разбирается один раз при подъёме тракта.</summary>
    private SampleLayout _captureLayout;
    private SampleLayout _renderLayout;
    private int _renderPadding;
    private int _capturePendingSamples;
    private int _latencyFrames;
    private int _lastDelayMilliseconds;
    private double _correctionCarry;
    private double _lastClockSample;
    private AudioDeviceWatch? _watch;
    private RestartSupervisor? _supervisor;

    /// <summary>
    /// Пики уровня с подъёма тракта: сырой микрофон, ушедшее в линию и
    /// отданное наушникам.
    ///
    /// Три точки, а не одна, потому что жалоба «не слышно» без них не
    /// разбирается: тишина на микрофоне — это устройство, тишина после
    /// обработки — шумодав или АРУ, тишина в наушниках при принятых пакетах —
    /// то, что прислал собеседник. Пишет каждую свой поток, читает журнал; гонка
    /// здесь стоит одного неточного числа в строке диагностики.
    /// </summary>
    private float _capturePeak;
    private float _sentPeak;
    private float _playedPeak;

    /// <summary>
    /// Те же две точки — «в линию» и «в наушники», — но окном для шкал, а не
    /// с начала работы: <see cref="TakeLevels"/> забирает их и обнуляет.
    /// </summary>
    ///
    /// <remarks>
    /// Отдельно от пиков журнала, потому что те копятся до пересборки и
    /// отвечают на вопрос «было ли громко», а шкале нужно «громко ли сейчас».
    /// Запись без замка: окно, в которое читатель и писатель попали разом,
    /// теряет одну двадцатую секунды на шкале, и ничего больше.
    /// </remarks>
    private float _sentMeter;
    private float _playedMeter;

    /// <summary>
    /// Пики по каналам захвата — до сведения в моно.
    ///
    /// Сведение усреднением гасит каналы в противофазе: у части наборов
    /// микрофонов Realtek так и приходит. Тогда «микрофон: тишина» в журнале
    /// стоит при живом устройстве, и отличить это от мёртвого входа можно
    /// только по каналам.
    /// </summary>
    private float[] _captureChannelPeaks = [];

    /// <summary>
    /// Сколько отсчётов микрофон отдал у самого потолка. Перегруз случается
    /// ещё в устройстве — громкость входа на 100 % у LifeChat, — и после него
    /// голос хрипит, что бы ни делал тракт. Лечится ручкой входа в Windows, и
    /// журнал обязан на это указать.
    /// </summary>
    private int _captureOverloads;

    /// <summary>
    /// Чем тракт связан с устройствами: что просили в настройках и что
    /// досталось на самом деле.
    ///
    /// Пишется при сборке, читается с рабочего потока звуковой службы, и потому
    /// целиком одной записью: разобранная на поля, она читалась бы наполовину
    /// обновлённой — с новым микрофоном и старым выходом, — а по такой смеси
    /// фильтр уведомлений ответил бы неверно.
    /// </summary>
    private AudioRouteBinding _binding = AudioRouteBinding.None;

    private bool _running;
    private bool _disposed;

    /// <summary>
    /// Тракт просили остановить. Читается путями, которые могут не дождаться
    /// замка, поэтому отдельно от <see cref="_running"/>.
    /// </summary>
    private bool _stopRequested;

    public WasapiVoiceAudioEngine(VoiceAudioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public VoiceAudioHandlers Handlers
    {
        // Ссылка целиком, а не пять свойств: см. VoiceAudioHandlers. Чтение и
        // запись атомарны, поэтому поток звука никогда не видит набор
        // наполовину заменённым.
        get => Volatile.Read(ref _handlers);
        set => Volatile.Write(ref _handlers, value ?? VoiceAudioHandlers.None);
    }

    /// <summary>Сходится ли баланс отсчётов. <c>null</c> — тракт не запускался.</summary>
    public SampleBalance? Balance => _balance;

    /// <summary>Что происходит с микрофоном на самом деле.</summary>
    public DeviceActivity CaptureActivity =>
        _captureActivity is null ? DeviceActivity.Warmup : _captureActivity.Assess(_uptime.Elapsed);

    /// <summary>Задержка, объявленная эхоподавителю на последнем кадре.</summary>
    public int DeclaredDelayMilliseconds => Volatile.Read(ref _lastDelayMilliseconds);

    /// <inheritdoc/>
    ///
    /// <remarks>
    /// Считается из тех же величин, что и задержка для эхоподавителя, но по
    /// половинам: их складывают в разных местах и лечат разным. Числа берутся
    /// мгновенным снимком без замка на кольце — цена точности здесь ноль, а
    /// брать замок кольца ради диагностики значило бы вставать в очередь к
    /// потоку вывода.
    /// </remarks>
    public AudioLatencySnapshot Latency
    {
        get
        {
            int processingRate = _processor?.SampleRate ?? (int)_configuration.Codec.SampleRate();
            if (processingRate <= 0)
            {
                return default;
            }

            int ringFill = _playbackRing?.Available ?? 0;
            int renderRate = _renderFormat?.SampleRate ?? processingRate;
            double toProcessing = (double)processingRate / renderRate;

            double playbackSamples = (ringFill + Volatile.Read(ref _renderPadding)) * toProcessing;
            double captureSamples = Volatile.Read(ref _capturePendingSamples) + _latencyFrames;

            return new AudioLatencySnapshot(
                captureSamples * 1000.0 / processingRate,
                playbackSamples * 1000.0 / processingRate);
        }
    }

    /// <inheritdoc/>
    ///
    /// <remarks>
    /// До 11 сентября 2026 настройки доезжали до тракта только при его
    /// подъёме: ползунки громкости и переключатели в «Звуке» двигались посреди
    /// разговора и ничего не меняли — ровно та ручка, «которая двигается и
    /// ничего не меняет», от которой предостерегают комментарии в настройках.
    ///
    /// Три уровня цены. Громкость и усиление потоки читают на каждом такте —
    /// им достаточно новой записи. АРУ и шумодав живут в блоке обработки, и
    /// он подменяется целиком под своим замком, не трогая устройств. Другое
    /// устройство — пересборка тракта, как при его смене в системе: короткий
    /// провал звука, разговор и RTP остаются.
    /// </remarks>
    public void Apply(VoiceAudioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? restartReason = null;
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            VoiceAudioConfiguration previous = _configuration;
            Volatile.Write(ref _configuration, configuration);

            if (!_running)
            {
                return;
            }

            bool routeChanged = previous.InputDeviceId != configuration.InputDeviceId
                || previous.OutputDeviceId != configuration.OutputDeviceId
                || previous.UseRawCapture != configuration.UseRawCapture
                || previous.Codec != configuration.Codec
                || previous.PacketTimeMilliseconds != configuration.PacketTimeMilliseconds;

            if (routeChanged)
            {
                // Микрофон выбран руками — подмена молчавшего больше не нужна.
                if (previous.InputDeviceId != configuration.InputDeviceId)
                {
                    _inputOverrideId = null;
                }

                restartReason = "в настройках выбрано другое устройство";
            }
            else if (previous.AutomaticGainControl != configuration.AutomaticGainControl
                || previous.NoiseSuppression != configuration.NoiseSuppression)
            {
                ReplaceProcessor();
                Diagnostic(ProcessingSummary());
            }
        }

        // Пересборка — вне замка: она берёт его сама и между попытками
        // отпускает (см. Restart).
        if (restartReason is not null)
        {
            Restart(restartReason);
        }
    }

    /// <summary>
    /// Подменяет блок обработки на ходу. Зовётся под <see cref="_control"/>.
    ///
    /// Новый собирается до замка обработки — сборка APM стоит миллисекунды, и
    /// держать ради неё потоки звука незачем. Прежний освобождается после:
    /// под замком его уже никто не держит.
    /// </summary>
    private void ReplaceProcessor()
    {
        if (_processor is not VoiceProcessor current)
        {
            return;
        }

        VoiceProcessor fresh = new(
            current.SampleRate,
            _configuration.AutomaticGainControl,
            _configuration.NoiseSuppression,
            echoCancellation: !_echoFree);

        lock (_processing)
        {
            _processor = fresh;
        }

        current.Dispose();
    }

    /// <summary>Что включено в обработке — одной строкой для журнала.</summary>
    private string ProcessingSummary() =>
        "обработка: "
        + (_echoFree ? "эхоподавление выкл. (вывод в наушники), " : "эхоподавление вкл., ")
        + (_configuration.NoiseSuppression ? (_echoFree ? "шумодав умеренный, " : "шумодав сильный, ") : "шумодав выкл., ")
        + (_configuration.AutomaticGainControl ? "АРУ вкл." : "АРУ выкл.");

    /// <inheritdoc/>
    public void Reconfigure(VoiceAudioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                throw new InvalidOperationException(
                    "перестраивать работающий тракт нельзя: сначала остановка");
            }

            _configuration = configuration;
        }
    }

    /// <inheritdoc/>
    public void Start()
    {
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                return;
            }

            // Новый запуск отменяет намерение остановиться: прежнее относилось
            // к прошлому разговору.
            Volatile.Write(ref _stopRequested, false);

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Build();
                    break;
                }
                catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException
                                              or NotSupportedException)
                {
                    // Разбираем недособранное сами: оставить половину открытых
                    // потоков значит держать гарнитуру в режиме связи после
                    // неудавшегося звонка.
                    Teardown();

                    if (attempt >= StartAttempts || !IsTransient(e))
                    {
                        throw new VoiceAudioException("тракт не поднялся: " + e.Message, e);
                    }

                    Diagnostic($"запуск не удался ({e.Message}), попытка {attempt + 1}");
                    Thread.Sleep(StartRetryDelay);
                }
            }

            _running = true;
            StartWatching();
        }
    }

    /// <inheritdoc/>
    /// <summary>
    /// Останавливает тракт.
    ///
    /// <b>Не ждёт пересборку, застрявшую в драйвере.</b> Замер на живой
    /// гарнитуре: открытие <b>умирающего</b> Bluetooth-устройства блокируется
    /// внутри драйвера, и серия попыток растянулась на десятки секунд. Отбой,
    /// который в это время просто ждал бы замка, выглядит для оператора как
    /// зависшее приложение — а он всего лишь положил трубку.
    ///
    /// Поэтому намерение остановиться объявляется <b>до</b> замка и отдельным
    /// флагом. Если замок свободен, тракт разбирается здесь же, как раньше.
    /// Если нет — разберёт его сама пересборка, увидев флаг, как только драйвер
    /// её отпустит. В обоих случаях устройство закрывается ровно один раз.
    /// </summary>
    public void Stop()
    {
        // Порядок важен: сначала флаг, потом отписка. Наоборот — и уведомление,
        // проскочившее между ними, назначило бы пересборку уже остановленного
        // тракта.
        Volatile.Write(ref _stopRequested, true);
        StopWatching();

        if (!_control.TryEnter(StopLockWait))
        {
            Diagnostic(
                "остановка: пересборка держит тракт, устройство закроется по её выходе");
            return;
        }

        try
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            Teardown();
        }
        finally
        {
            _control.Exit();
        }
    }

    /// <summary>
    /// Пересобирает тракт.
    ///
    /// <b>Замок не держится во время ожидания между попытками, и это
    /// существенно.</b> Первый вариант спал внутри замка — то есть отбой,
    /// пришедший посреди серии попыток, ждал бы до десяти секунд, пока серия не
    /// кончится, и всё это время приложение выглядело бы зависшим. Теперь
    /// каждая попытка берёт замок заново, а между ними он свободен: остановка
    /// проходит в промежуток, снимает <c>_running</c>, и следующая попытка
    /// видит это и уходит.
    ///
    /// <b>Чего это всё же не лечит, и это надо знать.</b> Сама попытка сборки
    /// держит замок, и на исправном устройстве это дёшево — замерено 511 мс на
    /// пересборку и 626 мс на первый запуск. Но на <b>умирающем</b>
    /// Bluetooth-устройстве открытие блокируется в драйвере надолго: в прогоне,
    /// где AirPods уходили из режима связи, серия попыток растянулась на
    /// десятки секунд, и отбой всё это время ждал бы замка. Лечится это только
    /// сторожевым таймером на саму сборку, то есть отдельной работой; здесь
    /// записано, потому что заметить это можно лишь на живой гарнитуре, а
    /// объяснять придётся оператору.
    /// </summary>
    public void Restart(string reason)
    {
        AudioRestartPolicy policy;
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_running)
            {
                return;
            }

            policy = _configuration.RestartSettings.CreatePolicy();
            Report(new VoiceAudioEvent.Restarting(reason, 1));
            Teardown();
        }

        Stopwatch since = Stopwatch.StartNew();

        while (true)
        {
            TimeSpan wait;

            lock (_control)
            {
                // Пока мы ждали, тракт могли остановить или закрыть. Собирать
                // его обратно после этого — значит открыть устройство, которое
                // уже никому не нужно, и оставить гарнитуру в режиме связи.
                //
                // Флаг проверяется отдельно от `_running`: остановка, не
                // дождавшаяся замка, до `_running` не добралась и оставила
                // разбор нам.
                if (_disposed || !_running || Volatile.Read(ref _stopRequested))
                {
                    if (_running)
                    {
                        _running = false;
                        Teardown();
                    }

                    return;
                }

                try
                {
                    Build();

                    // Пока мы собирали, могли попросить остановиться — и
                    // именно так и происходит, когда сборка застревает в
                    // драйвере на десятки секунд. Оставить тракт поднятым
                    // значило бы держать гарнитуру открытой после отбоя.
                    if (Volatile.Read(ref _stopRequested))
                    {
                        _running = false;
                        Teardown();
                        return;
                    }

                    policy.RecordSuccess();
                    Report(new VoiceAudioEvent.Restarted(reason));
                    return;
                }
                catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException
                                              or NotSupportedException)
                {
                    Teardown();

                    RestartDecision decision = policy.RecordFailure(since.Elapsed);
                    if (decision.Action == RestartAction.GiveUp)
                    {
                        _running = false;
                        Report(new VoiceAudioEvent.Broken($"{reason}: {e.Message}"));
                        return;
                    }

                    Diagnostic(
                        $"пересборка не удалась ({e.Message}), попытка {decision.Attempt} "
                        + $"через {decision.Delay.TotalMilliseconds:F0} мс");
                    Report(new VoiceAudioEvent.Restarting(reason, decision.Attempt + 1));
                    wait = decision.Delay;
                }
            }

            Thread.Sleep(wait);
        }
    }

    /// <summary>
    /// Подписывается на смену устройств.
    ///
    /// Подписка живёт от <see cref="Start"/> до <see cref="Stop"/>, а не от
    /// сборки до разбора: пересборка сама разбирает и собирает тракт, и
    /// отписываться на это время значило бы пропустить ровно те уведомления,
    /// ради которых подписка и нужна, — те, что приходят, пока устройство в
    /// переходе.
    /// </summary>
    private void StartWatching()
    {
        _supervisor = new RestartSupervisor(reason => Restart(reason));

        try
        {
            _watch = new AudioDeviceWatch(OnDeviceChanged);
        }
        catch (COMException e)
        {
            // Звуковая служба не дала подписаться. Разговор от этого не
            // отменяется — он просто не переживёт смену устройства, и об этом
            // надо сказать в журнал, а не падать.
            Diagnostic($"следить за сменой устройств не удалось: {e.Message}");
        }
    }

    private void StopWatching()
    {
        _watch?.Dispose();
        _watch = null;

        _supervisor?.Dispose();
        _supervisor = null;
    }

    /// <summary>
    /// Уведомление от звуковой службы.
    ///
    /// Вызывается на её рабочем потоке, который держит внутренний замок, пока
    /// разносит уведомления. Здесь поэтому только фильтр и запись повода:
    /// пересобирать тракт отсюда значило бы подвесить звук во всей системе на
    /// то время, пока мы открываем устройство.
    /// </summary>
    private void OnDeviceChanged(AudioDeviceChange change)
    {
        AudioRouteBinding binding = Volatile.Read(ref _binding);
        if (!DeviceChangeRelevance.AffectsRoute(change, binding))
        {
            return;
        }

        _supervisor?.Notify(Describe(change));
    }

    private static string Describe(AudioDeviceChange change) => change.Kind switch
    {
        AudioDeviceChangeKind.DeviceAdded => "устройство вернулось",
        AudioDeviceChangeKind.DeviceRemoved => "устройство исчезло",
        AudioDeviceChangeKind.DefaultChanged => "сменилось устройство для связи",
        _ => change.Availability == AudioDeviceAvailability.Active
            ? "устройство снова доступно"
            : "устройство сменило состояние",
    };

    public void Dispose()
    {
        Volatile.Write(ref _stopRequested, true);
        StopWatching();

        lock (_control)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            Teardown();
        }
    }

    /// <summary>Строка для журнала: всё, чем тракт может отчитаться о себе.</summary>
    public string Summary()
    {
        if (_balance is null || _captureActivity is null || _rateController is null)
        {
            return "тракт не запущен";
        }

        return string.Join(
            Environment.NewLine,
            $"захват {_captureFormat?.SampleRate} Гц, вывод {_renderFormat?.SampleRate} Гц, "
                + $"обработка {_processor?.SampleRate} Гц, кодек {_configuration.Codec}",
            _captureActivity.Summary(_uptime.Elapsed),
            _balance.Summary,
            _rateController.Summary,
            _drift.Summary,
            $"задержка эхоподавителю: {DeclaredDelayMilliseconds} мс",
            LevelSummary);
    }

    /// <summary>Пики уровня одной строкой. Сбрасываются на каждом подъёме тракта.</summary>
    public string LevelSummary
    {
        get
        {
            float[] channels = _captureChannelPeaks;
            string perChannel = channels.Length > 1
                ? $" (каналы {string.Join(" / ", channels.Select(Decibels))})"
                : string.Empty;

            int overloads = Volatile.Read(ref _captureOverloads);
            string overload = overloads > 0
                ? $", микрофон у потолка {overloads} отсч. — убавьте громкость входа в Windows"
                : string.Empty;

            return $"пики: микрофон {Decibels(_capturePeak)}{perChannel}, в линию {Decibels(_sentPeak)}, "
                + $"в наушники {Decibels(_playedPeak)}{overload}";
        }
    }

    private static int CountOverloads(ReadOnlySpan<float> samples)
    {
        int count = 0;
        foreach (float sample in samples)
        {
            if (Math.Abs(sample) >= 0.999f)
            {
                count++;
            }
        }

        return count;
    }

    private static string Decibels(float peak) => peak <= 1e-5f
        ? "тишина"
        : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{20 * Math.Log10(peak):F0} дБ");

    /// <inheritdoc/>
    public AudioLevels TakeLevels() => new(
        Interlocked.Exchange(ref _sentMeter, 0),
        Interlocked.Exchange(ref _playedMeter, 0));

    private static float PeakOf(ReadOnlySpan<float> samples, float peak)
    {
        foreach (float sample in samples)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak;
    }

    // MARK: - Сборка и разбор

    private void Build()
    {
        // Вывод первым: по нему выбирается запасной микрофон — той же карты.
        _outputDevice = OpenDevice(_configuration.OutputDeviceId, AudioDeviceDirection.Render);
        _inputDevice = OpenInput(_outputDevice?.FriendlyName);

        if (_inputDevice is null || _outputDevice is null)
        {
            throw new VoiceAudioException(
                _inputDevice is null && _outputDevice is null
                    ? "в системе нет ни микрофона, ни выхода"
                    : _inputDevice is null ? "нет микрофона" : "нет устройства воспроизведения");
        }

        _inputDeviceId = _inputDevice.ID;
        _inputDeviceName = _inputDevice.FriendlyName;
        _outputDeviceName = _outputDevice.FriendlyName;

        // Привязка запоминается до открытия потоков: по ней фильтруются
        // уведомления, а прийти они могут уже в следующую миллисекунду.
        Volatile.Write(
            ref _binding,
            new AudioRouteBinding(
                _configuration.InputDeviceId,
                _configuration.OutputDeviceId,
                _inputDevice.ID,
                _outputDevice.ID));

        _captureClient = _inputDevice.CreateAudioClient();
        _renderClient = _outputDevice.CreateAudioClient();

        // Категория «связь» — не косметика: по ней система решает, приглушать
        // ли остальные звуки и как вести себя с гарнитурой.
        AudioClientStreamOptions options = _configuration.UseRawCapture
            ? AudioClientStreamOptions.Raw
            : AudioClientStreamOptions.None;
        _captureClient.SetClientProperties(AudioStreamCategory.Communications, options);
        _renderClient.SetClientProperties(AudioStreamCategory.Communications, options);

        _captureFormat = _captureClient.MixFormat;
        _renderFormat = _renderClient.MixFormat;

        _captureLayout = LayoutOf(_captureFormat, "захват");
        _renderLayout = LayoutOf(_renderFormat, "вывод");

        int processingRate = VoiceProcessor.NearestSupportedRate(_captureFormat.SampleRate);
        int codecRate = (int)_configuration.Codec.SampleRate();

        _echoFree = AudioDeviceCatalog.IsEchoFree(_outputDevice);
        _processor = new VoiceProcessor(
            processingRate,
            _configuration.AutomaticGainControl,
            _configuration.NoiseSuppression,
            echoCancellation: !_echoFree);

        _captureToProcessing = new Resampler(_captureFormat.SampleRate, processingRate);
        _processingToCodec = new Resampler(processingRate, codecRate);
        _codecToRender = new Resampler(codecRate, _renderFormat.SampleRate);
        _renderToProcessing = new Resampler(_renderFormat.SampleRate, processingRate);

        int targetFill = _configuration.TargetPlaybackFrames
            * _configuration.PacketTimeMilliseconds
            * _renderFormat.SampleRate
            / 1000;
        int capacity = Math.Max(targetFill * RingHeadroomFactor, _renderFormat.SampleRate / 2);
        _playbackRing = new SampleRing(capacity, targetFill);

        _encoder = new AudioFrameEncoder(_configuration.Codec);
        _decoder = new AudioFrameDecoder(_configuration.Codec);

        // Сколько отсчётов законно висит в пути между устройством и кодером.
        //
        // Считать это надо в отсчётах разговора и по всем местам, где они
        // задерживаются, а не только по фильтрам. Первый вариант заложил одни
        // фильтры — и прогон матрицы показал ровно то, ради чего баланс и
        // написан, только наоборот: сводка говорила «сходится», а приговор
        // стенда «разошёлся», потому что баланс читается с чужого потока
        // посреди работы захвата и застаёт кадр на полпути.
        //
        // Слагаемых три: два кадра обработки, которые ждут, пока наберутся
        // (устройство отдаёт пакеты пачками, и двух хватает), плюс задержки
        // обоих ядер пересчёта.
        double toCodec = (double)codecRate / processingRate;
        int inFlight = (int)Math.Ceiling(
            ((2 * _processor.FrameSamples) + _captureToProcessing.LatencySamples
                + _processingToCodec.LatencySamples) * toCodec) + 2;

        _balance = new SampleBalance(
            (double)codecRate / _captureFormat.SampleRate,
            _configuration.SamplesPerFrame,
            inFlight);
        _captureActivity = new DeviceActivityWatch(_captureFormat.SampleRate);
        _rateController = new PlaybackRateController(targetFill, _renderFormat.SampleRate);
        _drift.Reset();

        _captureClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback,
            _captureClient.DefaultDevicePeriod,
            0,
            _captureFormat,
            Guid.Empty);

        _renderClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback,
            _renderClient.DefaultDevicePeriod,
            0,
            _renderFormat,
            Guid.Empty);

        _latencyFrames = (int)((_captureClient.StreamLatency + _renderClient.StreamLatency)
            / 10_000_000.0 * processingRate);

        _captureReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        _renderReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        _captureClient.SetEventHandle(_captureReady.SafeWaitHandle.DangerousGetHandle());
        _renderClient.SetEventHandle(_renderReady.SafeWaitHandle.DangerousGetHandle());

        _stop = new CancellationTokenSource();
        CancellationToken token = _stop.Token;

        _correctionCarry = 0;
        _lastClockSample = 0;
        _capturePeak = _sentPeak = _playedPeak = 0;
        _captureChannelPeaks = new float[_captureFormat.Channels];
        _captureOverloads = 0;
        _uptime.Restart();
        _captureClient.Start();
        _renderClient.Start();

        // Приоритет выше обычного: пропущенное пробуждение — это потерянный
        // звук. Реального времени не просим: под ним ошибка в цикле вешает
        // машину целиком.
        _captureThread = StartThread("elitesip-capture", () => CaptureLoop(token));
        _renderThread = StartThread("elitesip-render", () => RenderLoop(token));
        _feedThread = StartThread("elitesip-feed", () => FeedLoop(token));

        // Имена устройств — первым делом. Жалоба «микрофон не идёт» 11 сентября
        // 2026 оказалась гарнитурой, которая лежала на столе: тракт открывал
        // системное устройство для связи, а говорили в другое, и по журналу,
        // где были одни форматы, этого было не видно вовсе.
        Diagnostic(
            $"тракт поднят: микрофон «{_inputDevice.FriendlyName}», наушники «{_outputDevice.FriendlyName}»"
            + (_configuration.InputDeviceId is null || _configuration.OutputDeviceId is null
                ? " (системные для связи)"
                : string.Empty));

        Diagnostic(ProcessingSummary());

        Diagnostic(
            $"форматы: захват {_captureFormat.SampleRate} Гц {_captureFormat.Channels} кан. "
            + $"{_captureFormat.Encoding}/{_captureFormat.BitsPerSample} бит, "
            + $"вывод {_renderFormat.SampleRate} Гц {_renderFormat.Channels} кан. "
            + $"{_renderFormat.Encoding}/{_renderFormat.BitsPerSample} бит, "
            + $"обработка {processingRate} Гц, кодек {_configuration.Codec} {codecRate} Гц, "
            + $"запас кольца {targetFill} отсчётов, латентность потоков "
            + $"{_latencyFrames * 1000.0 / processingRate:F1} мс");
    }

    private static Thread StartThread(string name, Action body)
    {
        Thread thread = new(() => body())
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = name,
        };

        thread.Start();
        return thread;
    }

    private void Teardown()
    {
        _stop?.Cancel();

        JoinThread(_captureThread);
        JoinThread(_renderThread);
        JoinThread(_feedThread);
        _captureThread = _renderThread = _feedThread = null;

        StopClient(_captureClient);
        StopClient(_renderClient);

        _captureClient?.Dispose();
        _renderClient?.Dispose();
        _captureClient = _renderClient = null;

        _inputDevice?.Dispose();
        _outputDevice?.Dispose();
        _inputDevice = _outputDevice = null;

        _captureReady?.Dispose();
        _renderReady?.Dispose();
        _captureReady = _renderReady = null;

        _stop?.Dispose();
        _stop = null;

        _processor?.Dispose();
        _processor = null;

        _uptime.Stop();
    }

    private static void JoinThread(Thread? thread)
    {
        // Секунда — с запасом: и захват, и вывод просыпаются не реже чем раз в
        // сто миллисекунд даже при мёртвом устройстве. Ждать бесконечно нельзя:
        // поток, застрявший в драйвере, не должен утаскивать за собой отбой.
        if (thread is not null && !thread.Join(TimeSpan.FromSeconds(1)))
        {
            Debug.Fail($"поток {thread.Name} не остановился за секунду");
        }
    }

    private static void StopClient(AudioClient? client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            client.Stop();
        }
        catch (COMException)
        {
            // Устройство уже исчезло. Останавливать нечего, а бросать отсюда
            // нельзя: это путь разбора.
        }
    }

    /// <summary>
    /// Открывает микрофон с поправкой на виртуальные и молчащие устройства.
    /// </summary>
    ///
    /// <remarks>
    /// Поправка — только когда микрофон в настройках не выбран. Выбор человека
    /// не перебивается: он мог выбрать вход с аппаратным выключателем, и
    /// молча уехать на встроенный микрофон ноутбука было бы хуже тишины. Для
    /// «системного» же подмена — единственный способ заговорить на машине, где
    /// системным для связи назначен виртуальный Oculus.
    /// </remarks>
    private MMDevice? OpenInput(string? renderName)
    {
        if (_inputOverrideId is string forced && AudioDeviceCatalog.Open(forced) is MMDevice overridden)
        {
            return overridden;
        }

        MMDevice? chosen = OpenDevice(_configuration.InputDeviceId, AudioDeviceDirection.Capture);
        if (_configuration.InputDeviceId is not null || chosen is null)
        {
            return chosen;
        }

        bool isSilent = SilentInputs.ContainsKey(chosen.ID);
        bool isVirtual = AudioDeviceCatalog.Describe(chosen, AudioDeviceDirection.Capture)?.Transport
            is AudioTransport.Virtual;
        if (!isSilent && !isVirtual)
        {
            return chosen;
        }

        string[] excluding = [.. SilentInputs.Keys, chosen.ID];
        if (AudioDeviceCatalog.SpareCapture(excluding, renderName) is not AudioDevice spare
            || AudioDeviceCatalog.Open(spare.Id) is not MMDevice replacement)
        {
            return chosen;
        }

        Diagnostic(
            $"системный микрофон «{chosen.FriendlyName}» "
            + (isSilent ? "молчал на прошлом звонке" : "виртуальный")
            + $" — берём «{replacement.FriendlyName}»");
        chosen.Dispose();
        return replacement;
    }

    /// <summary>
    /// Микрофон молчит — ни одного ненулевого отсчёта с подъёма тракта.
    ///
    /// Настоящий микрофон так не умеет: даже в тихой комнате у него шум на
    /// −90…−60 дБ. Цифровой ноль отдают виртуальные устройства, незанятый вход
    /// звуковой карты и закрытый в Windows доступ к микрофону. Зовётся один
    /// раз на подъём тракта, с потока подачи.
    /// </summary>
    private void CheckSilentInput()
    {
        if (_capturePeak >= SilentPeak || _inputDeviceId is not string current)
        {
            return;
        }

        SilentInputs[current] = 0;

        if (_configuration.InputDeviceId is not null)
        {
            Diagnostic(
                $"микрофон «{_inputDeviceName}» молчит: за {SilenceWatch.TotalSeconds:0.#} с ни одного отсчёта"
                + " — проверьте, тот ли вход выбран в настройках");
            return;
        }

        if (AudioDeviceCatalog.SpareCapture([.. SilentInputs.Keys], _outputDeviceName) is not AudioDevice spare)
        {
            Diagnostic(
                $"микрофон «{_inputDeviceName}» молчит, а другого живого нет"
                + " — проверьте доступ к микрофону в параметрах Windows");
            return;
        }

        _inputOverrideId = spare.Id;
        Diagnostic($"микрофон «{_inputDeviceName}» молчит — переключаемся на «{spare.Name}»");
        _supervisor?.Notify($"микрофон молчал, переход на «{spare.Name}»");
    }

    private MMDevice? OpenDevice(string? id, AudioDeviceDirection direction)
    {
        // Сохранённое устройство могло исчезнуть — гарнитуру выдернули. Тогда
        // берём системное для связи, а не отказываемся от звонка: требование
        // перенесено из оригинала дословно, потому что цена ошибки —
        // несостоявшийся разговор.
        if (!string.IsNullOrEmpty(id))
        {
            MMDevice? saved = AudioDeviceCatalog.Open(id);
            if (saved is not null)
            {
                return saved;
            }

            Diagnostic($"устройство из настроек не найдено, берём системное для связи: {id}");
        }

        AudioDevice? fallback = AudioDeviceCatalog.Default(direction);
        return fallback is null ? null : AudioDeviceCatalog.Open(fallback.Id);
    }

    // MARK: - Захват: микрофон → обработка → кодек → сеть

    private void CaptureLoop(CancellationToken token)
    {
        AudioClient client = _captureClient!;
        WaveFormat format = _captureFormat!;
        VoiceProcessor processor = _processor!;
        Resampler toProcessing = _captureToProcessing!;
        Resampler toCodec = _processingToCodec!;
        AudioFrameEncoder encoder = _encoder!;
        SampleBalance balance = _balance!;
        DeviceActivityWatch activity = _captureActivity!;

        AudioCaptureClient capture = client.AudioCaptureClient;
        int channels = format.Channels;
        SampleLayout layout = _captureLayout;
        float[] channelPeaks = _captureChannelPeaks;

        float[] mono = new float[format.SampleRate];
        float[] processingPending = new float[format.SampleRate];
        int processingCount = 0;
        float[] processed = new float[processor.FrameSamples];
        float[] codecPending = new float[format.SampleRate];
        int codecCount = 0;
        short[] frame = new short[_configuration.SamplesPerFrame];

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_captureReady!.WaitOne(100))
                {
                    continue;
                }

                while (capture.GetNextPacketSize() > 0)
                {
                    nint buffer = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);

                    if (frames > 0 && frames <= mono.Length)
                    {
                        MixToMono(buffer, mono, frames, channels, layout);

                        // Флаг «тишина» означает, что буфер читать не надо:
                        // система отдаёт в нём что угодно.
                        if ((flags & AudioClientBufferFlags.Silent) == 0)
                        {
                            _capturePeak = PeakOf(mono.AsSpan(0, frames), _capturePeak);
                            ChannelPeaks(buffer, frames, channels, layout, channelPeaks);
                            _captureOverloads += CountOverloads(mono.AsSpan(0, frames));
                        }

                        activity.NoteDelivered(frames);
                        balance.NoteCaptured(frames);

                        // Пересчёт в частоту обработки. При устройстве на 48,
                        // 16 или 8 кГц это проход насквозь без единого
                        // умножения.
                        processingCount += toProcessing.Process(
                            mono.AsSpan(0, frames),
                            processingPending.AsSpan(processingCount));
                    }

                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
                    {
                        activity.NoteDiscontinuity();
                    }

                    capture.ReleaseBuffer(frames);
                }

                Volatile.Write(ref _capturePendingSamples, processingCount);

                // Обработка идёт кадрами ровно по десять миллисекунд: другого
                // размера APM не принимает.
                int frameSize = processor.FrameSamples;
                int consumed = 0;
                while (processingCount - consumed >= frameSize)
                {
                    int delay = ComputeDelayMilliseconds(processor.SampleRate, processingCount - consumed);

                    // Блок обработки берётся из поля под своим замком, а не
                    // из переменной цикла: переключатели АРУ и шумодава
                    // подменяют его посреди разговора (см. Apply). Частота
                    // и размер кадра у подменённого те же, поэтому они
                    // читаются из прежнего.
                    lock (_processing)
                    {
                        _processor!.Process(processingPending.AsSpan(consumed, frameSize), processed, delay);
                    }

                    consumed += frameSize;

                    int produced = toCodec.Process(processed, codecPending.AsSpan(codecCount));
                    codecCount += produced;
                    balance.NoteConverted(produced);
                }

                if (consumed > 0)
                {
                    Array.Copy(processingPending, consumed, processingPending, 0, processingCount - consumed);
                    processingCount -= consumed;
                }

                codecCount = EmitFrames(encoder, codecPending, codecCount, frame);
            }
            catch (COMException e)
            {
                FailFromThread("захват", e);
                return;
            }
        }
    }

    /// <summary>Собирает кадры кодека и отдаёт их сети.</summary>
    private int EmitFrames(AudioFrameEncoder encoder, float[] pending, int count, short[] frame)
    {
        SampleBalance balance = _balance!;
        float gain = Volatile.Read(ref _configuration).MicrophoneGain;
        int size = frame.Length;
        int offset = 0;

        float peak = _sentPeak;
        float meter = 0;

        while (count - offset >= size)
        {
            for (int i = 0; i < size; i++)
            {
                // Усиление стоит здесь, после обработки голоса и перед
                // кодированием: решение перенесено из оригинала вместе с
                // причиной — оно умножает уже готовые отсчёты вместе со всем,
                // что в них попало, поэтому и ограничено вдвое.
                float sample = pending[offset + i] * gain;
                meter = Math.Max(meter, Math.Abs(sample));

                // Мягко, а не срезом: эхоподавитель и шумодав отдают выбросы
                // за шкалу, и жёсткий срез в кодеке собеседник слышит хрипом.
                // Пик в журнал идёт до ограничения — «в линию +4 дБ» и есть
                // признак того, что ограничивать было что.
                frame[i] = ToPcm(SpeechGainControl.Limit(sample));
            }

            offset += size;

            byte[] payload = encoder.Encode(frame);
            balance.NoteEncodedFrame();
            Handlers.EncodedFrame?.Invoke(payload);
        }

        _sentPeak = Math.Max(peak, meter);

        if (meter > _sentMeter)
        {
            _sentMeter = meter;
        }

        if (offset > 0)
        {
            Array.Copy(pending, offset, pending, 0, count - offset);
        }

        return count - offset;
    }

    /// <summary>
    /// Насколько опорный сигнал опережает микрофонный, в миллисекундах.
    ///
    /// Считается, а не объявляется: см. описание типа. Слагаемые — всё, что
    /// кадр проведёт в пути, прежде чем прозвучит, плюс то, что микрофонный
    /// кадр уже пролежал у нас.
    /// </summary>
    private int ComputeDelayMilliseconds(int processingRate, int capturePending)
    {
        int ringFill;
        lock (_ring)
        {
            ringFill = _playbackRing?.Available ?? 0;
        }

        // Кольцо и буфер устройства живут в отсчётах вывода, а задержка нужна в
        // отсчётах обработки. Без пересчёта на устройстве с 44 100 Гц число
        // уехало бы на десятую часть.
        int renderRate = _renderFormat?.SampleRate ?? processingRate;
        double toProcessing = (double)processingRate / renderRate;

        double samples = ((ringFill + Volatile.Read(ref _renderPadding)) * toProcessing)
            + capturePending
            + _latencyFrames;

        int milliseconds = (int)(samples * 1000.0 / processingRate);
        Volatile.Write(ref _lastDelayMilliseconds, milliseconds);
        return milliseconds;
    }

    // MARK: - Вывод: кольцо → устройство

    private void RenderLoop(CancellationToken token)
    {
        AudioClient client = _renderClient!;
        WaveFormat format = _renderFormat!;
        VoiceProcessor processor = _processor!;
        Resampler toProcessing = _renderToProcessing!;
        SampleRing ring = _playbackRing!;

        AudioRenderClient render = client.AudioRenderClient;
        int channels = format.Channels;
        SampleLayout layout = _renderLayout;
        int bufferFrames = client.BufferSize;

        float[] scratch = new float[bufferFrames];
        float[] reversePending = new float[format.SampleRate];
        int reverseCount = 0;

        // Сглаживание краёв звука: вход — 5 мс, спад — около 4 мс.
        int fadeLength = Math.Max(1, format.SampleRate / 200);
        int fadePosition = fadeLength;
        bool starving = false;
        float tail = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_renderReady!.WaitOne(100))
                {
                    continue;
                }

                int padding = client.CurrentPadding;
                Volatile.Write(ref _renderPadding, padding);

                int free = bufferFrames - padding;
                if (free <= 0)
                {
                    continue;
                }

                int taken;
                lock (_ring)
                {
                    taken = ring.Read(scratch.AsSpan(0, free));
                }

                // Звук возвращается после тишины — плавно, а не ступенькой.
                if (taken > 0 && starving)
                {
                    fadePosition = 0;
                    starving = false;
                }

                for (int i = 0; i < taken && fadePosition < fadeLength; i++, fadePosition++)
                {
                    scratch[i] *= (float)fadePosition / fadeLength;
                }

                // Недобор добивается затуханием последнего отсчёта, а не
                // нулём: вернуться к устройству надо в этот такт, но обрыв
                // волны в ноль — это ступенька, а ступенька слышна щелчком.
                // Именно так звучали вход в удержание и выход из него, смена
                // источника на музыку ожидания и каждый недобор: звук
                // обрывался на полуслове в ноль одним отсчётом.
                if (taken < free)
                {
                    float last = taken > 0 ? scratch[taken - 1] : tail;
                    for (int i = taken; i < free; i++)
                    {
                        last *= TailDecay;
                        scratch[i] = last;
                    }

                    starving = true;
                }

                tail = scratch[free - 1];

                // Мягкое ограничение после громкости: декодированный G.711
                // стоит у самой шкалы, пересчёт частоты даёт выбросы за неё, и
                // микшер Windows срезал их жёстко — пики «в наушники +2 дБ» в
                // журнале, треск в ухе.
                //
                // Громкость читается на каждом такте, а не один раз перед
                // циклом: ползунок в настройках двигают посреди разговора.
                float volume = Volatile.Read(ref _configuration).PlaybackVolume;
                for (int i = 0; i < free; i++)
                {
                    scratch[i] = SpeechGainControl.Limit(scratch[i] * volume);
                }

                float played = PeakOf(scratch.AsSpan(0, free), 0);
                _playedPeak = Math.Max(_playedPeak, played);

                if (played > _playedMeter)
                {
                    _playedMeter = played;
                }

                WriteFrames(render, scratch, free, channels, layout);

                // Опорный сигнал эхоподавителю — ровно то, что мы отдали
                // устройству, и в том же порядке. Брать его до умножения на
                // громкость было бы ошибкой: слышит микрофон именно то, что
                // прозвучало.
                reverseCount += toProcessing.Process(
                    scratch.AsSpan(0, free),
                    reversePending.AsSpan(reverseCount));

                int frameSize = processor.FrameSamples;
                int consumed = 0;
                while (reverseCount - consumed >= frameSize)
                {
                    lock (_processing)
                    {
                        _processor!.AnalyzeReverse(reversePending.AsSpan(consumed, frameSize));
                    }

                    consumed += frameSize;
                }

                if (consumed > 0)
                {
                    Array.Copy(reversePending, consumed, reversePending, 0, reverseCount - consumed);
                    reverseCount -= consumed;
                }
            }
            catch (COMException e)
            {
                FailFromThread("вывод", e);
                return;
            }
        }
    }

    /// <summary>
    /// Разрядность устройства.
    /// </summary>
    ///
    /// <remarks>
    /// В общем режиме WASAPI разговаривает с нами форматом устройства, и он не
    /// обязан быть тем, которого мы ждём. Порт до 10 сентября 2026 приводил
    /// буфер к <c>float*</c> в обе стороны и не проверял ничего: на USB-гарнитуре
    /// это работало (там почти всегда 32-битные float), а на встроенном Realtek,
    /// отдающем 16-битный PCM, байты читались как float. Получалась ровно та
    /// пара, с которой пришёл заказчик: на выводе «робот», на захвате значения
    /// вне диапазона, которые эхоподавление и АРУ добивали до тишины.
    ///
    /// Ветка выбирается один раз, до цикла, а не на каждом отсчёте: цикл этот
    /// крутится каждые десять миллисекунд.
    /// </remarks>
    private enum SampleLayout
    {
        Float32,
        Pcm16,
    }

    // Подтипы расширенного формата. Числа из `mmreg.h`; своими константами, а
    // не через NAudio, потому что имени у них там нет.
    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Разбирает формат устройства. Отказ — с внятной причиной.</summary>
    ///
    /// <remarks>
    /// Отказ, а не молчаливое «попробуем как float»: неверно прочитанный звук
    /// не выглядит поломкой, он выглядит плохой связью, и разбирают его
    /// неделями. Пусть лучше тракт не поднимется и скажет почему.
    /// </remarks>
    private static SampleLayout LayoutOf(WaveFormat format, string role)
    {
        // Расширенный формат — обычный ответ WASAPI, а не редкость.
        //
        // Здесь я уже ошибся однажды, 10 сентября 2026, и ошибка стоила выпуска
        // 0.1.1: разбор смотрел только на `Encoding`, а у `MixFormat` он почти
        // всегда `Extensible` — настоящий вид лежит в подтипе. Тракт падал на
        // каждом звонке, и падал молча.
        var encoding = format.Encoding;
        var bits = format.BitsPerSample;

        if (format is WaveFormatExtensible extensible)
        {
            encoding = extensible.SubFormat == SubTypeIeeeFloat
                ? WaveFormatEncoding.IeeeFloat
                : extensible.SubFormat == SubTypePcm
                    ? WaveFormatEncoding.Pcm
                    : WaveFormatEncoding.Unknown;
        }

        return (encoding, bits) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => SampleLayout.Float32,
            (WaveFormatEncoding.Pcm, 16) => SampleLayout.Pcm16,

            // Сюда попадает всё прочее: 24 бита, целый 32-битный, экзотика.
            _ => throw new NotSupportedException(
                $"{role}: устройство отдаёт {encoding}, {bits} бит на отсчёт — "
                    + "такой формат тракт не читает"),
        };
    }

    private static unsafe void WriteFrames(
        AudioRenderClient render,
        float[] source,
        int frames,
        int channels,
        SampleLayout layout)
    {
        nint buffer = render.GetBuffer(frames);

        if (layout is SampleLayout.Float32)
        {
            float* destination = (float*)buffer;
            for (int i = 0; i < frames; i++)
            {
                float sample = source[i];
                for (int c = 0; c < channels; c++)
                {
                    destination[(i * channels) + c] = sample;
                }
            }
        }
        else
        {
            short* destination = (short*)buffer;
            for (int i = 0; i < frames; i++)
            {
                // Ограничение обязательно: обработка отдаёт значения, которые
                // после АРУ выходят за единицу, а short их молча заворачивает —
                // тихий щелчок вместо громкого звука.
                float sample = Math.Clamp(source[i], -1f, 1f);
                short value = (short)(sample * short.MaxValue);
                for (int c = 0; c < channels; c++)
                {
                    destination[(i * channels) + c] = value;
                }
            }
        }

        render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }

    /// <summary>Пики по каналам сырого буфера захвата — для журнала.</summary>
    private static unsafe void ChannelPeaks(
        nint buffer, int frames, int channels, SampleLayout layout, float[] peaks)
    {
        if (channels < 2 || peaks.Length < channels)
        {
            return;
        }

        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                float value = layout is SampleLayout.Float32
                    ? ((float*)buffer)[(i * channels) + c]
                    : ((short*)buffer)[(i * channels) + c] / (float)short.MaxValue;

                float magnitude = Math.Abs(value);
                if (magnitude > peaks[c])
                {
                    peaks[c] = magnitude;
                }
            }
        }
    }

    private static unsafe void MixToMono(
        nint buffer, float[] destination, int frames, int channels, SampleLayout layout)
    {
        if (layout is SampleLayout.Float32)
        {
            float* source = (float*)buffer;
            for (int i = 0; i < frames; i++)
            {
                // Сумма по каналам, а не первый канал: у части гарнитур полезный
                // сигнал лежит во втором, и «первый» дал бы тишину. Замер W0.
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    sum += source[(i * channels) + c];
                }

                destination[i] = sum / channels;
            }
        }
        else
        {
            short* source = (short*)buffer;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    sum += source[(i * channels) + c] / (float)short.MaxValue;
                }

                destination[i] = sum / channels;
            }
        }
    }

    // MARK: - Подача: сеть → кольцо

    private void FeedLoop(CancellationToken token)
    {
        Resampler toRender = _codecToRender!;
        SampleRing ring = _playbackRing!;
        PlaybackRateController controller = _rateController!;
        AudioFrameDecoder decoder = _decoder!;
        PacketLossConcealer concealer = new(_configuration.Codec);

        int renderRate = _renderFormat!.SampleRate;
        float[] decoded = new float[renderRate];
        float[] converted = new float[renderRate];
        double lastObserved = _uptime.Elapsed.TotalSeconds;
        bool silenceChecked = false;

        while (!token.IsCancellationRequested)
        {
            int fill;
            lock (_ring)
            {
                fill = ring.Available;
            }

            double now = _uptime.Elapsed.TotalSeconds;
            controller.Observe(fill, now - lastObserved);
            lastObserved = now;

            SampleClocks(now);

            if (!silenceChecked && now >= SilenceWatch.TotalSeconds)
            {
                silenceChecked = true;
                CheckSilentInput();
            }

            bool fed = false;
            // Запас — из настройки, а не числом на месте: на нём сходятся
            // тракт и джиттер-буфер, и разъезд здесь стоит половины разговора
            // (см. VoiceAudioConfiguration.PlaybackLeadFrames).
            int lead = ring.TargetFill
                / Math.Max(_configuration.TargetPlaybackFrames, 1)
                * _configuration.PlaybackLeadFrames;

            while (fill < lead)
            {
                PlaybackFrame? next = Handlers.NeedsFrame?.Invoke();
                if (next is not PlaybackFrame playback)
                {
                    break;
                }

                // Потерянный кадр синтезируется по периоду основного тона
                // (G.711 Appendix I), настоящий — сшивается с хвостом синтеза.
                //
                // До 11 сентября 2026 здесь стоял повтор прежнего кадра с
                // затуханием 0,6, хотя сокрытие было написано и проверено ещё
                // на W3 и просто не подключено. Повтор двадцати миллисекунд
                // режет голос на случайной фазе тона — на слух это
                // «бульканье» и щелчок на каждом шве, а оператор назвал это
                // «непонятными тресками».
                short[] samples = playback.IsConcealment
                    ? concealer.Conceal(_configuration.SamplesPerFrame)
                    : concealer.Receive(decoder.Decode(playback.Payload.Span));
                Handlers.DecodedSamples?.Invoke(samples);

                int length = Math.Min(samples.Length, decoded.Length);
                for (int i = 0; i < length; i++)
                {
                    decoded[i] = samples[i] / 32768f;
                }

                int produced = toRender.Process(decoded.AsSpan(0, length), converted);

                // Поправка темпа — вот здесь она и работает: лишний или
                // недостающий отсчёт добавляется не в звук, а в счёт того,
                // сколько мы кладём в кольцо.
                produced = ApplyRateCorrection(
                    converted,
                    produced,
                    controller.Correction,
                    ref _correctionCarry);

                lock (_ring)
                {
                    ring.Write(converted.AsSpan(0, produced));
                    fill = ring.Available;
                }

                fed = true;
            }

            if (!fed)
            {
                // Спросить было нечего. Ждём такт устройства, а не крутим цикл:
                // сеть отдаёт кадры не чаще, чем раз в двадцать миллисекунд.
                token.WaitHandle.WaitOne(5);
            }
        }
    }

    /// <summary>
    /// Растягивает или укорачивает кусок на доли процента.
    ///
    /// Поправка мала — единицы отсчётов на кадр, — и делать ради неё второй
    /// проход фильтра незачем: разница между отбросить один отсчёт из тысячи и
    /// правильно его интерполировать лежит на 60 дБ ниже разговора. Отсчёт
    /// снимается с конца куска, а не из середины, чтобы не рвать волну там, где
    /// она громче всего.
    ///
    /// <b>Дробный остаток обязан переноситься между кусками, и это не
    /// придирка.</b> Первый вариант округлял <c>count × correction</c> на каждом
    /// куске — и первый же прогон на живом железе показал, что поправка не
    /// делает ничего: кадр это 960 отсчётов, поправка вышла 179 ppm, произведение
    /// 960,17 округляется обратно в 960. Регулятор при этом исправно наматывал
    /// интеграл, потому что ошибка никуда не девалась, и в журнале стояли
    /// честные 179 ppm компенсации, которой на самом деле не было. Настоящее
    /// расхождение кварцев — десятки ppm, то есть <b>любая</b> поправка меньше
    /// тысячи ppm терялась бы в округлении целиком.
    ///
    /// С переносом остатка лишний отсчёт добавляется раз в несколько кадров, и
    /// в среднем темп получается ровно тот, который назначил регулятор.
    /// </summary>
    internal static int ApplyRateCorrection(
        float[] samples,
        int count,
        double correction,
        ref double carry)
    {
        if (count <= 0)
        {
            return count;
        }

        carry += count * (correction - 1.0);

        if (carry >= 1.0)
        {
            int extra = Math.Min((int)carry, samples.Length - count);
            if (extra > 0)
            {
                for (int i = 0; i < extra; i++)
                {
                    samples[count + i] = samples[count - 1];
                }

                carry -= extra;
                return count + extra;
            }

            return count;
        }

        if (carry <= -1.0)
        {
            int drop = Math.Min((int)(-carry), count - 1);
            if (drop > 0)
            {
                carry += drop;
                return count - drop;
            }
        }

        return count;
    }

    /// <summary>
    /// Проба часов обоих устройств для оценки их расхождения.
    ///
    /// <b>Часы устройства, а не собственные счётчики.</b> Первая версия стенда
    /// W0 считала расхождение по числу отсчётов, которые сама захватила и сама
    /// записала, — и получала две тысячи ppm на проводной гарнитуре, где кварц
    /// физически один. Ошибка была в постановке: вывод отдаёт ровно столько,
    /// сколько ему дало кольцо, то есть такой счётчик меряет пропускную
    /// способность кольца, а не ход часов. Про часы знает только само
    /// устройство.
    ///
    /// Делится позиция на заявленную частоту счётчика, а не на частоту
    /// дискретизации: у разных драйверов они разные.
    /// </summary>
    private void SampleClocks(double now)
    {
        // Разгон в замер не идёт: устройства стартуют не одновременно, и первые
        // секунды дают сотни ppm на ровном месте.
        if (now < 3)
        {
            return;
        }

        // Раз в секунду, а не на каждом витке подачи. Первый прогон снял 1949
        // проб за 17 секунд — сто пятнадцать в секунду, при том что показание
        // часов обновляется раз в период устройства, то есть сто раз в секунду
        // мы переспрашивали одно и то же число. На точность это не влияет
        // никак, а окно оценки при заданной ёмкости сжимается с часов до
        // получаса.
        if (now - _lastClockSample < 1.0)
        {
            return;
        }

        _lastClockSample = now;

        try
        {
            AudioClockClient? capture = _captureClient?.AudioClockClient;
            AudioClockClient? render = _renderClient?.AudioClockClient;
            if (capture is null || render is null)
            {
                return;
            }

            _drift.Add(now, ClockSeconds(capture) - ClockSeconds(render));
        }
        catch (COMException)
        {
            // Устройство исчезает — про часы спрашивать больше нечего.
            // Разбираться с пропажей будут потоки звука, здесь это не авария.
        }
    }

    private static double ClockSeconds(AudioClockClient clock)
    {
        ulong frequency = clock.Frequency;
        return frequency == 0 ? 0 : (double)clock.AdjustedPosition / frequency;
    }

    // MARK: - Мелочи

    private static short ToPcm(float sample) =>
        (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);

    /// <summary>
    /// Поток звука получил отказ от драйвера.
    ///
    /// <b>Чинить отсюда нельзя, и объявлять разговор мёртвым тоже нельзя.</b>
    /// Пересборка требует замка, а его в этот момент может держать отбой — да и
    /// поток, который сам себя пересобирает, разбирал бы себя изнутри. Поэтому
    /// повод уходит надзирателю, а тот склеит его с уведомлениями системы,
    /// которые про то же самое событие сейчас придут, и пересоберёт тракт один
    /// раз.
    ///
    /// Прежний вариант объявлял здесь <c>Broken</c> сразу. Это ровно тот
    /// приговор без вины, ради которого написана
    /// <see cref="AudioRestartPolicy"/>: выдернутая на секунду гарнитура — не
    /// конец разговора.
    /// </summary>
    private void FailFromThread(string stage, Exception error)
    {
        Diagnostic($"{stage}: устройство отказало ({error.Message})");

        RestartSupervisor? supervisor = _supervisor;
        if (supervisor is null)
        {
            // Надзирателя нет — тракт уже останавливают. Чинить нечего.
            return;
        }

        supervisor.Notify($"{stage}: {error.Message}");
    }

    private void Diagnostic(string text) => Handlers.Diagnostic?.Invoke(text);

    private void Report(VoiceAudioEvent value) => Handlers.Event?.Invoke(value);
}
