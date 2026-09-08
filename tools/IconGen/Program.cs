using System.Globalization;
using System.Text;
using System.Xml.Linq;

// Собирает src/EliteSIP.App/Theme/Icons.xaml из комплекта иконок macOS-версии.
//
// Комплект переиспользуется как есть: он векторный и рисовался именно потому,
// что системных значков на Catalina не хватало. Перерисовывать его заново
// значило бы завести вторую правду о том, как выглядит приложение.
//
//     dotnet run --project tools/IconGen -- <каталог Symbols> <выходной .xaml>
//
// Каждая иконка становится ресурсом-`Canvas` с `x:Shared="False"`: один и тот
// же значок стоит в панели, в истории и в окне входящего одновременно, а
// элемент WPF не может висеть в двух местах дерева сразу. Цвет не зашивается —
// заливка и обводка привязаны к `Foreground` того `Icon`, внутрь которого
// значок поставлен.

if (args.Length != 2)
{
    Console.Error.WriteLine("нужны два довода: каталог Symbols и путь к .xaml");
    return 2;
}

var (sourceDirectory, outputPath) = (args[0], args[1]);
if (!Directory.Exists(sourceDirectory))
{
    Console.Error.WriteLine($"нет каталога {sourceDirectory}");
    return 2;
}

var culture = CultureInfo.InvariantCulture;
var generator = new IconGenerator(culture);

var files = Directory.GetFiles(sourceDirectory, "*.svg", SearchOption.AllDirectories)
    .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
    .ToArray();

var xaml = generator.Build(files);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
File.WriteAllText(outputPath, xaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"иконок собрано: {files.Length} -> {outputPath}");
return 0;

internal sealed class IconGenerator(CultureInfo culture)
{
    // Цвета в комплекте всюду `black` — SVG обязан назвать хоть какой-то.
    // Настоящий цвет значка задаёт то место, куда его поставили.
    private const string Ink =
        "{Binding Foreground, RelativeSource={RelativeSource AncestorType=theme:Icon}}";

    private readonly StringBuilder _output = new();

    public string Build(IReadOnlyList<string> files)
    {
        _output.Clear();
        _output.AppendLine("<!--");
        _output.AppendLine("    Собран стендом tools/IconGen. Руками не правится: правится");
        _output.AppendLine("    SVG в комплекте macOS-версии, а этот файл пересобирается.");
        _output.AppendLine("-->");
        _output.AppendLine("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
        _output.AppendLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
        _output.AppendLine("                    xmlns:theme=\"clr-namespace:EliteSIP.App.Theme\">");

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var root = XDocument.Load(file).Root
                ?? throw new InvalidDataException($"пустой {file}");

            _output.AppendLine();
            Line(2, $"<Canvas x:Key=\"{name}\" x:Shared=\"False\" Width=\"20\" Height=\"20\">");
            Emit(root, indent: 4);
            Line(2, "</Canvas>");
        }

        _output.AppendLine();
        _output.AppendLine("</ResourceDictionary>");
        return _output.ToString();
    }

