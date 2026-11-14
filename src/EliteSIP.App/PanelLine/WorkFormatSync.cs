using System.Net;
using System.Net.Http;
using EliteSIP.App.Settings;

namespace EliteSIP.App.PanelLine;

/// <summary>Последовательная отправка последнего выбора. Вызывается потоком интерфейса.</summary>
internal sealed class WorkFormatSync : IDisposable
{
    internal sealed record Request(string InstallationID, string Key, string Format);
    private readonly PanelSettings _panel;
    private readonly Func<Request, CancellationToken, Task<HttpStatusCode>> _send;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private Task? _running;
    private bool _disposed;

    internal WorkFormatSync(PanelSettings panel, Func<Request, CancellationToken, Task<HttpStatusCode>> send, Action<string> log)
    {
        _panel = panel;
        _send = send;
        _log = log;
        _token = _lifetime.Token;
    }

    internal Task SendAsync()
    {
        if (_disposed) { return Task.CompletedTask; }
        if (_running is { IsCompleted: false }) { return _running; }
        return _running = DrainAsync();
    }

    private async Task DrainAsync()
    {
        while (!_disposed && _panel.IsManaged && _panel.LockedSite is null
               && _panel.PendingWorkFormat is "office" or "remote" && _panel.ChannelKey() is { } key)
        {
            var request = new Request(_panel.InstallationID, key, _panel.PendingWorkFormat);
            HttpStatusCode status;
            try { status = await _send(request, _token).ConfigureAwait(true); }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            {
                if (!_disposed) { _log("формат работы: отправка в Spark отложена до восстановления связи"); }
                return;
            }
            // Ответ старой привязки не меняет новую привязку либо ручной режим.
            if (_disposed || !_panel.IsManaged || _panel.InstallationID != request.InstallationID || _panel.ChannelKey() != request.Key)
            {
                return;
            }
            if (((int)status is >= 200 and <= 299) || status == HttpStatusCode.Conflict)
            {
                if (_panel.PendingWorkFormat == request.Format) { _panel.PendingWorkFormat = string.Empty; }
                if (status == HttpStatusCode.Conflict)
                {
                    _log("формат работы закреплён в Spark; перечитываем конфигурацию");
                    return;
                }
                // Пока ждали ответ, выбор мог измениться: сразу отправляем последнее значение.
                continue;
            }
            _log($"формат работы: Spark ответил {(int)status}; повтор при следующей синхронизации");
            return;
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
