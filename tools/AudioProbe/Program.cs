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
                "capture" => CaptureProbe.Run(Positional(args, 10), Text(args, "--in-name")),
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
                    Option(args, "--delay") ?? 60,
                    quiet: false,
                    suppression: !Flag(args, "--isolate"),
                    raw: Flag(args, "--raw"),
                    primeMs: Option(args, "--prime") ?? 60,
                    mobile: Flag(args, "--mobile")),
                "aec-sweep" => AecSweep(args),
                "echo-path" => EchoPathProbe.Run(
                    Text(args, "--in-name"),
                    Text(args, "--out-name"),
                    Positional(args, 12),
                    Flag(args, "--raw"),
                    (Option(args, "--level") ?? 15) / 100f),
                "aec-model" => AecModelProbe.Run(
                    Positional(args, 14),
                    Option(args, "--reverb") ?? 150,
                    !Flag(args, "--isolate"),
                    product: Flag(args, "--product")),
                "aec-selftest" => AecSelfTest.Run(Option(args, "--delay") ?? 60, Positional(args, 20)),
                // Длительность и ключом тоже: общий разборщик позиционного
                // числа пропускает его, если слева стоит флаг, и «tract
                // --matrix 30» молча превращалось в шестьдесят секунд. Ключ
                // работает при любом порядке аргументов.
                "tract" => TractProbe.Run(
                    Text(args, "--in-name"),
                    Text(args, "--out-name"),
                    Option(args, "--seconds") ?? Positional(args, 60),
                    audible: Flag(args, "--audible"),
                    matrix: Flag(args, "--matrix")),
                _ => Unknown(args[0]),
            };
        }
        catch (ArgumentOutOfRangeException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>
    /// Перебор объявленной задержки опорного сигнала.
    ///
    /// Нужен потому, что одиночный замер на открытых динамиках дал ERLE 3,3 дБ
    /// при живом эхе, и объявленная задержка — первый подозреваемый: AEC3
    /// ищет отражение в окне вокруг того числа, которое ему назвали, и если
    /// промахнуться, он не найдёт ничего и честно ничего не подавит.
    ///
    /// Шумодав и АРУ на переборе выключены: они тоже меняют энергию выхода, а
    /// здесь надо видеть один лишь эхоподавитель.
    /// </summary>
    private static int AecSweep(string[] args)
    {
        int[] delays = [0, 20, 40, 60, 80, 120, 160, 200, 260];
        string? inputName = Text(args, "--in-name");
        string? outputName = Text(args, "--out-name");

        Console.WriteLine("Перебор задержки. Шум будет идти примерно {0} с. Молчите.", delays.Length * 12);
        Console.WriteLine();

        foreach (int delay in delays)
        {
            AecProbe.Run(
                inputName,
                outputName,
                seconds: 12,
                delayMs: delay,
                quiet: true,
                suppression: false,
                convergeSeconds: 5,
                raw: Flag(args, "--raw"),
                primeMs: Option(args, "--prime") ?? 60);
        }

        Console.WriteLine();
        Console.WriteLine("Если ERLE не растёт ни на одной задержке — дело не в ней.");
        return 0;
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

              AudioProbe tract [--in-name Кусок] [--out-name Кусок]
                               [--matrix] [--audible] [секунд]
                  прогон боевого тракта (этап W4): баланс отсчётов,
                  состояние устройства, уход часов, поправка темпа.
                  --matrix прогоняет каждую пару «вход — выход».
                  Без --audible звук наружу не идёт

            """);
    }
}
