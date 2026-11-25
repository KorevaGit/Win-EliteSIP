using System.IO;
using System.Net.NetworkInformation;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;
using EliteSIP.Audio;

namespace EliteSIP.App.Shell;

/// <summary>Снимки сети и звука работают независимо от UI и друг от друга.</summary>
internal sealed class RuntimeDiagnostics : IDisposable
{
    internal const string NetworkFile = "network-state.txt";
    internal const string AudioFile = "audio-state.txt";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int MaximumSnapshotCharacters = 2048;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _writeGate = new();

    internal RuntimeDiagnostics(AppSettings settings, Action<string> log)
    {
        var directory = Path.GetDirectoryName(AppSettings.DefaultPath)!;
        var token = _stop.Token;
        _ = Task.Run(() => ObserveAsync(Path.Combine(directory, NetworkFile), "Сеть", CaptureNetwork, log, token));
        _ = Task.Run(() => ObserveAsync(Path.Combine(directory, AudioFile), "Звук", () => CaptureAudio(settings), log, token));
    }

    private async Task ObserveAsync(string path, string label, Func<string> capture, Action<string> log, CancellationToken token)
    {
        string? previous = null;
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: <= MaximumSnapshotCharacters * 4 })
            {
                previous = File.ReadAllText(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log($"диагностика {label}: прежний снимок недоступен ({error.GetType().Name})");
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                string current;
                try { current = capture(); }
                catch (NetworkInformationException error)
                {
                    log($"диагностика {label}: снимок недоступен ({error.GetType().Name})");
                    continue;
                }

                if (current.Length > MaximumSnapshotCharacters) { current = current[..MaximumSnapshotCharacters]; }
                if (current == previous) { continue; }

                lock (_writeGate)
                {
                    if (token.IsCancellationRequested) { return; }
                    try
                    {
                        File.WriteAllText(path + ".tmp", current);
                        File.Move(path + ".tmp", path, overwrite: true);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        log($"диагностика {label}: снимок не сохранён ({error.GetType().Name})");
                    }

                    if (previous is not null)
                    {
                        var message = $"{label}: было [{previous}], стало [{current}]";
                        log(message);
                        ClientEventLog.Shared.Record(ClientEventLog.Kind.Config, ClientEventLog.Level.Info, message);
                    }
                    previous = current;
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static string CaptureNetwork() => string.Join("; ", NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(adapter =>
        {
            var properties = adapter.GetIPProperties();
            var addresses = properties.UnicastAddresses.Select(address => address.Address.ToString()).Order(StringComparer.Ordinal);
            var gateways = properties.GatewayAddresses.Select(gateway => gateway.Address.ToString()).Order(StringComparer.Ordinal);
            return $"{adapter.Name}: IP {string.Join(", ", addresses)}, шлюзы {string.Join(", ", gateways)}";
        }).Order(StringComparer.Ordinal));

    private static string CaptureAudio(AppSettings settings)
    {
        string Describe(AudioDeviceDirection direction, string? selected)
        {
            var devices = AudioDeviceCatalog.Devices(direction);
            var system = AudioDeviceCatalog.Default(direction);
            string chosen = string.IsNullOrEmpty(selected)
                ? "системное"
                : devices.FirstOrDefault(device => device.Id == selected)?.Name ?? "недоступно";
            return $"по умолчанию {system?.Name ?? "нет"}, выбрано {chosen}, устройства "
                + string.Join(", ", devices.Select(device => device.Name).Order(StringComparer.Ordinal));
        }

        return $"Микрофон: {Describe(AudioDeviceDirection.Capture, settings.Audio.InputDeviceId)}; "
            + $"выход: {Describe(AudioDeviceDirection.Render, settings.Audio.OutputDeviceId)}";
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
