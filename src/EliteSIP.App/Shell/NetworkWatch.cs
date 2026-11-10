using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EliteSIP.App.Shell;

/// <summary>
/// Две вещи, которых у настольного телефона не бывает: сон и переезд в другую
/// сеть.
/// </summary>
///
/// <remarks>
/// У ноутбука они случаются дважды в день — закрыли в офисе, открыли дома, — и
/// оба раза софтфон остаётся с мёртвым сокетом и вчерашним адресом в
/// <c>Contact</c>. Сервер об этом не знает: регистрация у него живёт до конца
/// срока, и всё это время вызовы уходят в никуда.
///
/// Пробуждение и смена адреса требуют заново открыть связь и повторить стук.
/// При смене таблицы маршрутов сначала проверяем путь до АТС: VPN может
/// перенаправить его, не меняя IP-адреса компьютера.
///
/// <b>Смена адреса приходит очередью.</b> Одно переключение Wi-Fi даёт несколько
/// событий подряд: адрес пропал, адрес появился, появился второй у виртуального
/// адаптера. Отвечать на каждое значило бы поднимать регистрацию три раза за
/// секунду, поэтому события собираются в одно с небольшой паузой — заодно она
/// даёт стеку дописать таблицу маршрутов.
/// </remarks>
internal sealed class NetworkWatch : IDisposable
{
    /// <summary>
    /// Сколько ждать, пока сеть перестанет меняться.
    ///
    /// Три секунды: меньше — и мы поднимаемся раньше, чем у адаптера появится
    /// маршрут; больше — и человек успевает заметить, что телефон молчит.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly Action _onNetworkChanged;
    private readonly Action _onResume;
    private readonly Action<string> _log;
    private readonly Func<bool> _sipRouteChanged;
    private readonly DispatcherTimer _settling;
    private readonly RouteChangeCallback _routeCallback;
    private readonly nint _routeNotification;

    private bool _disposed;
    private bool _addressChanged;

    internal NetworkWatch(Action onNetworkChanged, Action onResume, Func<bool> sipRouteChanged, Action<string> log)
    {
        _onNetworkChanged = onNetworkChanged;
        _onResume = onResume;
        _sipRouteChanged = sipRouteChanged;
        _log = log;

        _settling = new DispatcherTimer { Interval = Settle };
        _settling.Tick += (_, _) =>
        {
            _settling.Stop();
            bool addressChanged = _addressChanged;
            _addressChanged = false;
            if (addressChanged || _sipRouteChanged())
            {
                _onNetworkChanged();
            }
        };

        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _routeCallback = (_, _, _) => ScheduleCheck(addressChanged: false);
        uint error = NotifyRouteChange2(0, _routeCallback, 0, false, out _routeNotification);
        if (error != 0)
        {
            _log($"уведомления об IP-маршрутах недоступны: код Windows {error}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Подписка на `SystemEvents` статическая и живёт дольше приложения:
        // неотписанная стреляет по закрытым окнам. То же правило, что у темы и
        // у значка.
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _settling.Stop();
        _disposed = true;
        if (_routeNotification != 0)
        {
            _ = CancelMibChangeNotify2(_routeNotification);
            GC.KeepAlive(_routeCallback);
        }
    }

    private void OnAddressChanged(object? sender, EventArgs e)
        => ScheduleCheck(addressChanged: true);

    private void ScheduleCheck(bool addressChanged)
    {
        // Событие приходит не в потоке интерфейса, а таймер — его. Перезапуск
        // таймера и есть склейка очереди событий в одно.
        _settling.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _addressChanged |= addressChanged;
            _settling.Stop();
            _settling.Start();
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not PowerModes.Resume)
        {
            return;
        }

        // Уход в сон не обрабатывается намеренно: снимать регистрацию перед
        // сном значило бы пропустить вызовы у машины, которая просто погасила
        // экран, — а Windows усыпляет и по бездействию.
        _log("машина проснулась: связь поднимается заново");
        _onResume();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RouteChangeCallback(nint context, nint row, int notificationType);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint NotifyRouteChange2(
        ushort addressFamily, RouteChangeCallback callback, nint context,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification, out nint notificationHandle);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint CancelMibChangeNotify2(nint notificationHandle);
}
