using EliteSIP.Updater;

namespace EliteSIP.Updates.Tests;

/// <summary>
/// Отметка о согласии оператора: обновляльщик её только читает, а обработанную
/// запоминает у себя.
///
/// До 20 октября 2026 — часть <c>SharedDirectoryTests</c>. Правило общее для
/// Windows и Linux и проверяется здесь; то, что всё записываемое лежит вне
/// общего каталога, зависит от раскладки ОС и проверяется у обновляльщика
/// (<c>WindowsLayoutTests</c>).
/// </summary>
public sealed class ConsentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EliteSIP.Updater.Tests", Guid.NewGuid().ToString("N"));

    public ConsentTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private string RequestPath => Path.Combine(_root, "update-requested");

    private string HandledPath => Path.Combine(_root, "trusted", "request-handled");

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
