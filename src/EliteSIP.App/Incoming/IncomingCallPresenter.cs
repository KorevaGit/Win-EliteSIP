using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using EliteSIP.CallGuard;

namespace EliteSIP.App.Incoming;

/// <summary>
/// Показ окна входящего вызова и сбор фактов для защиты.
/// </summary>
///
/// <remarks>
/// То же разделение, что в оригинале: здесь — окно, экран, курсор и мигание
/// панели задач, в пакете <c>CallGuard</c> — разбор. Ни одна проверка не
/// принимает решения здесь, и ни одна строка отсюда не участвует в тестах
/// пакета: разбор проверяется без экрана, а показ проверяется живым вызовом.
/// </remarks>
public sealed class IncomingCallPresenter : IDisposable
{
    /// <summary>Насколько считать курсор «рядом с окном», в точках.</summary>
    ///
    /// <remarks>
    /// Рядом, а не строго внутри: рука подходит к кнопке снаружи, и обрезать
    /// путь по рамке значит требовать движений уже над самой кнопкой.
    /// </remarks>
    private const double NearWindowMargin = 80;

    private readonly Action<string> _log;
    private readonly Func<nint> _taskbarWindow;

    private IncomingCallWindow? _window;
    private IncomingCallViewModel? _model;
    private CallGuardSession? _session;
    private IncomingCallPlacement? _placement;
    private PointerWatch? _pointer;
    private Stopwatch? _clock;
    private Action? _onAnswer;
    private Action? _onDecline;

    /// <summary>
    /// Где окно было в прошлый раз — чтобы следующая позиция гарантированно
    /// отличалась и оператор не привыкал жать в одну точку.
    /// </summary>
    private ScreenPoint? _lastOrigin;

    /// <param name="log">Журнал приложения.</param>
    /// <param name="taskbarWindow">Окно с кнопкой в панели задач: панель софтфона.</param>
    public IncomingCallPresenter(Action<string> log, Func<nint> taskbarWindow)
    {
        _log = log;
        _taskbarWindow = taskbarWindow;
    }

    public bool IsVisible => _window is not null;

    /// <summary>
    /// Отчёт защиты по последнему вызову. После <see cref="Hide"/> остаётся
    /// последним, чтобы его успел прочитать тот, кто разбирает завершение звонка.
    /// </summary>
    public CallGuardReport? LastReport { get; private set; }

    /// <summary>Показывает окно и берёт вызов под защиту.</summary>
    public void Show(
        IncomingCallSubject subject,
        CallGuardPolicy policy,
        Action onAnswer,
        Action onDecline)
    {
        ArgumentNullException.ThrowIfNull(policy);

        Hide();

        policy = policy.Normalized();

        _clock = Stopwatch.StartNew();
        _session = new CallGuardSession(policy, TimeSpan.Zero, Random.Shared);
        LastReport = _session.Report;
        _onAnswer = onAnswer;
        _onDecline = onDecline;

        _model = new IncomingCallViewModel(
            subject,
            _session.Challenge,
            policy.IsEnabled,
            Attempt,
            Decline);

        var window = new IncomingCallWindow(_model);
        _window = window;

        // Показ — единственный момент, когда окно точно знает свой размер: до
        // него высота карточки известна только вёрстке. В оригинале это стоило
        // случайной позиции целиком — окно уезжало за край ровно на свою высоту,
        // потому что позицию считали по нулевому размеру.
        window.Show();

        _placement = PlacementFor(policy, window);
        var origin = NextOrigin(policy, window.PhysicalFrame.Size);
        window.PlaceAt(origin);
        _lastOrigin = origin;

        // Последний рубеж: любое изменение размера после размещения возвращает
        // окно внутрь области. Окно, которое оператор не видит целиком, — это не
        // огрех оформления, а непринятый лид.
        window.SizeChanged += OnWindowSizeChanged;
        KeepOnScreen();

        _pointer = new PointerWatch(NoteCursor);
        if (!_pointer.IsWatching)
        {
            // Молчать нельзя: без хука защита держится на одной случайной
            // позиции, а отчёт покажет нулевой путь курсора у честного человека.
            _log("окно входящего: слежение за мышью не встало — путь курсора не считается");
        }

        IncomingCallWindow.FlashTaskbar(_taskbarWindow());
        _log(PlacementSummary(window.PhysicalFrame));
    }

    /// <summary>Убирает окно и закрывает отчёт.</summary>
    public void Hide()
    {
        _pointer?.Dispose();
        _pointer = null;

        if (_session is not null)
        {
            LastReport = _session.Report;
        }

        if (_window is not null)
        {
            _window.SizeChanged -= OnWindowSizeChanged;
            _window.Close();
            _window = null;
        }

        _session = null;
        _model = null;
        _placement = null;
        _clock = null;
        _onAnswer = null;
        _onDecline = null;
    }

    public void Dispose() => Hide();

    /// <summary>Строка для журнала: где окно оказалось и в какой области его держали.</summary>
    ///
    /// <remarks>
    /// Пишется на каждом вызове, потому что случайная позиция — мера защиты, а не
    /// оформление: её поломка не видна ни по одному другому признаку, и разбирать
    /// жалобу «окно уехало» по памяти оператора нечем.
    /// </remarks>
    // не переводится: строка уходит в журнал, а он остаётся техническим.
    private string PlacementSummary(ScreenRect frame)
    {
        var area = _placement?.Bounds ?? new ScreenRect(0, 0, 0, 0);
        var fits = area.Contains(frame) ? "внутри" : "ВЫШЛО ЗА ОБЛАСТЬ";
        return $"окно входящего: рамка {Short(frame)}, область {Short(area)} — {fits}";
    }

