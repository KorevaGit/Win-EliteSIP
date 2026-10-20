using EliteSIP.App.History;
using EliteSIP.CallHistory;

namespace EliteSIP.App.Tests;

/// <summary>
/// Конвертеры строк истории в разметке WPF.
///
/// Вынесено из <c>MobileNumberMaskTests</c> 20 октября 2026: правило маски —
/// общее и проверяется в <c>EliteSIP.App.Core.Tests</c>, а то, как привязка
/// WPF доносит до него значения, — только здесь.
/// </summary>
public sealed class HistoryConverterTests
{
    private const string Own = "176";
    private const string Mobile = "89181234567";

    [Fact]
    public void Строка_истории_без_флага_прячет()
    {
        // Привязка, не донёсшая третье значение, не должна открыть номер.
        CallRecord record = new() { CallId = "in", Direction = CallDirection.Incoming, Number = Mobile };

        var title = new CallTitleConverter().Convert([record, Own], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("+7**********", title);
    }
}