    private void Emit(XElement parent, int indent)
    {
        foreach (var element in parent.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "g":
                    // У группы своего вида нет — она только двигает и мельчит
                    // то, что внутри. В XAML это Canvas с преобразованием.
                    Line(indent, "<Canvas>");
                    Line(indent + 2, "<Canvas.RenderTransform>");
                    Line(indent + 4, "<TransformGroup>");
                    foreach (var transform in Transforms(element))
                    {
                        Line(indent + 6, transform);
                    }

                    Line(indent + 4, "</TransformGroup>");
                    Line(indent + 2, "</Canvas.RenderTransform>");
                    Emit(element, indent + 2);
                    Line(indent, "</Canvas>");
                    break;

                case "path":
                    Line(indent, $"<Path Data=\"{Data(element)}\"{Paint(element)} />");
                    break;

                case "circle":
                    Line(indent, $"<Path Data=\"{Circle(element)}\"{Paint(element)} />");
                    break;

                case "rect":
                    Line(indent, $"<Path Data=\"{Rect(element)}\"{Paint(element)} />");
                    break;

                default:
                    throw new InvalidDataException($"неизвестный элемент {element.Name.LocalName}");
            }
        }
    }

    private static string Data(XElement element)
    {
        // Правило заливки в SVG — атрибут, в XAML — буква в начале разметки:
        // F0 «чётно-нечётное», F1 «ненулевое», как по умолчанию в SVG.
        var rule = Attribute(element, "fill-rule") == "evenodd" ? "F0 " : string.Empty;
        return rule + Attribute(element, "d");
    }

    private string Circle(XElement element)
    {
        var (cx, cy, r) = (Number(element, "cx"), Number(element, "cy"), Number(element, "r"));
        var (left, right) = (Round(cx - r), Round(cx + r));

        // Двумя полудугами: одной дугой замкнутая окружность в разметке пути не
        // задаётся — начало и конец совпали бы, и дуга выродилась бы в точку.
        return Format($"M {left},{cy} A {r},{r} 0 1 0 {right},{cy} A {r},{r} 0 1 0 {left},{cy} Z");
    }

    private string Rect(XElement element)
    {
        var (x, y) = (Number(element, "x"), Number(element, "y"));
        var (width, height) = (Number(element, "width"), Number(element, "height"));
        var (right, bottom) = (Round(x + width), Round(y + height));
        var radius = Number(element, "rx");

        if (radius <= 0)
        {
            return Format($"M {x},{y} H {right} V {bottom} H {x} Z");
        }

        return Format($"M {Round(x + radius)},{y} H {Round(right - radius)} A {radius},{radius} 0 0 1 {right},{Round(y + radius)}")
            + Format($" V {Round(bottom - radius)} A {radius},{radius} 0 0 1 {Round(right - radius)},{bottom}")
            + Format($" H {Round(x + radius)} A {radius},{radius} 0 0 1 {x},{Round(bottom - radius)}")
            + Format($" V {Round(y + radius)} A {radius},{radius} 0 0 1 {Round(x + radius)},{y} Z");
    }

    private string Paint(XElement element)
    {
        var paint = new StringBuilder();

        if (Attribute(element, "fill") is not "none")
        {
            paint.Append(culture, $" Fill=\"{Ink}\"");
        }

        if (Attribute(element, "stroke") is not (null or "none"))
        {
            paint.Append(culture, $" Stroke=\"{Ink}\"");
            paint.Append(culture, $" StrokeThickness=\"{Attribute(element, "stroke-width") ?? "1"}\"");

            // Скруглённые торцы и стыки — не украшение: у значков вроде
            // «микрофон выключен» линия пересекает фигуру, и квадратный торец
            // на двенадцати точках читается как обрыв.
            if (Attribute(element, "stroke-linecap") is { } cap)
            {
                paint.Append(culture, $" StrokeStartLineCap=\"{Capitalize(cap)}\" StrokeEndLineCap=\"{Capitalize(cap)}\"");
            }

            if (Attribute(element, "stroke-linejoin") is { } join)
            {
                paint.Append(culture, $" StrokeLineJoin=\"{Capitalize(join)}\"");
            }
        }

        return paint.ToString();
    }

    private List<string> Transforms(XElement element)
    {
        var parts = new List<string>();
        var source = Attribute(element, "transform") ?? string.Empty;

        foreach (var piece in source.Split(')', StringSplitOptions.RemoveEmptyEntries))
        {
            var open = piece.IndexOf('(', StringComparison.Ordinal);
            if (open < 0)
            {
                continue;
            }

            var kind = piece[..open].Trim();
            var numbers = piece[(open + 1)..]
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => double.Parse(value, culture))
                .ToArray();

            parts.Add(kind switch
            {
                "translate" => Format($"<TranslateTransform X=\"{numbers[0]}\" Y=\"{At(numbers, 1, 0)}\" />"),
                "scale" => Format($"<ScaleTransform ScaleX=\"{numbers[0]}\" ScaleY=\"{At(numbers, 1, numbers[0])}\" />"),
                "rotate" => Format($"<RotateTransform Angle=\"{numbers[0]}\" CenterX=\"{At(numbers, 1, 0)}\" CenterY=\"{At(numbers, 2, 0)}\" />"),
                _ => throw new InvalidDataException($"неизвестное преобразование {kind}"),
            });
        }

        // В SVG преобразования применяются справа налево, в `TransformGroup` —
        // слева направо. Без переворота вложенный телефон уезжает за край.
        parts.Reverse();
        return parts;
    }

    private static double At(double[] numbers, int index, double fallback)
        => index < numbers.Length ? numbers[index] : fallback;

    private static string? Attribute(XElement element, string name) => element.Attribute(name)?.Value;

    private double Number(XElement element, string name)
        => Attribute(element, name) is { } value ? double.Parse(value, culture) : 0;

    private static string Capitalize(string value)
        => char.ToUpperInvariant(value[0]) + value[1..];

    // Округление — не косметика: без него разность вроде «10 минус 8.3» уезжает
    // в 1.6999999999999993, и разметка пути читается как случайный набор цифр.
    private string Format(FormattableString value) => value.ToString(culture);

    private static double Round(double value) => Math.Round(value, 4);

    private void Line(int indent, string text)
        => _output.Append(' ', indent).AppendLine(text);
}
