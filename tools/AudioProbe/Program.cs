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

        try
        {
            return args[0] switch
            {
                "devices" => DeviceList.Run(),
                "capture" => CaptureProbe.Run(Positional(args, 10)),
                "loop" => LoopProbe.Run(
                    Option(args, "--in"),
                    Option(args, "--out"),
                    Text(args, "--in-name"),
                    Text(args, "--out-name"),
                    Positional(args, 20),
                    Flag(args, "--mute"),
                    Option(args, "--prime") ?? 20),
                "aec" => AecProbe.Run(
                    Text(args, "--in-name"),
                    Text(args, "--out-name"),
                    Positional(args, 30),
                    Option(args, "--delay") ?? 60),
                _ => Unknown(args[0]),
            };
        }
        catch (ArgumentOutOfRangeException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Неизвестная команда: {command}");
        PrintUsage();
        return 1;
    }

    /// <summary>Первое число среди аргументов — длительность прогона.</summary>
    private static int Positional(string[] args, int fallback)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && !args[i - 1].StartsWith("--", StringComparison.Ordinal))
            {
                return value;
            }
        }

        return fallback;
    }

    private static int? Option(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == name
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Значение строкового ключа. Нужен, потому что номер устройства оказался
    /// ненадёжен: порядок перечисления менялся между запусками подряд.
    /// </summary>
    private static string? Text(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool Flag(string[] args, string name) => Array.IndexOf(args, name) > 0;

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Стенд аудиотракта EliteSIP (этап W0).

              AudioProbe devices
                  устройства с номерами, форматы, периоды

              AudioProbe capture [секунд]
                  замер такта захвата. Звука не издаёт

              AudioProbe loop [--in N | --in-name Кусок] [--out N | --out-name Кусок]
                              [--mute] [секунд]
                  дуплексная петля: расхождение часов, задержка, срывы.
                  Без --mute выводит микрофон в наушники — на открытых
                  динамиках это самовозбуждение, надевайте гарнитуру

              AudioProbe aec [--in-name Кусок] [--out-name Кусок] [--delay мс] [секунд]
                  замер эхоподавления в децибелах ERLE. Играет шум в
                  наушники, во время замера надо молчать

            """);
    }
}
