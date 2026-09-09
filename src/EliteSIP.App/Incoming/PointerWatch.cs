using System.Runtime.InteropServices;
using EliteSIP.CallGuard;

namespace EliteSIP.App.Incoming;

/// <summary>
/// Слежение за мышью на время одного входящего вызова.
/// </summary>
///
/// <remarks>
/// Сбор фактов, а не решение: путь курсора и происхождение нажатия видны только
/// здесь, а разбирает их <see cref="CallGuardSession"/>. Ровно так же было
/// поделено в оригинале — <c>IncomingCallPanel</c> собирал, пакет решал.
///
/// <b>Здесь Windows сильнее macOS.</b> Оригинал считал путь курсора и смотрел на
/// <c>CGEventSourceStateID</c> — признак, который подделывается парой строк. В
/// Windows низкоуровневый хук отдаёт <c>LLMHF_INJECTED</c>: нажатие от
/// <c>SendInput</c> и <c>mouse_event</c> помечено самой системой, и подделать
/// это можно только драйвером. Драйверные автокликеры по-прежнему не
/// детектируются — как и на macOS.
///
/// Хук именно низкоуровневый и глобальный: движения над чужим окном нужны
/// точно так же, как над своим. Честный оператор в этот момент работает в CRM,
/// и, считай мы только путь над своим окном, он выглядел бы телепортирующимся
/// кликером.
///
/// Координаты хука — <b>физические пиксели экрана</b>, и весь расчёт защиты
/// ведётся в них же. Перевод в точки WPF происходит один раз, в момент, когда
/// окну задают позицию.
/// </remarks>
internal sealed class PointerWatch : IDisposable
{
    private const int WhMouseLowLevel = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;

    /// <summary>Событие создано программно: <c>SendInput</c>, <c>mouse_event</c>.</summary>
    private const int InjectedFlag = 0x00000001;

    /// <summary>То же, но от процесса с правами ниже наших.</summary>
    private const int LowerIntegrityInjectedFlag = 0x00000002;

    // Делегат держится полем: сборщик мусора не знает про ссылку из системы, и
    // хук, отданный временному объекту, умирает молча — в отладке всё работает,
    // а на живой машине курсор перестаёт считаться через несколько минут.
    private readonly HookProc _proc;

    private readonly Action<ScreenPoint> _moved;

    private nint _hook;

    public PointerWatch(Action<ScreenPoint> moved)
    {
        _moved = moved;
        _proc = OnMouseEvent;
        _hook = SetWindowsHookEx(WhMouseLowLevel, _proc, hMod: 0, dwThreadId: 0);
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    /// <summary>Было ли последнее нажатие левой кнопкой помечено системой как программное.</summary>
    ///
    /// <remarks>
    /// Читается обработчиком кнопки: до него событие успевает пройти через хук,
    /// потому что низкоуровневый хук стоит раньше очереди сообщений окна.
    /// </remarks>
    public bool LastClickWasInjected { get; private set; }

    /// <summary>Встал ли хук вообще.</summary>
    ///
    /// <remarks>
    /// Может не встать: политика машины, чужой хук, сеанс без интерактивного
    /// рабочего стола. Молчать об этом нельзя — защита в таком случае держится
    /// на одной случайной позиции, и знать об этом должен разбор, а не догадка.
    /// </remarks>
    public bool IsWatching => _hook != 0;

    /// <summary>Сколько система не видела ввода вообще — ни мыши, ни клавиатуры.</summary>
    ///
    /// <remarks>
    /// Второй признак «человек за столом», которого на macOS не было. Барьером
    /// не служит и служить не может: оператор вправе смотреть в экран не
    /// шевелясь. В журнал идёт как признак — вместе с нулевым путём курсора он
    /// говорит то, чего не говорит ни один из них порознь.
    /// </remarks>
    public static TimeSpan SystemIdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Оба значения — 32-битные счётчики миллисекунд от старта системы, и
        // разность считается в них же: вычитание переживает переполнение через
        // 49 суток, а приведение к длинному до вычитания — нет.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    public static ScreenPoint CursorPosition()
        => GetCursorPos(out var point) ? new ScreenPoint(point.X, point.Y) : new ScreenPoint(0, 0);

    public void Dispose()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    private nint OnMouseEvent(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<MouseLowLevelHookStruct>(lParam);

            switch ((int)wParam)
            {
                case WmMouseMove:
                    _moved(new ScreenPoint(data.pt.X, data.pt.Y));
                    break;

                case WmLeftButtonDown:
                    LastClickWasInjected = (data.flags & (InjectedFlag | LowerIntegrityInjectedFlag)) != 0;
                    break;

                default:
                    break;
            }
        }

        // Ничего не глотаем: хук здесь наблюдатель. Проглоченное нажатие сломало
        // бы чужое приложение, в котором оператор работает, — цена несоизмерима
        // с тем, что мы можем этим выиграть.
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLowLevelHookStruct
    {
        public NativePoint pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
