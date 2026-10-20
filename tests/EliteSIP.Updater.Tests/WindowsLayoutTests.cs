using EliteSIP.Updater;

namespace EliteSIP.Updater.Tests;

/// <summary>
/// Раскладка каталогов обновляльщика Windows.
///
/// Обновляльщик работает от SYSTEM, а общий каталог открыт оператору на запись.
/// Всякая запись или удаление от SYSTEM в общем каталоге — путь к правам
/// администратора (подмена каталога связкой). Эти проверки держат границу:
/// пишет обновляльщик только к себе, а отметку оператора лишь читает.
///
/// Собраны 20 октября 2026 из <c>SharedDirectoryTests</c> и
/// <c>RecoveryTests</c>: сами правила переехали в EliteSIP.Updates и
/// проверяются там, а пути — Windows и остаются здесь.
/// </summary>
public sealed class WindowsLayoutTests
{
    [Theory]
    [InlineData(nameof(Paths.Log))]
    [InlineData(nameof(Paths.RequestHandled))]
    [InlineData(nameof(Paths.RelaunchMarker))]
    [InlineData(nameof(Paths.TrustedUpdates))]
    public void ВсёЧтоПишетОбновляльщик_ЛежитВнеОбщегоКаталога(string name)
    {
        var path = name switch
        {
            nameof(Paths.Log) => Paths.Log,
            nameof(Paths.RequestHandled) => Paths.RequestHandled,
            nameof(Paths.RelaunchMarker) => Paths.RelaunchMarker,
            _ => Paths.TrustedUpdates,
        };

        Assert.False(string.IsNullOrEmpty(path));
        Assert.False(
            Path.GetFullPath(path).StartsWith(Path.GetFullPath(Paths.Shared), StringComparison.OrdinalIgnoreCase),
            $"{name} = {path} лежит в общем каталоге");
        Assert.StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory),
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Отметка_о_запуске_лежит_в_общем_каталоге_а_отсчёт_у_себя()
    {
        Assert.StartsWith(Paths.Shared, Paths.Started, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Paths.TrustedUpdates, Paths.InstalledSeen, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Paths.TrustedUpdates, Paths.RecoveryNoted, StringComparison.OrdinalIgnoreCase);
    }
}
