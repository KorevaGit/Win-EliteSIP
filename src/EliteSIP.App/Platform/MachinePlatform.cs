using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EliteSIP.App.Platform;

/// <summary>
/// Какая система на машине — для Spark: семейство и название под версией
/// клиента в списке сотрудников. Уходит заголовками
/// <c>X-EliteSIP-Platform</c> и <c>X-EliteSIP-OS</c> с каждым запросом канала.
/// </summary>
internal static class MachinePlatform
{
    /// <summary><c>linux</c>, <c>windows</c> или <c>macos</c> — как ждёт сервер обновлений.</summary>
    internal static string Family { get; } =
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Название системы: «Linux Mint 22.3»; не нашлось — описание среды .NET.</summary>
    internal static string Name { get; } = HeaderSafe(
        (OperatingSystem.IsWindows() ? WindowsName() : OperatingSystem.IsLinux() ? PrettyName(ReadOsRelease()) : null)
        ?? RuntimeInformation.OSDescription);

    /// <summary>«Windows 11 Pro 24H2»: имя выпуска, версия и сборка из реестра.</summary>
    ///
    /// <remarks>
    /// <c>ProductName</c> у Windows 11 по-прежнему говорит «Windows 10»: различает
    /// их только номер сборки, с 22000 — одиннадцатая.
    /// </remarks>
    internal static string? WindowsName(string? productName, string? displayVersion, string? build)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        var name = productName.Trim();
        if (int.TryParse(build, out var number) && number >= 22000 && name.StartsWith("Windows 10", StringComparison.Ordinal))
        {
            name = "Windows 11" + name["Windows 10".Length..];
        }

        return string.IsNullOrWhiteSpace(displayVersion) ? name : $"{name} {displayVersion.Trim()}";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? WindowsName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return WindowsName(key?.GetValue("ProductName") as string, key?.GetValue("DisplayVersion") as string,
                key?.GetValue("CurrentBuildNumber") as string);
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>PRETTY_NAME</c> из содержимого <c>/etc/os-release</c>.</summary>
    internal static string? PrettyName(string? osRelease)
    {
        foreach (var line in (osRelease ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                var value = line["PRETTY_NAME=".Length..].Trim().Trim('"', '\'').Trim();
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Печатный ASCII до 64 знаков: значение уходит заголовком HTTP, и
    /// кириллица в нём (названия сборок бывают и такими) не дошла бы.
    /// </summary>
    internal static string HeaderSafe(string text)
    {
        StringBuilder safe = new(Math.Min(text.Length, 64));
        foreach (var symbol in text)
        {
            if (safe.Length == 64)
            {
                break;
            }

            if (symbol is >= ' ' and <= '~')
            {
                safe.Append(symbol);
            }
        }

        return safe.ToString().Trim();
    }

    private static string? ReadOsRelease()
    {
        foreach (var path in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}
