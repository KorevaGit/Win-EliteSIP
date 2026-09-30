using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

/// <summary>Смайлики в подписях (0.1.66): разбор строки и отрисовка.</summary>
public sealed class EmojiTests
{
    private static readonly string[] Names =
    [
        "1f600.png", "2764.png", "2b50.png", "1f44d.png", "1f44d_1f3fb.png", "00a9.png",
        "0023_fe0f_20e3.png", "1f1f7_1f1fa.png", "1f469_200d_1f4bb.png",
    ];

    private static EmojiPack Pack() => new(Names, _ => null);

    private static string Show(EmojiPack pack, string text)
        => string.Join("|", pack.Split(text).Select(piece => piece.Text ?? $"<{piece.Key}>"));

    [Fact]
    public void Обычный_текст_остаётся_текстом()
        => Assert.Equal("Юрист 24/7", Show(Pack(), "Юрист 24/7"));

    [Fact]
    public void Смайлик_среди_слов()
        => Assert.Equal("Офис |<1f600.png>| привет", Show(Pack(), "Офис \U0001F600 привет"));

    [Theory]
    [InlineData("❤", "<2764.png>")]
    [InlineData("❤️", "<2764.png>")]
    [InlineData("⭐", "<2b50.png>")]
    public void Селектор_вариантов_не_обязателен(string text, string expected)
        => Assert.Equal(expected, Show(Pack(), text));

    [Fact]
    public void Самая_длинная_последовательность_выигрывает()
    {
        Assert.Equal("<1f44d_1f3fb.png>", Show(Pack(), "\U0001F44D\U0001F3FB"));
        Assert.Equal("<1f44d.png>|x", Show(Pack(), "\U0001F44Dx"));
        Assert.Equal("<1f469_200d_1f4bb.png>", Show(Pack(), "\U0001F469‍\U0001F4BB"));
        Assert.Equal("<1f1f7_1f1fa.png>", Show(Pack(), "\U0001F1F7\U0001F1FA"));
    }

    [Fact]
    public void Знак_авторского_права_без_селектора_остаётся_текстом()
    {
        Assert.Equal("© 2026", Show(Pack(), "© 2026"));
        Assert.Equal("<00a9.png>", Show(Pack(), "©️"));
    }

    [Fact]
    public void Кнопка_с_цифрой_целиком()
        => Assert.Equal("<0023_fe0f_20e3.png>", Show(Pack(), "#️⃣"));

    [Fact]
    public void Быстрая_проверка_не_трогает_обычные_подписи()
    {
        Assert.False(EmojiPack.MayContainEmoji("Тестикович 176"));
        Assert.False(EmojiPack.MayContainEmoji(null));
        Assert.True(EmojiPack.MayContainEmoji("Огонь \U0001F525"));
    }

    [Fact]
    public void Пакет_из_выпуска_читается_и_знает_ходовые_смайлики() => WpfHost.Run(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "emoji.pak");
        Assert.True(File.Exists(path), "emoji.pak не попал в выход сборки");

        var pack = EmojiPack.Shared;
        Assert.NotNull(pack);

        var pieces = pack.Split("Привет \U0001F44B \U0001F525 ❤️ ✅ \U0001F1F7\U0001F1FA");
        Assert.Equal(5, pieces.Count(piece => piece.Key is not null));

        foreach (var piece in pieces.Where(piece => piece.Key is not null))
        {
            var image = pack.Image(piece.Key!);
            Assert.NotNull(image);
            Assert.Equal(64, image.PixelWidth);
        }
    });

    [Fact]
    public void Подпись_со_смайликом_становится_строкой_с_картинкой() => WpfHost.Run(() =>
    {
        var block = new TextBlock { FontSize = 16 };
        Emoji.SetText(block, "Акции \U0001F525");

        Assert.Contains(block.Inlines, inline => inline is InlineUIContainer);
        Assert.Contains(block.Inlines, inline => inline is Run { Text: "Акции " });

        block.Measure(new System.Windows.Size(300, 100));
        Assert.True(block.DesiredSize.Height >= 16 * 1.25, $"высота {block.DesiredSize.Height:0.#}");

        Emoji.SetText(block, "Акции");
        Assert.Equal("Акции", block.Text);
        Assert.DoesNotContain(block.Inlines, inline => inline is InlineUIContainer);
    });
}
