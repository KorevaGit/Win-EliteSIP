using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EliteSIP.App.Resources;
using EliteSIP.App.Theme;
using EliteSIP.CallHistory;

namespace EliteSIP.App.History;

/// <summary>Окно «История звонков».</summary>
public partial class CallHistoryWindow : Window
{
    private readonly AppearanceService _appearance;
    private readonly DispatcherTimer _noticeTimer;

    public CallHistoryWindow(CallHistoryViewModel model, AppearanceService appearance)
    {
        InitializeComponent();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
            ClearValue(TemplateProperty);
            WindowStyle = WindowStyle.SingleBorderWindow;
        }
        Model = model;
        _appearance = appearance;
        DataContext = model;

        // Профиль в заголовке, потому что в строках его больше нет: список и
        // так целиком принадлежит одному профилю, и повторять метку двести раз
        // незачем.
        Title = string.IsNullOrEmpty(model.ProfileTitle)
            ? Strings.Get("HistoryWindowTitle")
            : Strings.Get("HistoryWindowTitleWithProfile")
                .Replace("{0}", model.ProfileTitle, StringComparison.Ordinal);

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice.Visibility = Visibility.Collapsed;
        };
    }

    public CallHistoryViewModel Model { get; }

    private void OnListViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement viewport)
        {
            viewport.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState is WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCaptionCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Кому перезвонить по кнопке в строке. Ставит слой приложения.</summary>
    ///
    /// <remarks>
    /// Событием, а не командой в модели окна: набирает номер не история, а
    /// панель — там поле набора, там же и разрешение звонить. Окно истории
    /// только говорит, какой номер выбрали.
    /// </remarks>
    public event Action<string>? RedialRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SystemCaption.Apply(this, _appearance.IsDark);
    }

    protected override void OnClosed(EventArgs e)
    {
        _noticeTimer.Stop();
        base.OnClosed(e);
    }

    private void OnFilterChosen(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: HistoryFilter filter })
        {
            Model.Filter = filter;

            // Новый отбор — новый список, и он обязан начинаться сверху.
            // Прокрутка от прежнего списка на новом означает, что оператор
            // видит середину чужой выборки и не понимает, почему.
            Scroller.ScrollToTop();
        }
    }

    /// <summary>Догружает следующую страницу, когда список долистали до низа.</summary>
    ///
    /// <remarks>
    /// Порог в одну высоту окна, а не «доехали ровно до конца»: страница в
    /// двести строк читается быстрее, чем приходит следующая, и без запаса
    /// оператор упирается в дно и ждёт.
    /// </remarks>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!Model.HasMore || e.ViewportHeight <= 0)
        {
            return;
        }

        var remaining = e.ExtentHeight - e.VerticalOffset - e.ViewportHeight;
        if (remaining <= e.ViewportHeight)
        {
            Model.LoadMore();
        }
    }

    private void OnCalendarClick(object sender, RoutedEventArgs e) => CalendarPopup.IsOpen = true;

    private void OnCalendarOpened(object sender, EventArgs e) => Model.Calendar.Reset();

    private void OnPreviousMonthClick(object sender, RoutedEventArgs e) => Model.Calendar.ShiftMonth(-1);

    private void OnNextMonthClick(object sender, RoutedEventArgs e) => Model.Calendar.ShiftMonth(1);

    private void OnClearDayClick(object sender, RoutedEventArgs e) => Model.Calendar.SelectedDay = null;

    private void OnDayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CalendarDay day)
        {
            Model.Calendar.SelectedDay = day.Date;

            // Календарь закрывается сам: выбор дня — законченное действие, и
            // оставлять сетку открытой поверх списка, ради которого её
            // открывали, незачем.
            CalendarPopup.IsOpen = false;
            Scroller.ScrollToTop();
        }
    }

    private void OnRedialClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string number } && !string.IsNullOrEmpty(number))
        {
            RedialRequested?.Invoke(number);
        }
    }

    /// <summary>Кладёт снимок списка в буфер обмена.</summary>
    ///
    /// <remarks>
    /// Снимок, а не выгрузка в файл: его пересылают в переписке — «вот эти три
    /// звонка», — и файл на этом пути лишний шаг. Список дополняется шапкой с профилем и текущим отбором.
    /// Кнопки окна и фильтров в снимок не входят.
    ///
    /// Разрешение вдвое против точек: снимок смотрят с телефона, увеличив
    /// пальцами, и на своей плотности он там рассыпается.
    /// </remarks>
    private void OnSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (ListSurface.ActualWidth <= 0 || ListSurface.ActualHeight <= 0)
        {
            return;
        }
        Clipboard.SetImage(CreateSnapshot());
        ShowNotice(Strings.Get("HistorySnapshotDone"));
    }

    /// <summary>Снимок в собственных координатах: шапка, профиль, отбор и скруглённый список.</summary>
    internal BitmapSource CreateSnapshot()
    {
        const double scale = 2;
        const double padding = 12;
        const double header = 56;
        var width = ListSurface.ActualWidth + padding * 2;
        var height = ListSurface.ActualHeight + header + padding * 2;
        var body = new Rect(padding, header + padding, ListSurface.ActualWidth, ListSurface.ActualHeight);
        var image = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var notice = Notice.Visibility;
        Notice.Visibility = Visibility.Collapsed;
        try
        {
            ListSurface.UpdateLayout();
            var drawing = new DrawingVisual();
            using (var canvas = drawing.RenderOpen())
            {
                canvas.DrawRectangle(Background, null, new Rect(0, 0, width, height));
                canvas.PushClip(new RectangleGeometry(body, 12, 12));
                canvas.DrawRectangle(ListSurface.Background, null, body);
                canvas.DrawRectangle(new VisualBrush(ListViewport)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, ListSurface.ActualWidth, ListSurface.ActualHeight),
                    Stretch = Stretch.Fill,
                }, null, body);
                canvas.Pop();
            }
            image.Render(drawing);
            var heading = new DrawingVisual();
            using (var canvas = heading.RenderOpen())
            {
                canvas.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
                var text = (Brush)FindResource("TextPrimaryBrush");
                var secondary = (Brush)FindResource("TextSecondaryBrush");
                var title = new FormattedText(Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 15, text, scale)
                    { MaxTextWidth = width - padding * 2, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                canvas.DrawText(title, new Point(padding, padding));
                var scope = Model.Filter.Title() + " · " + (Model.Calendar.HasSelectedDay ? Model.Calendar.SelectedDayTitle : Strings.Get("HistoryAllDaysLabel"));
                canvas.DrawText(new FormattedText(scope, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, secondary, scale), new Point(padding, padding + 25));
            }
            image.Render(heading);
            image.Freeze();
            return image;
        }
        finally
        {
            Notice.Visibility = notice;
        }
    }

    /// <summary>Показывает сообщение и убирает его само.</summary>
    ///
    /// <remarks>
    /// Своим сообщением в окне, а не системным уведомлением: последнее просит
    /// разрешения, живёт в центре уведомлений и приходит туда же, куда приходят
    /// чужие письма, — для подтверждения нажатия, которое человек только что
    /// сделал и видит, это несоразмерно.
    /// </remarks>
    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        Notice.Visibility = Visibility.Visible;

        // Перезапуск, а не вторая заводка: второй снимок подряд иначе гасился
        // бы таймером первого, то есть через мгновение после нажатия.
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }
}
