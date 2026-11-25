using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using EliteSIP.App.Settings;
using EliteSIP.Audio;
using EliteSIP.Diagnostics;
using Microsoft.Win32;

namespace EliteSIP.App.Shell;

/// <summary>Разрешённые диагностические поля; файл настроек и учётные секреты не копируются.</summary>
internal static class SupportSnapshot
{
    internal static string SavedRuntimeState(string name) =>
        ReadLogTail(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, name));

    internal static string InstallerLogs()
    {
        try
        {
            var directory = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "updates"));
            return string.Join(Environment.NewLine, directory.EnumerateFiles("install-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(3)
                .Select(file => $"[{file.Name}]{Environment.NewLine}{ReadLogTail(file.FullName)}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"Журналы установщика недоступны: {error.GetType().Name}";
        }
    }

    internal static string UpdaterLog() =>
        ReadLogTail(Path.Combine(AppContext.BaseDirectory, "updates", "updater.log"));

    private static string ReadLogTail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int limit = 256 * 1024;
            var length = stream.Length;
            var buffer = new byte[(int)Math.Min(length, limit)];
            stream.Position = length - buffer.Length;
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            int offset = length > buffer.Length ? Array.IndexOf(buffer, (byte)'\n', 0, read) + 1 : 0;
            if (length > buffer.Length && offset == 0) { return string.Empty; }
            return LogRedaction.Redact(Encoding.UTF8.GetString(buffer, offset, read - offset));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"Журнал обновляльщика недоступен: {error.GetType().Name}";
        }
    }

    internal static string Capture(AppSettings settings, bool includeAudioInventory = true)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Снимок: {DateTimeOffset.Now:O}; часовой пояс: {TimeZoneInfo.Local.Id}");
        Section(text, "ПК", () =>
        {
            using var process = Process.GetCurrentProcess();
            text.AppendLine(CultureInfo.InvariantCulture, $"имя: {Environment.MachineName}; ОС: {RuntimeInformation.OSDescription}; версия: {Environment.OSVersion.Version}");
            text.AppendLine(CultureInfo.InvariantCulture, $"архитектура ОС: {RuntimeInformation.OSArchitecture}; процесса: {RuntimeInformation.ProcessArchitecture}; {RuntimeInformation.FrameworkDescription}");
            text.AppendLine(CultureInfo.InvariantCulture, $"производитель: {ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer")}; модель: {ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName")}");
            text.AppendLine(CultureInfo.InvariantCulture, $"CPU: {ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")}; логических процессоров: {Environment.ProcessorCount}");
            if (GetPhysicallyInstalledSystemMemory(out var ram)) { text.AppendLine(CultureInfo.InvariantCulture, $"RAM: {ram / 1024} МБ"); }
            text.AppendLine(CultureInfo.InvariantCulture, $"ОС работает: {TimeSpan.FromMilliseconds(Environment.TickCount64)}; процесс запущен: {process.StartTime:O}; PID: {process.Id}; память процесса: {process.WorkingSet64 / 1024 / 1024} МБ");
            text.AppendLine(CultureInfo.InvariantCulture, $"программа: {Environment.ProcessPath}; настройки: {AppSettings.DefaultPath}");
        });
        text.Append(Network());
        Section(text, "Экземпляры EliteSIP", () =>
        {
            foreach (var instance in Process.GetProcessesByName("EliteSIP.App"))
            {
                using (instance)
                {
                    try
                    {
                        text.AppendLine(CultureInfo.InvariantCulture,
                            $"PID={instance.Id}; session={instance.SessionId}; start={instance.StartTime:O}; RAM={instance.WorkingSet64 / 1048576} МБ");
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        text.AppendLine(CultureInfo.InvariantCulture, $"PID={instance.Id}: {error.GetType().Name}");
                    }
                }
            }
        });
        Section(text, "Конфигурация подключения", () =>
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"площадка: {settings.Account.Site}; номер: {settings.Account.Username}; профиль: {settings.Account.ProfileId}");
            text.AppendLine(CultureInfo.InvariantCulture, $"АТС учётки: {settings.Account.Domain}; офис: {settings.Pbx.OfficeAddress}; удалённо: {settings.Pbx.RemoteAddress}");
            text.AppendLine(CultureInfo.InvariantCulture, $"SIP: {settings.Pbx.Transport}:{settings.Pbx.Port}; срок регистрации: {settings.Pbx.RegistrationExpirySeconds} с; TLS AcceptAny: {settings.Pbx.AcceptsAnyTlsCertificate}; пиннинг задан: {settings.Pbx.PinnedCertificateFingerprint.Length > 0}");
            text.AppendLine(CultureInfo.InvariantCulture, $"стук: шагов {settings.PortKnock.Steps.Count}; пауза {settings.PortKnock.SpacingSeconds} с; повтор {settings.PortKnock.RepeatIntervalSeconds} с");
            foreach (var step in settings.PortKnock.Steps) { text.AppendLine(CultureInfo.InvariantCulture, $"  ICMP: адрес={(step.Host.Length > 0 ? step.Host : "адрес АТС")}; payload={step.PayloadBytes}; count={step.Count}"); }
            text.AppendLine(CultureInfo.InvariantCulture, $"журнал: {settings.Maintenance.LogToFile}; SIP-трасса: {settings.Maintenance.LogsSipTrace}");
        });
        Section(text, "Звук", () =>
        {
            var audio = settings.Audio;
            text.AppendLine(CultureInfo.InvariantCulture, $"вход: {audio.InputDeviceName ?? "системный"}; ID={audio.InputDeviceId ?? "default"}");
            text.AppendLine(CultureInfo.InvariantCulture, $"выход: {audio.OutputDeviceName ?? "системный"}; ID={audio.OutputDeviceId ?? "default"}");
            text.AppendLine(CultureInfo.InvariantCulture, $"микрофон: gain={audio.MicrophoneGain}, mute={audio.MicrophoneMuted}; громкость={audio.PlaybackVolume}; AGC={audio.AutomaticGainControl}; receiveAGC={audio.ReceiveGainControl}");
            text.AppendLine(CultureInfo.InvariantCulture, $"noise={audio.NoiseSuppression}; voices={audio.BackgroundVoiceSuppression}; Windows={audio.WindowsProcessing}; освобождение в простое=автоматически; мелодия={settings.Ringtone.Output}");
            if (!includeAudioInventory) { return; }
            foreach (var direction in new[] { AudioDeviceDirection.Capture, AudioDeviceDirection.Render })
            {
                var defaultDevice = AudioDeviceCatalog.Default(direction);
                text.AppendLine(CultureInfo.InvariantCulture, $"системное устройство {direction}: {defaultDevice?.Name}; ID={defaultDevice?.Id}");
                foreach (var device in AudioDeviceCatalog.Devices(direction, includeInactive: true))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {direction}: {device.Name}; ID={device.Id}; {device.Availability}; {device.Transport}/{device.TransportName}; {device.SampleRate} Гц/{device.Channels} каналов; формат активен={device.FormatIsLive}");
                }
            }
        });
        return text.ToString();
    }

    internal static string Network()
    {
        var text = new StringBuilder();
        Section(text, "Сеть", () =>
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"сеть доступна: {NetworkInterface.GetIsNetworkAvailable()}; внешний IP см. в журнале SIP received/rport (адрес, наблюдаемый АТС)");
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                Section(text, $"адаптер {adapter.Name}", () =>
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {adapter.Description}; ID={adapter.Id}; тип={adapter.NetworkInterfaceType}; состояние={adapter.OperationalStatus}; скорость={adapter.Speed}; MAC={adapter.GetPhysicalAddress()}");
                    var properties = adapter.GetIPProperties();
                    foreach (var ip in properties.UnicastAddresses) { text.AppendLine(CultureInfo.InvariantCulture, $"  IP={ip.Address}/{ip.PrefixLength}; origin={ip.PrefixOrigin}; DAD={ip.DuplicateAddressDetectionState}"); }
                    text.AppendLine(CultureInfo.InvariantCulture, $"  шлюзы: {string.Join(", ", properties.GatewayAddresses.Select(value => value.Address))}");
                    text.AppendLine(CultureInfo.InvariantCulture, $"  DNS: {string.Join(", ", properties.DnsAddresses)}; suffix={properties.DnsSuffix}; DHCP: {string.Join(", ", properties.DhcpServerAddresses)}");
                    var stats = adapter.GetIPStatistics();
                    text.AppendLine(CultureInfo.InvariantCulture, $"  байты получено/отправлено: {stats.BytesReceived}/{stats.BytesSent}; ошибки вход/выход: {stats.IncomingPacketsWithErrors}/{stats.OutgoingPacketsWithErrors}; отброшено: {stats.IncomingPacketsDiscarded}/{stats.OutgoingPacketsDiscarded}");
                });
            }
        });
        return text.ToString();
    }

    private static void Section(StringBuilder text, string name, Action capture)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"[{name}]");
        try { capture(); }
        catch (Exception error) when (error is SystemException) { text.AppendLine(CultureInfo.InvariantCulture, $"недоступно: {error.GetType().Name}: {error.Message}"); }
    }

    private static string ReadRegistry(string path, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return Convert.ToString(key?.GetValue(name), CultureInfo.InvariantCulture) ?? "—";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
}
