using System.Windows.Media;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EliteSIP.App.Panel;

/// <summary>Панель софтфона — единственное окно, которое оператор видит весь день.</summary>
///
/// <remarks>
/// Главное правило, из которого выведено всё остальное: **нижняя полоса
/// неподвижна**. Кнопка завершения обязана оказываться под курсором в одном и
/// том же месте независимо от того, появилась ли вторая линия, потеряна ли
/// регистрация, открыто ли поле перевода и сколько у сотрудника макросов.
/// </remarks>
public partial class PanelWindow : Window
{
    private readonly DispatcherTimer _clock;
    private double _bottomEdge;

    public PanelWindow(PanelViewModel model)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;

        // Такт разговора: ровно раз в секунду и только пока панель на экране.
        // `DispatcherTimer`, а не таймер из пула, потому что он толкает
        // привязки — а те живут в потоке интерфейса.
        //
        // Точность здесь не нужна и вредна: таймер на 100 мс ради ровной смены
        // цифры будил бы процесс десять раз в секунду весь рабочий день.
        _clock = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _clock.Tick += (_, _) => Model.Tick();
    }

    public PanelViewModel Model { get; }

    /// <summary>
    /// Где панель стояла в прошлый раз и куда её вернуть.
    /// </summary>
    ///
    /// <remarks>
    /// Ставится приложением, потому что настройки — его дело, а не окна. Пустое
    /// значение означает первый запуск: тогда окно встаёт посреди экрана, как и
    /// было.
    /// </remarks>
    public Func<(double Left, double Top)?>? RestorePlacement { get; init; }

    /// <summary>Куда панель переехала. Зовётся при закрытии.</summary>
    public Action<double, double>? SavePlacement { get; init; }

    /// <summary>До какого размера панель растянули в прошлый раз.</summary>
    public Func<(double Width, double Height)?>? RestoreSize { get; init; }

    /// <summary>Новый размер после растягивания мышью.</summary>
    public Action<double, double>? SaveSize { get; init; }

    /// <summary>Ширина содержимого, на которой панель свёрстана. Всё шире — масштаб.</summary>
    private double _baseFrameWidth;

    /// <summary>Идёт ли растягивание мышью прямо сейчас.</summary>
    private bool _isUserSizing;

    /// <summary>Самый крупный масштаб.</summary>
    ///
    /// <remarks>
    /// С 0.1.60 — единица: масштаба больше нет. Окно растягивают ради горячих
    /// клавиш (29 сентября 2026, владелец), а масштаб растил всё сразу — поле
    /// номера, кнопки управления, «Позвонить», — и на экране ноутбука клавиш
    /// от этого не прибавлялось, зато окно уходило за край. Теперь лишняя
    /// ширина и высота достаются только сетке клавиш. Механизм оставлен: он
    /// же делает чёткой разметку при масштабе единица.
    /// </remarks>
    private const double MaximumZoom = 1.0;

    /// <summary>Высота содержимого без масштаба и растяжения.</summary>
    private double _baseContentHeight;

    /// <summary>Рабочая область монитора, на котором стоит панель, в точках WPF.</summary>
    ///
    /// <remarks>
    /// Своего монитора, а не основного: панель часто стоит на втором экране,
    /// рядом с CRM, и мерить её по основному значит уводить её за чужой край.
    /// </remarks>
    private Rect WorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };

        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info)
            || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
        {
            return SystemParameters.WorkArea;
        }

        // Пиксели монитора — в точки WPF: на экране со 150 % это разные числа.
        var fromDevice = target.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));

        return new Rect(topLeft, bottomRight);
    }

    /// <summary>Возвращает панель в пределы монитора, если растягивание вывело её за край.</summary>
    private void KeepOnScreen()
    {
        var work = WorkArea();

        if (Top + ActualHeight > work.Bottom)
        {
            Top = Math.Max(work.Top, work.Bottom - ActualHeight);
        }

        if (Top < work.Top)
        {
            Top = work.Top;
        }

        _bottomEdge = Top + ActualHeight;
    }

    private static class NativeMethods
    {
        internal const uint MonitorDefaultToNearest = 2;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint window, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Исходная ширина содержимого — та, что была бы при 270 точках окна:
        // на ней панель свёрстана. Считается от настоящей ширины за вычетом
        // рамок, а не задаётся числом: рамки у Windows 10 и 11 разные.
        //
        // Окно при этом открывается шире — 324, на пятую часть крупнее
        // (11 сентября 2026: «стандартный размер чуть больше»). Разница
        // уходит в масштаб, а не в раскладку: крупнее становится всё сразу,
        // а не одни растянутые клавиши.
        _baseFrameWidth = Frame.ActualWidth > 0
            ? Frame.ActualWidth - (ActualWidth - MinWidth)
            : 254;
        ApplyZoom();
        UpdateMinimumHeight();

        if (RestoreSize?.Invoke() is { } size && size.Width >= MinWidth)
        {
            SizeToContent = SizeToContent.Manual;
            Width = size.Width;
            Height = Math.Max(size.Height, MinHeight);
        }

        // Содержимое меняет высоту само — вторая линия, поле перевода, число
        // макросов. Растянутому окну нельзя стать меньше того, что в нём
        // лежит, иначе нижняя кнопка уедет за край.
        //
        // Только по тем свойствам, что меняют раскладку: таймер разговора
        // трогает модель каждую секунду, и перемер на каждый такт — это
        // пересборка разметки панели весь рабочий день.
        Model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(PanelViewModel.IsTransferEntryVisible)
                or nameof(PanelViewModel.HasMacros)
                or nameof(PanelViewModel.ShowsMacros)
                or nameof(PanelViewModel.MacroColumns)
                or nameof(PanelViewModel.MacroHeight)
                or nameof(PanelViewModel.IsInCall)
                or nameof(PanelViewModel.Trouble))
            {
                Dispatcher.BeginInvoke(UpdateMinimumHeight, DispatcherPriority.Loaded);
            }
        };

        // Число клавиш меняет число рядов, а `HasMacros` при этом может и не
        // измениться: предустановка на двенадцать клавиш вместо шести.
        Model.Macros.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(UpdateMinimumHeight, DispatcherPriority.Loaded);

        Restore();

        // Сохранённый размер мог прийти с большего монитора или другого
        // масштаба экрана — панель обязана встать целиком.
        if (SizeToContent is SizeToContent.Manual)
        {
            KeepOnScreen();
        }

        // Место запоминается по концу перетаскивания, а не по каждому шагу
        // мыши: настройки пишутся на каждую правку, и запись на каждый пиксель
        // означала бы сотню записей на один перенос окна.
        //
        // Закрытие тоже сохраняет — но одного его мало: машину выключают, не
        // закрывая софтфон, и тогда OnClosed не случается вовсе.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(OnMessage);
        }

        _bottomEdge = Top + ActualHeight;
        _clock.Start();
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;

        if (message == WM_ENTERSIZEMOVE)
        {
            _isUserSizing = true;

            // Раз окно тянут руками, его размер задаёт рука, а не содержимое.
            // Пока `SizeToContent` оставался `Height`, любой перемер разметки
            // посреди перетаскивания возвращал высоту к содержимому — окно
            // «сбрасывалось» под мышью (0.1.60 на живой машине).
            if (SizeToContent is not SizeToContent.Manual)
            {
                var (width, height) = (ActualWidth, ActualHeight);
                SizeToContent = SizeToContent.Manual;
                Width = width;
                Height = height;
            }
        }

        if (message == WM_EXITSIZEMOVE && WindowState is WindowState.Normal)
        {
            _isUserSizing = false;
            UpdateMinimumHeight();

            // Выросшее окно могло уйти за нижний край. Новый низ — тот, что
            // получился в пределах экрана.
            KeepOnScreen();

            SavePlacement?.Invoke(Left, Top);

            if (SizeToContent is SizeToContent.Manual)
            {
                SaveSize?.Invoke(Width, Height);
            }
        }

        return 0;
    }

    /// <summary>Масштаб по ширине: всё содержимое увеличивается равномерно.</summary>
    ///
    /// <remarks>
    /// Через <c>LayoutTransform</c>, а не через пересчёт размеров: содержимое
    /// раскладывается на исходной ширине и увеличивается целиком — кегль,
    /// значки и поля вместе. Пересчёт каждого числа означал бы десятки мест, в
    /// которых растянутая панель разойдётся с исходной.
    ///
    /// Высота при этом решается отдельно: лишняя, сверх нужной содержимому при
    /// этом масштабе, уходит рядам со звёздочкой — макросам и кнопкам.
    /// </remarks>
    private void ApplyZoom()
    {
        if (_baseFrameWidth <= 0)
        {
            return;
        }

        var zoom = Math.Clamp(Frame.ActualWidth / _baseFrameWidth, 1, MaximumZoom);

        // Масштаб растит и высоту, и дальше высоты экрана ему расти некуда:
        // иначе растянутая вширь панель уводит «Позвонить» под панель задач.
        var work = WorkArea();
        var chrome = ActualHeight - Frame.ActualHeight;
        if (_baseContentHeight > 0 && chrome > 0)
        {
            zoom = Math.Max(1, Math.Min(zoom, (work.Height - chrome) / _baseContentHeight));
        }

        if (Math.Abs(Zoom.ScaleX - zoom) < 0.001)
        {
            return;
        }

        Zoom.ScaleX = zoom;
        Zoom.ScaleY = zoom;

        // Чёткость увеличенного.
        //
        // Панель свёрстана под экранную сетку: текст в режиме `Display`
        // подгоняется под пиксели исходного кегля, а края элементов
        // округляются до целых точек. Под увеличением то и другое тянется как
        // готовая картинка — привязанные к сетке буквы и однопиксельные края
        // рассыпались на ступеньки, что и было видно в 0.1.47.
        //
        // В увеличенном окне текст считается в режиме `Ideal` — по контурам
        // шрифта на итоговом размере, — и округление снимается: пиксель
        // исходной сетки больше не существует, и держаться за него незачем.
        // В исходном размере всё остаётся как было — там экранная сетка и
        // даёт самый чёткий мелкий текст.
        var zoomed = zoom > 1.001;
        TextOptions.SetTextFormattingMode(
            Root, zoomed ? TextFormattingMode.Ideal : TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(
            Root, zoomed ? TextRenderingMode.Grayscale : TextRenderingMode.Auto);
        Root.UseLayoutRounding = !zoomed;
        Root.SnapsToDevicePixels = !zoomed;

        UpdateMinimumHeight();
    }

    /// <summary>Меньше содержимого окну становиться нельзя.</summary>
    private void UpdateMinimumHeight()
    {
        if (!IsLoaded && _baseFrameWidth <= 0)
        {
            return;
        }

        // Посреди перетаскивания не трогать: смена предела клавиш — это
        // перемер всей панели на каждый шаг мыши. Пересчёт — по отпусканию.
        if (_isUserSizing)
        {
            return;
        }

        // Сколько содержимому нужно без растяжения — замер на бесконечной
        // высоте при нынешней ширине. Ряды со звёздочкой при таком замере
        // берут свой минимум, то есть ровно исходную высоту панели, а масштаб
        // в замер уже входит: у элемента с `LayoutTransform` желаемый размер
        // — размер после преобразования.
        var width = Frame.ActualWidth > 0 ? Frame.ActualWidth : _baseFrameWidth;

        // Сначала — без предела: на большем мониторе клавиши возвращаются к
        // высоте из настроек.
        Model.MacroHeightLimit = double.PositiveInfinity;
        var content = MeasureContent(width);

        // Рамка и полоса заголовка — всё, что вне содержимого.
        var chrome = ActualHeight - Frame.ActualHeight;
        if (chrome <= 0 || double.IsNaN(chrome))
        {
            _baseContentHeight = content / Zoom.ScaleY;
            Root.InvalidateMeasure();
            return;
        }

        // Выше экрана окну быть нельзя. Не влезает — убавляется высота клавиш,
        // а не срезается низ: «Позвонить» и «Завершить» обязаны оставаться на
        // экране при любом числе клавиш (0.1.58: двенадцать клавиш в два ряда
        // уводили окно верхом за край).
        var room = WorkArea().Height - chrome;
        var rows = MacroRows();
        if (FitMacroHeight(content, room, rows, Zoom.ScaleY, Model.MacroHeight) is { } limit)
        {
            Model.MacroHeightLimit = limit;
            content = MeasureContent(width);
        }

        _baseContentHeight = content / Zoom.ScaleY;

        // Следующий проход разметки обязан перемерить по-настоящему, а не
        // взять замер на бесконечной высоте.
        Root.InvalidateMeasure();

        MinHeight = Math.Ceiling(Math.Min(content, room) + chrome);
        MaxHeight = Math.Max(MinHeight, room + chrome);
    }

    /// <summary>Ниже этого клавиша перестаёт быть мишенью для мыши.</summary>
    internal const double MinimumMacroHeight = 30;

    /// <summary>
    /// Высота клавиши, при которой содержимое влезает в экран; <c>null</c> —
    /// влезает и так.
    /// </summary>
    ///
    /// <param name="content">Высота содержимого в точках экрана, с масштабом.</param>
    /// <param name="room">Сколько высоты у содержимого на мониторе.</param>
    /// <param name="rows">Рядов клавиш.</param>
    /// <param name="zoom">Масштаб панели: клавиша задана в точках до него.</param>
    /// <param name="macroHeight">Высота клавиши из настроек.</param>
    internal static double? FitMacroHeight(double content, double room, int rows, double zoom, double macroHeight)
    {
        if (content <= room || rows <= 0 || zoom <= 0)
        {
            return null;
        }

        var excess = (content - room) / zoom;
        return Math.Max(MinimumMacroHeight, macroHeight - Math.Ceiling(excess / rows));
    }

    /// <summary>
    /// Сколько содержимому нужно без растяжения — замер на бесконечной высоте
    /// при нынешней ширине.
    /// </summary>
    private double MeasureContent(double width)
    {
        Root.Measure(new Size(width, double.PositiveInfinity));
        return Root.DesiredSize.Height;
    }

    /// <summary>Рядов в видимой сетке клавиш; 0 — сетки не видно.</summary>
    private int MacroRows()
    {
        if (!Model.HasMacros || !Model.ShowsMacros)
        {
            return 0;
        }

        var columns = Math.Max(1, Model.MacroColumns);
        return (Model.Macros.Count + columns - 1) / columns;
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();

        if (WindowState is WindowState.Normal)
        {
            SavePlacement?.Invoke(Left, Top);
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Возвращает панель туда, где её оставили.
    /// </summary>
    ///
    /// <remarks>
    /// <b>С проверкой, что это место ещё существует.</b> Панель, оставленная на
    /// втором мониторе, после его отключения оказалась бы за краем рабочего
    /// стола — то есть невидимой, притом что приложение считает её показанной.
    /// Не поместилась — встаёт посреди основного экрана, как при первом запуске.
    /// </remarks>
    private void Restore()
    {
        if (RestorePlacement?.Invoke() is not { } placement)
        {
            return;
        }

        var visible = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        // Требуется, чтобы на экране оказался весь заголовок с кнопками, а не
        // уголок окна: панель, у которой видны две точки, не перетащить мышью.
        var wanted = new Rect(placement.Left, placement.Top, ActualWidth, ActualHeight);

        if (!visible.Contains(wanted))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
    }

    /// <summary>Enter в поле номера звонит.</summary>
    private void OnDialedNumberKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not System.Windows.Input.Key.Enter)
        {
            return;
        }

        // Через ту же команду, что и кнопка: у неё уже есть все проверки —
        // пустой номер, отсутствие регистрации, идущий разговор.
        if (Model.CallOrHangUp?.CanExecute(null) is true)
        {
            Model.CallOrHangUp.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Звук нажатия на каждую набранную цифру.</summary>
    ///
    /// <remarks>
    /// По вводу текста, а не по нажатию клавиши: так звучит и цифра с верхнего
    /// ряда, и с цифрового блока, и вставка не звучит пачкой.
    /// </remarks>
    private void OnDialedNumberTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (e.Text.Length == 1)
        {
            Model.KeyPressed?.Invoke(e.Text[0]);
        }
    }

    /// <summary>Держит нижний край окна на месте, когда середина меняет высоту.</summary>
    ///
    /// <remarks>
    /// Это то место, где Windows расходится с macOS, и разойтись ей есть на чём.
    /// Начало координат окна на macOS — левый нижний угол, поэтому выросшее
    /// окно там раздаётся вверх само, и правило «низ не двигается» соблюдается
    /// без единой строки кода. На Windows начало — левый верхний, и окно растёт
    /// вниз: открытое поле перевода уводило бы кнопку «Завершить» на 70 точек
    /// вниз ровно в тот момент, когда до неё тянутся не глядя.
    ///
    /// Поэтому верхний край сдвигается на разницу высот. Считается это от
    /// запомненного низа, а не «текущий Top минус дельта»: округления
    /// накапливались бы, и за день окно уползало бы вверх по точке за
    /// перевод.
    /// </remarks>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (sizeInfo.WidthChanged)
        {
            ApplyZoom();
        }

        // Пока оператор тянет окно мышью, край двигает он сам: удержание низа
        // здесь дёргало бы окно навстречу его руке.
        if (_isUserSizing)
        {
            return;
        }

        if (!sizeInfo.HeightChanged || _bottomEdge <= 0)
        {
            return;
        }

        // Свёрнутое и развёрнутое окно двигать нельзя: у первого координаты
        // ничего не значат, у второго их задаёт система.
        if (WindowState is not WindowState.Normal)
        {
            return;
        }

        // Низ держится только за содержимым — поле перевода, вторая линия.
        // Высоту ручного окна меняет не содержимое, а Windows: восстановление
        // после «Развернуть», двойной щелчок по верхней или нижней грани
        // (растянуть на всю высоту экрана). Удержание низа в этих случаях
        // сдвигало окно на разницу высот — за верх экрана, и с каждым
        // «Развернуть — Восстановить» всё дальше (0.1.63 на живой машине).
        if (SizeToContent is SizeToContent.Manual)
        {
            _bottomEdge = Top + sizeInfo.NewSize.Height;
            return;
        }

        Top = _bottomEdge - sizeInfo.NewSize.Height;
        KeepOnScreen();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // Вернулись из развёрнутого или свёрнутого — положение задала система,
        // и держать надо новый низ, а не тот, что был до разворота. Позже, а не
        // сразу: размер и место восстановленного окна приходят следующими
        // сообщениями.
        if (WindowState is WindowState.Normal)
        {
            Dispatcher.BeginInvoke(() =>
            {
                KeepOnScreen();
                _bottomEdge = Top + ActualHeight;
            }, DispatcherPriority.Loaded);
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);

        // Окно передвинули мышью — значит, новый низ и есть тот, который надо
        // держать. Без этого панель, оттащенная в угол экрана, прыгала бы
        // обратно к прежнему низу при первом же изменении высоты.
        if (WindowState is WindowState.Normal && IsLoaded)
        {
            _bottomEdge = Top + ActualHeight;
        }
    }

    /// <summary>Закрытие панели прячет её в область уведомлений, а не гасит.</summary>
    ///
    /// <remarks>
    /// Софтфон, закрытый крестиком, обязан продолжать принимать звонки:
    /// оператор закрывает панель, чтобы она не мешала, а не чтобы перестать
    /// быть на линии. Выход живёт в меню значка — единственном месте, откуда
    /// его видно при спрятанной панели.
    ///
    /// Своих кнопок окна у панели больше нет — полоса заголовка системная, — и
    /// это место стало единственным, где закрытие перехватывается. Раньше их
    /// было два: свой крестик и вот эта проверка, и совпадали они только
    /// потому, что делали одно и то же руками.
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // Крестик в полосе заголовка, Alt+F4 и «Закрыть» из панели задач —
        // теперь один и тот же путь.
        if (!AllowsClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>Разрешено ли окну закрыться по-настоящему. Ставит выход.</summary>
    public bool AllowsClosing { get; set; }

    private void OnClearNumberClick(object sender, RoutedEventArgs e)
        => Model.DialedNumber = string.Empty;

    /// <summary>Раскрывает меню профиля под капсулой.</summary>
    ///
    /// <remarks>
    /// Руками, а не само: меню, привязанное к кнопке через
    /// <c>ContextMenu</c>, WPF открывает по правому щелчку — так устроено
    /// контекстное меню. Здесь же оно не контекстное, а выпадающее: открывать
    /// его обязан обычный щелчок, тот же, каким открываются все прочие списки
    /// в приложении.
    ///
    /// Место назначается тут же: без <c>PlacementTarget</c> меню встаёт под
    /// курсором, то есть каждый раз в новом месте, а список под кнопкой обязан
    /// открываться под кнопкой.
    /// </remarks>
    /// <summary>
    /// Капсула профиля — не шире 60 % строки состояния.
    /// </summary>
    ///
    /// <remarks>
    /// Колонка капсулы растёт по содержимому, и длинная подпись профиля
    /// («ТестиковичАСУС») выталкивала шестерёнку настроек за край панели.
    /// Предел относительный: панель растягивают, и абсолютное число было бы
    /// либо тесным на широкой, либо бесполезным на узкой. Остальное место — слоту
    /// беды и шестерёнке, которые не должны исчезать никогда.
    /// </remarks>
    private void OnStatusRowSizeChanged(object sender, SizeChangedEventArgs e)
        => ProfilePill.MaxWidth = Math.Max(120, e.NewSize.Width * 0.6);

    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button pill || pill.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = pill;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = -8;
        menu.VerticalOffset = -4;

        // Данные меню берёт у кнопки: всплывающее живёт вне дерева окна, и
        // `DataContext` туда сам не доходит.
        menu.DataContext = DataContext;
        FillProfiles(menu);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Строки профилей в меню капсулы.
    /// </summary>
    ///
    /// <remarks>
    /// Профиль один — остаётся прежняя строка «номер · подпись» с галочкой
    /// «на линии». Несколько (номера сотрудника из Spark) — каждый своей
    /// строкой, активный с галочкой; щелчок по другому переключает и
    /// перерегистрирует. Строки собираются заново при каждом открытии:
    /// список меняется конфигурацией из Spark, пока меню закрыто.
    /// </remarks>
    private void FillProfiles(System.Windows.Controls.ContextMenu menu)
    {
        foreach (var stale in menu.Items.OfType<System.Windows.Controls.MenuItem>()
                     .Where(item => Equals(item.Tag, "profile")).ToList())
        {
            menu.Items.Remove(stale);
        }

        var single = menu.Items.OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(item => Equals(item.Tag, "single-profile"));

        var choices = Model.ReadProfiles?.Invoke() ?? [];
        var many = choices.Count > 1;

        if (single is not null)
        {
            single.Visibility = many ? Visibility.Collapsed : Visibility.Visible;
        }

        if (!many)
        {
            return;
        }

        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var item = new System.Windows.Controls.MenuItem
            {
                Header = choice.Title,
                IsChecked = choice.IsActive,
                Tag = "profile",
            };

            item.Click += (_, _) =>
            {
                if (!choice.IsActive)
                {
                    Model.SwitchProfile?.Invoke(choice.ProfileId);
                }
            };

            menu.Items.Insert(i, item);
        }
    }

    private void OnTransferClick(object sender, RoutedEventArgs e)
    {
        Model.ShowTransferEntry();

        // Поле открывается ради ввода, значит и курсор в нём. Без этого
        // оператор жмёт «Перевести», начинает набирать и обнаруживает, что
        // цифры уходят в поле набора над ним.
        TransferField.Focus();
    }

    private void OnCancelTransferClick(object sender, RoutedEventArgs e)
        => Model.CancelTransferEntry();
}
