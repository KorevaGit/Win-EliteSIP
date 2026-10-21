using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

/// <summary>Смайлики в подписях (0.1.66): разбор строки и отрисовка.</summary>
public sealed class EmojiTests
{
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
            var image = EmojiImages.Get(pack, piece.Key!);
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
