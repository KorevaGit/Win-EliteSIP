using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EliteSIP.App.Theme;

/// <summary>
/// Приводит системную полосу заголовка в согласие с палитрой приложения.
/// </summary>
///
/// <remarks>
/// <b>Зачем это вообще нужно.</b> Полосу заголовка рисует не приложение, а
/// диспетчер окон, и палитру для неё он берёт свою — системную. Приложение,
/// которому оператор поставил тёмное оформление при светлой системе, получает
/// тёмное окно под белой полосой: ровно тот стык, ради которого в прошлой
/// версии полосу и рисовали сами.
///
/// Свойства «тёмная полоса» у окна WPF нет и не будет: полоса появилась в
/// Windows 10 1809, а набор свойств окна закрыт с .NET Framework 4. Поэтому
/// оба параметра ставятся диспетчеру напрямую, по номеру.
///
/// <b>Ответ не проверяется.</b> Оба вызова могут не пройти — скруглённых углов
/// нет до Windows 11, а у номера тёмной полосы на ранних сборках 10 было
/// другое значение. Неудача здесь означает окно с системной полосой обычного
/// вида, то есть ровно то, что было бы без этого класса; падать или что-то
/// сообщать не из-за чего.
/// </remarks>
internal static class SystemCaption
{
    /// <summary>Тёмная ли полоса. Номер с Windows 10 2004 и далее.</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>Тот же признак до 2004: номер сменили, смысл остался.</summary>
    private const int UseImmersiveDarkModeLegacy = 19;

    /// <summary>Скругление углов окна. Windows 11 и новее.</summary>
    private const int WindowCornerPreference = 33;

    /// <summary>«Скругляй как принято» — то же, что у системных окон.</summary>
    private const int CornerRound = 2;

    /// <summary>Ставит окну тёмную или светлую полосу и скруглённые углы.</summary>
    ///
    /// <remarks>
    /// Зовётся из <c>OnSourceInitialized</c>, а не из конструктора: до того у
    /// окна нет ручки, а ставится всё именно ручке. И до показа, а не после:
    /// перекрашенная на глазах полоса — это белая вспышка при запуске в тёмной
    /// теме.
    /// </remarks>
    /// <param name="repaint">
    /// Заставить диспетчер перерисовать уже нарисованную рамку. Нужно только
    /// при смене оформления на живом окне — при первом показе рамки ещё нет.
    /// </param>
    public static void Apply(Window window, bool dark, bool repaint = false)
    {
        var handle = new WindowInteropHelper(window).Handle;

        if (handle == 0)
        {
            return;
        }

        var value = dark ? 1 : 0;

        // Сначала нынешний номер, потом прежний — и только если нынешний не
        // взяли. Наоборот нельзя: на новых сборках номер 19 занят другим
        // признаком, и попадание в него означало бы неизвестно что.
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref value, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref value, sizeof(int));
        }

        var corner = CornerRound;
        _ = DwmSetWindowAttribute(handle, WindowCornerPreference, ref corner, sizeof(int));

        if (repaint)
        {
            Repaint(handle);
        }
    }

    /// <summary>Заставляет диспетчер нарисовать полосу заново.</summary>
    ///
    /// <remarks>
    /// Смена признака сама по себе полосу не перекрашивает: диспетчер читает
    /// его, когда рисует рамку, а нарисованную оставляет как есть. Оформление
    /// же меняется на живом окне — оператор переключает его в настройках, и
    /// окно настроек в этот момент открыто. Без этой строки оно и оставалось
    /// светлым под тёмной полосой; найдено живой проверкой.
    ///
    /// Толчок размером — единственное, что сработало. Ни `SWP_FRAMECHANGED`,
    /// ни перестановка признака «туда и обратно» рамку не трогают, что и было
    /// проверено по очереди на живом окне.
    ///
    /// Толкается ширина, а не высота: у панели высоту задаёт содержимое, и она
    /// же держит на месте нижний край окна — толчок по высоте прошёл бы через
    /// этот расчёт. По ширине там расчёта нет, а <c>OnRenderSizeChanged</c>
    /// панели смотрит только на высоту.
    ///
    /// Обе перестановки идут подряд, в одном сообщении очереди: между ними
    /// окно шириной на точку больше, и разъехаться содержимому за это время
    /// негде — WPF пересчитает раскладку один раз, уже после второй.
    /// </remarks>
    private static void Repaint(nint handle)
    {
        if (!GetWindowRect(handle, out var frame))
        {
            return;
        }

        var width = frame.Right - frame.Left;
        var height = frame.Bottom - frame.Top;

        const uint NoMove = 0x0002;
        const uint NoZOrder = 0x0004;
        const uint NoActivate = 0x0010;
        const uint Flags = NoMove | NoZOrder | NoActivate;

        _ = SetWindowPos(handle, 0, 0, 0, width + 1, height, Flags);
        _ = SetWindowPos(handle, 0, 0, 0, width, height, Flags);
    }

    /// <summary>Переставляет полосу всем окнам, которые сейчас на экране.</summary>
    ///
    /// <remarks>
    /// Нужно потому, что оформление меняется на живом приложении: оператор
    /// переключает его в настройках, а Windows — сама, по расписанию «светлая
    /// днём, тёмная вечером». Панель к этому мгновению открыта уже несколько
    /// часов.
    /// </remarks>
    public static void ApplyToOpenWindows(Application application, bool dark)
    {
        foreach (Window window in application.Windows)
        {
            // Перерисовка нужна только показанным: спрятанная панель нарисует
            // рамку заново сама, когда её достанут из области уведомлений.
            Apply(window, dark, repaint: window.IsVisible);
        }
    }

    /// <summary>Ставит полосу каждому окну приложения, как только оно открылось.</summary>
    ///
    /// <remarks>
    /// Обработчик классом, а не строчка в каждом окне: окон семь, заводятся они
    /// в разных местах, и седьмое, добавленное через полгода, про эту строчку
    /// не узнает — а узнает о ней по белой полосе над тёмным окном.
    ///
    /// <c>Loaded</c>, а не <c>SourceInitialized</c>: второе не маршрутизируемое
    /// событие и классом не перехватывается вовсе.
    ///
    /// <b>Ставится дважды, и второй раз — обязательный.</b> К <c>Loaded</c>
    /// ручка у окна уже есть, но рамки ещё нет: диспетчер окон рисует её при
    /// показе и атрибут, поставленный до этого, попросту не учитывает.
    /// Проверено живьём — светлая полоса на светлой теме так и осталась
    /// тёмной, а тот же вызов снаружи, по уже открытому окну, сработал сразу.
    /// Поэтому первый заход стоит ради того, чтобы полоса не мигнула, если
    /// диспетчер всё же успеет, а второй, после <c>ContentRendered</c>, —
    /// чтобы она встала наверняка.
    /// </remarks>
    public static void Watch(Func<bool> isDark)
        => EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is not Window window)
                {
                    return;
                }

                Apply(window, isDark());

                // Подписка одноразовая: `ContentRendered` приходит и на каждый
                // повторный показ спрятанного окна, а панель прячут и достают
                // из области уведомлений весь день.
                void Repeat(object? source, EventArgs _)
                {
                    window.ContentRendered -= Repeat;
                    Apply(window, isDark());
                }

                window.ContentRendered += Repeat;
            }));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect frame);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
