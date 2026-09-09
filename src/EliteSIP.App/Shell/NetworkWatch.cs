using System.Net.NetworkInformation;
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
/// <b>Событий два, а лечение одно.</b> И пробуждение, и смена адреса означают
/// «связь надо поднять заново, а стук переиграть»; звать их обоих через одну
/// дверь дешевле, чем помнить, какое из них что чинит.
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
    private readonly DispatcherTimer _settling;

    private bool _disposed;

    internal NetworkWatch(Action onNetworkChanged, Action onResume, Action<string> log)
    {
        _onNetworkChanged = onNetworkChanged;
        _onResume = onResume;
        _log = log;

        _settling = new DispatcherTimer { Interval = Settle };
        _settling.Tick += (_, _) =>
        {
            _settling.Stop();
            _onNetworkChanged();
        };

        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
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
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        // Событие приходит не в потоке интерфейса, а таймер — его. Перезапуск
        // таймера и есть склейка очереди событий в одно.
        _settling.Dispatcher.BeginInvoke(() =>
        {
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
}