    private static string Short(ScreenRect rect) => string.Format(
        CultureInfo.InvariantCulture,
        "({0}, {1}, {2}×{3})",
        (int)rect.X,
        (int)rect.Y,
        (int)rect.Width,
        (int)rect.Height);

    /// <summary>Разбирает попытку принять вызов.</summary>
    ///
    /// <remarks>
    /// Источник у попытки всегда один — мышь. Клавиатурного приёма нет
    /// намеренно: он не оставлял защите ни одного признака живого человека —
    /// ни пути курсора, ни его отсутствия. «Отклонить» с клавиатуры при этом
    /// работает, то есть отказаться от вызова можно и без мыши.
    /// </remarks>
    private void Attempt(char target)
    {
        if (_session is not CallGuardSession session || _clock is not Stopwatch clock)
        {
            return;
        }

        var attempt = new CallGuardAttempt(
            target,
            _pointer?.LastClickWasInjected ?? false,
            clock.Elapsed);

        var verdict = session.Evaluate(attempt);
        LastReport = session.Report;

        if (verdict.Rejection is CallGuardRejection reason)
        {
            // Окно остаётся на месте: скрыть его в ответ на отклонённое нажатие
            // значило бы потерять лид из-за собственной защиты.
            if (_model is not null)
            {
                _model.Refusal = reason.OperatorMessage();
            }

            _log($"вызов не принят: {reason.LogMessage()}, {IdleSummary()}");
            return;
        }

        var answer = _onAnswer;
        _log($"вызов принят: {session.Report.Summary()}, {IdleSummary()}");
        Hide();
        answer?.Invoke();
    }

    private void Decline()
    {
        var decline = _onDecline;
        Hide();
        decline?.Invoke();
    }

    /// <summary>Сколько система не видела ввода к моменту решения.</summary>
    ///
    /// <remarks>
    /// Этого признака на macOS не было вовсе. Барьером он не служит — оператор
    /// вправе смотреть в экран не шевелясь, — но рядом с нулевым путём курсора
    /// говорит то, чего не говорит ни один из них порознь.
    /// </remarks>
    // не переводится: журнал.
    private static string IdleSummary()
        => $"без ввода {(int)PointerWatch.SystemIdleTime().TotalMilliseconds} мс";

    /// <summary>Считает только те перемещения, что случились рядом с окном.</summary>
    private void NoteCursor(ScreenPoint point)
    {
        if (_session is not CallGuardSession session || _window is not IncomingCallWindow window)
        {
            return;
        }

        var margin = NearWindowMargin * ScaleOf(window);
        if (!window.PhysicalFrame.Inset(-margin).Contains(point))
        {
            return;
        }

        session.NoteCursor(point);
    }

    /// <summary>Область, в которой окну разрешено появиться на этом вызове.</summary>
    ///
    /// <remarks>
    /// Экран берётся тот, где курсор, а не «главный»: рабочее место вполне может
    /// быть на втором мониторе. Систему при этом не приходится уговаривать —
    /// <c>MONITOR_DEFAULTTONEAREST</c> отвечает ровно то, что в оригинале
    /// пришлось писать руками: точный экран, а если курсор оказался на кромке —
    /// ближайший.
    ///
    /// Рабочая область (<c>rcWork</c>), а не весь экран: под панелью задач окно
    /// видно наполовину, и это тот же непринятый лид.
    /// </remarks>
    private static IncomingCallPlacement PlacementFor(CallGuardPolicy policy, IncomingCallWindow window)
    {
        var work = WorkAreaUnderCursor();
        var margin = policy.ScreenMargin * ScaleOf(window);
        return new IncomingCallPlacement(work.Inset(margin), policy.MinimumTravel * ScaleOf(window));
    }

    private ScreenPoint NextOrigin(CallGuardPolicy policy, ScreenSize size)
    {
        var placement = _placement!;

        if (!policy.IsEnabled || !policy.IsRandomPositionEnabled)
        {
            return new ScreenPoint(
                placement.Bounds.MidX - (size.Width / 2),
                placement.Bounds.MidY - (size.Height / 2));
        }

        return placement.Origin(size, _lastOrigin, Random.Shared);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => KeepOnScreen();

    /// <summary>Возвращает окно внутрь области, если оно оттуда вылезло.</summary>
    ///
    /// <remarks>
    /// Позиция при этом остаётся случайной: рамка не выбирается заново, а
    /// вдвигается обратно ровно на столько, на сколько вылезла.
    /// </remarks>
    private void KeepOnScreen()
    {
        if (_window is not IncomingCallWindow window || _placement is not IncomingCallPlacement placement)
        {
            return;
        }

        var frame = window.PhysicalFrame;
        var corrected = placement.Contained(frame);
        if (corrected.Origin == frame.Origin)
        {
            return;
        }

        window.PlaceAt(corrected.Origin);

        // Запоминаем поправленную точку, а не исходную: следующий вызов обязан
        // отсчитывать смещение от того места, где окно оказалось на самом деле.
        _lastOrigin = corrected.Origin;
    }

    /// <summary>Масштаб экрана: пикселей на точку.</summary>
    private static double ScaleOf(Window window)
        => PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    private static ScreenRect WorkAreaUnderCursor()
    {
        var cursor = PointerWatch.CursorPosition();
        var monitor = MonitorFromPoint(
            new NativePoint { X = (int)cursor.X, Y = (int)cursor.Y },
            MonitorDefaultToNearest);

        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
        {
            // Экрана нет — сеанс без монитора. Показывать окно всё равно надо:
            // разбирать «почему вызов не пришёл» дороже, чем окно в углу.
            return new ScreenRect(0, 0, 1024, 768);
        }

        return new ScreenRect(
            info.rcWork.Left,
            info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left,
            info.rcWork.Bottom - info.rcWork.Top);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}
