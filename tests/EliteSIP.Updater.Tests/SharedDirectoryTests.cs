using EliteSIP.Updater;

namespace EliteSIP.Updater.Tests;

/// <summary>
/// Обновляльщик работает от SYSTEM, а общий каталог открыт оператору на запись.
/// Всякая запись или удаление от SYSTEM в общем каталоге — путь к правам
/// администратора (подмена каталога связкой). Эти проверки держат границу:
/// пишет обновляльщик только к себе, а отметку оператора лишь читает.
/// </summary>
public sealed class SharedDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EliteSIP.Updater.Tests", Guid.NewGuid().ToString("N"));

    public SharedDirectoryTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private string RequestPath => Path.Combine(_root, "update-requested");

    private string HandledPath => Path.Combine(_root, "trusted", "request-handled");

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
    public void ОтметкиНет_СогласияНет()
    {
        Assert.Null(Request.Read(RequestPath));
    }

    [Fact]
    public void ОтметкаЧитаетсяВерсией()
    {
        File.WriteAllText(RequestPath, "0.1.67");

        Assert.Equal(new Version(0, 1, 67), Request.Read(RequestPath)?.Version);
    }

    [Fact]
    public void ОбработаннаяОтметка_ВтороеРазНеСрабатывает_ИНеУдаляется()
    {
        File.WriteAllText(RequestPath, "0.1.67");
        var consent = Request.Read(RequestPath)!.Value;

        Assert.False(Request.IsHandled(consent, HandledPath));

        Request.MarkHandled(consent, HandledPath);

        Assert.True(Request.IsHandled(Request.Read(RequestPath)!.Value, HandledPath));
        Assert.True(File.Exists(RequestPath));
    }

    [Fact]
    public void ПереписаннаяОтметкаСТойЖеВерсией_Снова_Необработана()
    {
        File.WriteAllText(RequestPath, "0.1.67");
        Request.MarkHandled(Request.Read(RequestPath)!.Value, HandledPath);

        // Приложение оставляет согласие заново — после сорвавшейся установки.
        File.WriteAllText(RequestPath, "0.1.67");
        File.SetLastWriteTimeUtc(RequestPath, DateTime.UtcNow.AddMinutes(1));

        Assert.False(Request.IsHandled(Request.Read(RequestPath)!.Value, HandledPath));
    }

    [Fact]
    public void ОтметкаНаДругуюВерсию_Необработана()
    {
        File.WriteAllText(RequestPath, "0.1.67");
        var first = Request.Read(RequestPath)!.Value;
        Request.MarkHandled(first, HandledPath);

        var other = first with { Version = new Version(0, 1, 68) };

        Assert.False(Request.IsHandled(other, HandledPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("не версия")]
    public void БитаяОтметка_Игнорируется(string content)
    {
        File.WriteAllText(RequestPath, content);

        Assert.Null(Request.Read(RequestPath));
    }

    [Fact]
    public void ДлиннаяОтметка_НеЧитаетсяЦеликом_ИИгнорируется()
    {
        File.WriteAllText(RequestPath, "0.1.67" + new string(' ', 4096));

        Assert.Null(Request.Read(RequestPath));
    }
}
