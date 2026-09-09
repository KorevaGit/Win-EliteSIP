using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;

namespace EliteSIP.App.Shell;

/// <summary>
/// Значок в области уведомлений: состояние телефона и второй вход к панели.
/// </summary>
///
/// <remarks>
/// <b>Значок — не единственный вход, а второй.</b> Панель осталась обычным
/// окном; разница в том, что закрытие панели теперь означает не «выйти», а
/// «свернуть приложение в область уведомлений». Выход живёт в меню значка — на
/// Windows это единственное место, откуда его видно при спрятанной панели.
///
/// <b>Почему <c>Shell_NotifyIcon</c> руками, а не <c>NotifyIcon</c> из Windows
/// Forms.</b> Готовый значок стоил бы включения всего набора Forms в проект, а
/// вместе с ним — неявного <c>using System.Windows.Forms</c>, от которого
/// <c>Application</c>, <c>Control</c> и <c>KeyEventArgs</c> становятся
/// двусмысленными во всех окнах: у Forms и у WPF они одноимённые. Снять этот
/// импорт в проекте с XAML не вышло — набор добавляет его после тела проекта, и
/// правки в теле до него не доходят. Сотня строк здесь дешевле, чем
/// двусмысленность в каждом файле приложения; заодно меню остаётся своим,
/// нарисованным темой приложения, а не чужой.
///
/// <b>Значок рисуется руками, а не берётся готовым файлом.</b> Причина та же,
/// что в оригинале: цветная точка состояния — часть значка, и меняется она
/// вчетверо чаще самого значка. Готовых <c>.ico</c> пришлось бы держать четыре
/// штуки и следить, чтобы они не разъехались с палитрой.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;

    /// <summary>Сообщение о щелчке по значку. Своё, из области WM_APP.</summary>
    private const int CallbackMessage = 0x0400 + 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private readonly PanelViewModel _model;
    private readonly TrayActions _actions;
    private readonly HwndSource _window;
    private readonly ContextMenu _menu = new();
    private nint _iconHandle;
    private bool _disposed;

    public TrayIcon(PanelViewModel model, TrayActions actions)
    {
        _model = model;
        _actions = actions;

        // Своё окно, никогда не показываемое: щелчки по значку система
        // присылает сообщением, а слать его некуда, кроме как окну.
        _window = new HwndSource(new HwndSourceParameters("EliteSIP.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });

        _window.AddHook(OnMessage);

        var data = Data();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = CurrentIcon();
        data.szTip = StatusTitle;

        // Ответ системы проверяется, и это не педантизм: если значок не встал,
        // прятать за него панель нельзя — приложение окажется запущенным,
        // невидимым и без выхода. Тогда крестик панели снова означает выход.
        IsAdded = Shell_NotifyIcon(NIM_ADD, ref data);

        _model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(PanelViewModel.Registration)
                or nameof(PanelViewModel.IsOfflineByChoice)
                or nameof(PanelViewModel.StatusTitle))
            {
                Redraw();
            }
        };
    }

    /// <summary>Встал ли значок в области уведомлений.</summary>
    public bool IsAdded { get; }

    /// <summary>Что умеет меню. Замыкания, а не ссылка на окна.</summary>
    ///
    /// <remarks>
    /// Меню ничего не знает про окна — оно только зовёт и только спрашивает.
    /// </remarks>
    public sealed record TrayActions(
        Func<bool> IsPanelVisible,
        Action TogglePanel,
        Action ShowHistory,
        Action ShowSettings,
        Action ToggleOffline,
        Action Quit);

    private string StatusTitle
    {
        get
        {
            if (_model.IsOfflineByChoice)
            {
                return Strings.Get("TrayOffline");
            }

            var state = Strings.Get(_model.Registration switch
            {
                RegistrationState.Registered => "TrayRegistered",
                RegistrationState.Registering => "TrayRegistering",
                RegistrationState.Failed => "TrayFailed",
                _ => "TrayIdle",
            });

            return string.IsNullOrEmpty(_model.StatusTitle) || _model.StatusTitle == "—"
                ? state
                : state + " · " + _model.StatusTitle;
        }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != CallbackMessage)
        {
            return 0;
        }

        // Левый щелчок открывает панель, правый — меню. Разводить их приходится
        // самим: система присылает оба одним сообщением.
        switch ((int)lParam)
        {
            case WM_LBUTTONUP:
                _actions.TogglePanel();
                handled = true;
                break;

            case WM_RBUTTONUP:
                ShowMenu();
                handled = true;
                break;

            default:
                break;
        }

        return 0;
    }

    /// <summary>Собирает и показывает меню.</summary>
    ///
    /// <remarks>
    /// Пересобирается перед каждым показом: состояние в первой строке живое, а
    /// собранный однажды список показывал бы регистрацию такой, какой она была
    /// при запуске.
    /// </remarks>
    private void ShowMenu()
    {
        _menu.Items.Clear();

        // Первая строка — состояние словами. Цветная точка на значке говорит
        // «плохо», но не говорит «почему», а при спрятанной панели узнать
        // причину больше неоткуда.
        _menu.Items.Add(new MenuItem { Header = StatusTitle, IsEnabled = false });
        _menu.Items.Add(new Separator());

        // «Показать» и «Скрыть» — один пункт с двумя подписями: два отдельных,
        // из которых один всегда погашен, читаются как поломка.
        Add(Strings.Get(_actions.IsPanelVisible() ? "TrayHidePanel" : "TrayShowPanel"), _actions.TogglePanel);
        Add(Strings.Get("TrayHistory"), _actions.ShowHistory);
        Add(Strings.Get("TraySettings"), _actions.ShowSettings);

        _menu.Items.Add(new Separator());

        // Уход с линии — единственное здесь, что меняет состояние телефона.
        // Держится в меню потому, что сняться на обед надо каждый день, а ради
        // этого иначе приходится разворачивать панель.
        var offline = Add(Strings.Get("TrayDoNotDisturb"), _actions.ToggleOffline);
        offline.IsChecked = _model.IsOfflineByChoice;
        offline.IsCheckable = true;

        // В разговоре недоступно по той же причине, что и смена профиля:
        // отключение снимает регистрацию.
        offline.IsEnabled = !_model.IsInCall;

        _menu.Items.Add(new Separator());
        Add(Strings.Get("TrayQuit"), _actions.Quit);

        // Меню закрывается по щелчку мимо только у окна, которое система
        // считает передним. Своё окно у значка невидимое, но передним стать
        // может — без этого меню повисает на экране до следующего щелчка по
        // значку.
        _ = SetForegroundWindow(_window.Handle);

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private MenuItem Add(string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
        return item;
    }

    /// <summary>Перерисовывает значок под нынешнее состояние.</summary>
    private void Redraw()
    {
        var previous = _iconHandle;

        var data = Data();
        data.uFlags = NIF_ICON | NIF_TIP;
        data.hIcon = CurrentIcon();
        data.szTip = StatusTitle;
        _ = Shell_NotifyIcon(NIM_MODIFY, ref data);

        // Прежняя ручка освобождается после подмены, а не до: система рисует
        // значок до этого мгновения.
        if (previous != 0)
        {
            _ = DestroyIcon(previous);
        }
    }

    private nint CurrentIcon()
    {
        _iconHandle = Draw(DotBrush());
        return _iconHandle;
    }

    private Brush DotBrush()
    {
        if (_model.IsOfflineByChoice)
        {
            return Resource("StatusOfflineBrush");
        }

        return _model.Registration switch
        {
            RegistrationState.Registered => Resource("StatusRegisteredBrush"),
            RegistrationState.Registering => Resource("StatusConnectingBrush"),
            RegistrationState.Failed => Resource("StatusFailureBrush"),
            _ => Resource("StatusOfflineBrush"),
        };
    }

    private static Brush Resource(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>Золото короны — то же, что на значке приложения.</summary>
    ///
    /// <remarks>
    /// Градиент, а не плоский цвет: на значке macOS-версии корона залита
    /// переходом от светлого золота к тёмному, и в области уведомлений она
    /// обязана выглядеть тем же предметом, а не его перерисовкой.
    /// </remarks>
    private static readonly Brush CrownGold = new LinearGradientBrush(
        Color.FromRgb(0xE8, 0xC8, 0x6A),
        Color.FromRgb(0xB8, 0x86, 0x2F),
        angle: 90)
    {
        // Заморожена: кисть общая на все перерисовки, а незамороженная тянет за
        // собой поток, в котором её создали.
    };

    /// <summary>Рисует корону значка приложения и точку состояния под ней.</summary>
    ///
    /// <remarks>
    /// Размер 32, а не 16: Windows берёт из значка ту величину, которая ей
    /// нужна, и на масштабе 150% просит как раз 24. Нарисованный в 16 значок
    /// там растягивается и мылится.
    ///
    /// <b>Корона без тёмной плашки, хотя значок приложения — плашка с короной.</b>
    /// Плашку пробовали: в области уведомлений она занимает весь квадрат, и
    /// корона внутри неё на 16 точках сжимается до пятна — значок читается как
    /// тёмный прямоугольник и теряется среди соседей. Предмет один и тот же,
    /// фон у него разный: у окна и в проводнике значок стоит на своём поле, в
    /// области уведомлений полем служит панель задач.
    ///
    /// Фигура — из того же комплекта, что и остальные значки приложения
    /// (<c>crown.fill</c> в <c>Theme/Icons.xaml</c>), поэтому вторая правда о
    /// форме короны не заводится.
    ///
    /// Точка состояния остаётся: она отвечает на вопрос, которого у значка нет,
    /// — жива ли регистрация.
    /// </remarks>
    private static nint Draw(Brush dot)
    {
        const int size = 32;

        var visual = new DrawingVisual();
        using (var canvas = visual.RenderOpen())
        {
            var crown = Geometry.Parse(
                "M1.6 16.2V3.4c0-.5.6-.75.95-.4l3.7 3.6L9.3 1.5c.32-.5 1.06-.5 1.38 0l3.07 5.1 "
                + "3.7-3.6c.36-.35.95-.1.95.4v12.8z");

            canvas.PushTransform(new ScaleTransform(size / 20.0, size / 20.0));
            canvas.DrawGeometry(CrownGold, pen: null, crown);
            canvas.Pop();

            // Точка состояния в правом нижнем углу, с тёмной каймой под ней:
            // без каймы зелёное на золоте короны сливается в пятно.
            var centre = new System.Windows.Point(size * 0.76, size * 0.76);
            canvas.DrawEllipse(Brushes.Black, pen: null, centre, size * 0.24, size * 0.24);
            canvas.DrawEllipse(dot, pen: null, centre, size * 0.17, size * 0.17);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        return ToIcon(bitmap);
    }

    /// <summary>Собирает ручку значка из растра.</summary>
    ///
    /// <remarks>
    /// Через файл значка в памяти, а не через <c>CreateIconIndirect</c>: тому
    /// нужны две раздельные битовые карты — цвет и маска, — и полупрозрачные
    /// края сглаженной трубки в маску не помещаются, отчего значок приезжает с
    /// рваной каймой.
    /// </remarks>
    private static nint ToIcon(RenderTargetBitmap bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var png = new MemoryStream();
        encoder.Save(png);

        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // Заголовок ICO: тип 1, одна картинка.
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)1);

            // Ширина и высота нулями — так в ICO записывают 256 и больше, а
            // заодно это единственный способ сказать «бери из самой картинки».
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write((int)png.Length);
            writer.Write(22);
            writer.Write(png.ToArray());
        }

        file.Position = 0;
        var bytes = file.ToArray();
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var offset = LookupIconIdFromDirectoryEx(buffer, fIcon: true, 32, 32, 0);
            return CreateIconFromResourceEx(buffer + offset, (uint)(bytes.Length - offset), fIcon: true, 0x30000, 32, 32, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private NotifyIconData Data() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,

        // Идентификатор постоянный: он и есть тот значок, который потом
        // правится и снимается.
        uID = 1,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Значок снимается руками: неснятый остаётся в области уведомлений
        // призраком до тех пор, пока по нему не проведут мышью.
        var data = Data();
        _ = Shell_NotifyIcon(NIM_DELETE, ref data);

        if (_iconHandle != 0)
        {
            _ = DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }

        _window.Dispose();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern int LookupIconIdFromDirectoryEx(nint directory, bool fIcon, int width, int height, int flags);

    [DllImport("user32.dll")]
    private static extern nint CreateIconFromResourceEx(
        nint resource, uint size, bool fIcon, int version, int width, int height, int flags);
}
