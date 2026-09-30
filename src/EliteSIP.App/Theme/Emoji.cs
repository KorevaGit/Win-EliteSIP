using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace EliteSIP.App.Theme;

/// <summary>
/// <c>Emoji.Text</c> — вместо <c>Text</c> для подписей, где бывают смайлики.
/// </summary>
///
/// <remarks>
/// Строка без смайликов кладётся в <c>TextBlock.Text</c> как обычно: многоточие,
/// перенос и замеры ведут себя ровно как прежде. Со смайликами — набор <c>Run</c>
/// и картинок в строке, высота картинки идёт за кеглем блока (подпись клавиши
/// меняет его на ходу), так что смайлик растёт и ужимается вместе с текстом.
/// </remarks>
public static class Emoji
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(Emoji),
        new PropertyMetadata(defaultValue: null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>Готовый блок для мест, где подпись кладут в <c>Header</c>, а не в разметку.</summary>
    public static TextBlock Block(string text)
    {
        var block = new TextBlock();
        Fill(block, text);
        return block;
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs change)
    {
        if (sender is TextBlock block)
        {
            Fill(block, (string?)change.NewValue ?? string.Empty);
        }
    }

    private static void Fill(TextBlock block, string text)
    {
        if (!EmojiPack.MayContainEmoji(text) || EmojiPack.Shared is not { } pack)
        {
            block.Inlines.Clear();
            block.Text = text;
            return;
        }

        var pieces = pack.Split(text);
        if (pieces.All(piece => piece.Key is null))
        {
            block.Inlines.Clear();
            block.Text = text;
            return;
        }

        block.Inlines.Clear();
        foreach (var piece in pieces)
        {
            if (piece.Text is { } run)
            {
                block.Inlines.Add(new Run(run));
                continue;
            }

            if (pack.Image(piece.Key!) is not { } source)
            {
                continue;
            }

            var image = new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(1, 0, 1, 0),
            };

            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(TextBlock.FontSize))
            {
                Source = block,
                Converter = Scale,
            });

            block.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        }
    }

    private static readonly FontScale Scale = new();

    private sealed class FontScale : IValueConverter
    {
        // Смайлик чуть выше кегля: у шрифта «высота строки» больше кегля, и
        // равный ему смайлик выглядит мельче соседних букв.
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double size ? size * 1.25 : DependencyProperty.UnsetValue;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
