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
        WindowCaption.Apply(this, _appearance.IsDark);
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
    /// звонка», — и файл на этом пути лишний шаг. Снимается ровно плашка со
    /// списком: окно целиком принесло бы в переписку и полосу заголовка с
    /// именем профиля, и кнопки, по которым собеседник щёлкать всё равно не
    /// сможет.
    ///
    /// Разрешение вдвое против точек: снимок смотрят с телефона, увеличив
    /// пальцами, и на своей плотности он там рассыпается.
    /// </remarks>
    private void OnSnapshotClick(object sender, RoutedEventArgs e)
    {
        const double scale = 2;

        var width = (int)Math.Ceiling(ListSurface.ActualWidth * scale);
        var height = (int)Math.Ceiling(ListSurface.ActualHeight * scale);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var image = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        // Плашка полупрозрачна, и снятая как есть она приезжает в переписку с
        // дырами вместо фона. Поэтому под неё подкладывается фон окна — тот
        // самый, поверх которого её и видел человек.
        var backdrop = new DrawingVisual();
        using (var canvas = backdrop.RenderOpen())
        {
            canvas.DrawRectangle(
                Background,
                pen: null,
                new Rect(0, 0, ListSurface.ActualWidth, ListSurface.ActualHeight));
        }

        image.Render(backdrop);
        image.Render(ListSurface);

        Clipboard.SetImage(image);
        ShowNotice(Strings.Get("HistorySnapshotDone"));
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
