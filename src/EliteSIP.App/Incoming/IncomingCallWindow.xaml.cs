using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EliteSIP.CallGuard;

namespace EliteSIP.App.Incoming;

/// <summary>Плавающее окно входящего вызова.</summary>
///
/// <remarks>
/// Три требования, и каждое само по себе достаточно, чтобы окно не было
/// обычным:
///
/// <list type="bullet">
/// <item>оно не должно забирать фокус — оператор в этот момент печатает в CRM,
/// и активация чужого приложения посреди набора недопустима;</item>
/// <item>оно должно висеть поверх всех окон, включая чужие полноэкранные;</item>
/// <item>позиция задаётся точно и случайно, в аппаратных пикселях.</item>
/// </list>
///
/// <b>Здесь Windows жёстче macOS.</b> На macOS <c>orderFrontRegardless</c>
/// поднимал окно поверх всего без активации одной строкой. Windows не даёт
/// произвольно вытащить окно на передний план: <c>SetForegroundWindow</c>
/// работает только у процесса, который уже владеет фокусом. Поэтому окно
/// показывается без активации (<c>WS_EX_NOACTIVATE</c> плюс
/// <c>ShowActivated=False</c>) и держится поверх остальных признаком
/// <c>Topmost</c>, а внимание оператора добирается миганием кнопки приложения в
/// панели задач и значком в области уведомлений.
///
/// Позиция задаётся через <c>SetWindowPos</c> в пикселях, а не свойствами
/// <c>Left</c> и <c>Top</c> в точках WPF. Причина не в удобстве: расчёт защиты и
/// координаты курсора из хука живут в пикселях, а перевод в точки на машине с
/// двумя экранами разного масштаба — это ещё один источник ошибки ровно там,
/// где ошибка означает окно за краем экрана и непринятый лид.
/// </remarks>
public partial class IncomingCallWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    public IncomingCallWindow(IncomingCallViewModel model)
    {
        InitializeComponent();
        DataContext = model;

        // Далеко за пределами любого экрана: настоящий размер окно узнаёт
        // только при показе, а позиция считается по размеру. Показать его на
        // месте по умолчанию и переставить потом значило бы моргнуть окном в
        // середине экрана — то есть ровно в той точке, которую случайная
        // позиция и должна отменить.
        Left = -32000;
        Top = -32000;
    }

    /// <summary>Рамка окна в аппаратных пикселях — та, по которой считается защита.</summary>
    public ScreenRect PhysicalFrame
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == 0 || !GetWindowRect(handle, out var rect))
            {
                return new ScreenRect(0, 0, 0, 0);
            }

            return new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    /// <summary>Ставит окно углом в заданную точку экрана.</summary>
    public void PlaceAt(ScreenPoint origin)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        SetWindowPos(
            handle,
            HwndTopmost,
            (int)Math.Round(origin.X),
            (int)Math.Round(origin.Y),
            0,
            0,
            SwpNoSize | SwpNoActivate);
    }

    /// <summary>Мигает кнопкой приложения в панели задач.</summary>
    ///
    /// <param name="owner">Окно с кнопкой — панель софтфона.</param>
    ///
    /// <remarks>
    /// Мигает <b>панель</b>, а не окно входящего: у окна вызова своей кнопки в
    /// панели задач нет намеренно (<c>ShowInTaskbar=False</c>) — вторая кнопка
    /// того же приложения, живущая полминуты, читается как второе приложение.
    /// А <c>FlashWindowEx</c> окну без кнопки мигать нечем: оно рисует полосу
    /// заголовка, которой у карточки вызова тоже нет.
    /// </remarks>
    public static void FlashTaskbar(nint owner)
    {
        if (owner == 0)
        {
            return;
        }

        var info = new FlashInfo
        {
            cbSize = (uint)Marshal.SizeOf<FlashInfo>(),
            hwnd = owner,

            // FLASHW_TRAY | FLASHW_TIMERNOFG: мигать, пока оператор не выйдет на
            // приложение. Ограничить числом вспышек нельзя — вызов ждёт
            // тридцать секунд, а три вспышки заканчиваются за секунду.
            dwFlags = 0x00000002 | 0x0000000C,
            uCount = 0,
            dwTimeout = 0,
        };

        FlashWindowEx(ref info);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;

        // NOACTIVATE — чтобы окно не забирало фокус ни при показе, ни при
        // нажатии на него. TOOLWINDOW — чтобы оно не попадало в переключение по
        // Alt+Tab: оператор, переключающийся между CRM и почтой, не должен
        // натыкаться на карточку вызова как на ещё одно приложение.
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);

        HwndSource.FromHwnd(handle)?.AddHook(OnWindowMessage);
    }

    private nint OnWindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Нажатие по окну активирует его даже при NOACTIVATE, если окно об этом
        // не попросит: фокус уходит из CRM ровно в тот момент, когда оператор
        // тянется к кнопке, и набранное в чужом поле теряется.
        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }

        return 0;
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
    private struct FlashInfo
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
