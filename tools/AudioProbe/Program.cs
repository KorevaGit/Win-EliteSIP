using System.Globalization;

namespace AudioProbe;

/// <summary>
/// Стенд аудиотракта. Этап W0 плана переноса.
///
/// Консоль, а не окно, и отдельный проект, а не тест: то, что здесь
/// проверяется, требует живого устройства, живых ушей и получаса работы
/// подряд. Модульным тестом это не ловится, а в приложении — тонет.
///
/// В оригинале ту же роль играл <c>Tools/audioprobe</c>, и по той же причине.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Культура вывода фиксируется: числа замеров читает человек и сравнивает
        // с прошлым прогоном, а разделитель дробной части, зависящий от системы,
        // ломает и глазами, и grep-ом.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0] switch
        {
            "devices" => DeviceList.Run(),
            "capture" => CaptureProbe.Run(ParseSeconds(args, 10)),
            _ => Unknown(args[0]),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Неизвестная команда: {command}");
        PrintUsage();
        return 1;
    }

    private static int ParseSeconds(string[] args, int fallback)
    {
        if (args.Length < 2)
        {
            return fallback;
        }

        return int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Стенд аудиотракта EliteSIP (этап W0).

              AudioProbe devices            устройства, форматы, периоды
              AudioProbe capture [секунд]   замер такта захвата. Звука не издаёт

            """);
    }
}
