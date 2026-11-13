using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using EliteSIP.Diagnostics;

namespace EliteSIP.App.Shell;

/// <summary>Системные сообщения о сбое клиента, в том числе когда crash.log записать не удалось.</summary>
internal static class SupportCrashEvidence
{
    internal static bool IsClientEvent(string xml) =>
        xml.Contains("EliteSIP.App.exe", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("EliteSIP.exe", StringComparison.OrdinalIgnoreCase);

    internal static string Capture()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Windows Application: аварии EliteSIP за 30 суток; собрано {DateTimeOffset.Now:O}");
        const int scanLimit = 2000;
        var included = 0;
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                "*[System[(EventID=1000 or EventID=1001 or EventID=1026) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]")
            {
                ReverseDirection = true, TolerateQueryErrors = false,
            };
            using var reader = new EventLogReader(query);
            for (var scanned = 0; scanned < scanLimit; scanned++)
            {
                using var record = reader.ReadEvent(TimeSpan.FromMilliseconds(500));
                if (record is null) { break; }
                var xml = record.ToXml();
                if (IsClientEvent(xml))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"\n[{record.TimeCreated:O}] {record.ProviderName}; EventID={record.Id}; RecordID={record.RecordId}");
                    text.AppendLine(LogRedaction.Redact(xml));
                    included++;
                }
                if (scanned == scanLimit - 1) { text.AppendLine("Достигнут предел просмотра 2000 событий; более ранние события не проверялись."); }
            }
            text.AppendLine(CultureInfo.InvariantCulture, $"\nНайдено событий клиента: {included}.");
        }
        catch (Exception error) when (error is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Системный журнал недоступен: {error.GetType().Name}: {error.Message}");
        }
        return text.ToString();
    }
}
