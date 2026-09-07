namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Раскладка DTMF на пакеты RTP.
///
/// Проверяется здесь то, что на живой АТС стоит дороже всего и видно только в
/// дампе трафика: нарастающая длительность, неизменная метка времени внутри
/// события, повторённый конец и сдвиг метки после него. Каждая из этих ошибок
/// на слух выглядит одинаково — «меню не приняло цифру».
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/DTMFTests.swift</c>.
/// </summary>
public sealed class DtmfTests
{
    [Fact]
    public void Разбирает_цифры_звёздочку_и_решётку()
    {
        DtmfSequence sequence = new("12*#");
        Assert.Equal<DtmfStep[]>(
            [new DtmfStep.Tone(1), new DtmfStep.Tone(2), new DtmfStep.Tone(10), new DtmfStep.Tone(11)],
            [.. sequence.Steps]);
        Assert.True(sequence.HasTones);
    }

    [Fact]
    public void Запятые_складываются_в_одну_паузу()
    {
        DtmfSequence sequence = new("1,,2", pauseMilliseconds: 500);
        Assert.Equal<DtmfStep[]>(
            [new DtmfStep.Tone(1), new DtmfStep.Pause(1000), new DtmfStep.Tone(2)],
            [.. sequence.Steps]);
    }

    [Fact]
    public void Пробелы_и_дефисы_только_для_читаемости() =>
        Assert.Equal(new DtmfSequence("*022998#").Steps, new DtmfSequence("*022 998-#").Steps);

    [Fact]
    public void Непонятные_символы_видны_до_сохранения_макроса()
    {
        Assert.Equal<char[]>(['ы'], [.. DtmfSequence.UnsupportedCharacters("12ы3")]);
        Assert.Empty(DtmfSequence.UnsupportedCharacters("*022998#,,"));

        // A–D — законные события DTMF, хоть их и нет на клавиатуре телефона.
        Assert.Empty(DtmfSequence.UnsupportedCharacters("ABCD"));
    }

    [Fact]
    public void Макрос_из_одних_пауз_бесполезен_и_это_видно() =>
        Assert.False(new DtmfSequence(",,,").HasTones);

    [Fact]
    public void Длительность_нарастает_по_такту_пакета()
    {
        DtmfTiming timing = new(ToneMilliseconds: 100, PacketTimeMilliseconds: 20);
        List<DtmfPacket> packets = Packets(DtmfPlanner.Actions(5, timing));

        // Пять пакетов тона по 20 мс: 160 тактов на пакет при часах 8000 Гц.
        List<DtmfPacket> growing = [.. packets.Where(packet => !packet.Payload.IsEnd)];
        Assert.Equal<ushort[]>([160, 320, 480, 640, 800], [.. growing.Select(packet => packet.Payload.Duration)]);
        Assert.All(growing, packet => Assert.Equal(5, packet.Payload.Event));
    }

    [Fact]
    public void Маркер_стоит_только_на_первом_пакете_события()
    {
        List<DtmfPacket> packets = Packets(DtmfPlanner.Actions(1));
        Assert.True(packets[0].IsFirst);
        Assert.All(packets.Skip(1), packet => Assert.False(packet.IsFirst));
    }

    [Fact]
    public void Конец_события_повторяется_трижды_и_несёт_полную_длительность()
    {
        DtmfTiming timing = new(ToneMilliseconds: 100, PacketTimeMilliseconds: 20);
        List<DtmfPacket> ends = [.. Packets(DtmfPlanner.Actions(7, timing)).Where(packet => packet.Payload.IsEnd)];

        // Пакет конца ничем не защищён от потери, а потерянный конец — это тон,
        // который у собеседника длится, пока не придёт следующий.
        Assert.Equal(3, ends.Count);
        Assert.All(ends, packet => Assert.Equal(800, packet.Payload.Duration));
        Assert.Single(ends, packet => packet.CompletesEvent);
        Assert.True(ends[^1].CompletesEvent);
    }

    [Fact]
    public void Метка_времени_сдвигается_на_всю_длительность_тона_а_не_на_один_пакет()
    {
        DtmfTiming timing = new(ToneMilliseconds: 120, PacketTimeMilliseconds: 20);
        DtmfPacket completing = Packets(DtmfPlanner.Actions(0, timing)).First(packet => packet.CompletesEvent);

        // Шесть пакетов по 160 тактов. Сдвинуть на один пакет значило бы
        // отправить остаток разговора со сбитыми на 100 мс часами.
        Assert.Equal(960u, completing.TimestampAdvance);
    }

    [Fact]
    public void Пакеты_тона_идут_через_такт_а_конец_без_пауз()
    {
        DtmfTiming timing = new(ToneMilliseconds: 40, PacketTimeMilliseconds: 20);
        IReadOnlyList<DtmfAction> actions = DtmfPlanner.Actions(3, timing);

        // Два пакета тона, между и после каждого — такт, затем три конца подряд.
        Assert.Equal(7, actions.Count);
        Assert.Equal(20, Assert.IsType<DtmfAction.Wait>(actions[1]).Milliseconds);
        Assert.All(actions.Skip(4), action => Assert.IsType<DtmfAction.Send>(action));
    }

    [Fact]
    public void Тон_короче_такта_всё_равно_даёт_хотя_бы_один_пакет()
    {
        DtmfTiming timing = new(ToneMilliseconds: 5, PacketTimeMilliseconds: 20);
        Assert.NotEmpty(Packets(DtmfPlanner.Actions(9, timing)));
    }

    [Fact]
    public void Между_двумя_тонами_появляется_пауза()
    {
        DtmfTiming timing = new(ToneMilliseconds: 20, GapMilliseconds: 80, PacketTimeMilliseconds: 20);
        IReadOnlyList<DtmfAction> actions = DtmfPlanner.Actions(new DtmfSequence("11"), timing);

        // Две одинаковые цифры подряд без паузы принимающая сторона слышит как
        // одну длинную — и добавочный номер получается на цифру короче.
        Assert.Single(Waits(actions), milliseconds => milliseconds == 80);
    }

    [Fact]
    public void Перед_первым_тоном_паузы_нет() =>
        Assert.IsType<DtmfAction.Send>(DtmfPlanner.Actions(new DtmfSequence("5"))[0]);

    [Fact]
    public void Своя_пауза_заменяет_междуцифровую_а_не_складывается_с_ней()
    {
        DtmfTiming timing = new(ToneMilliseconds: 20, GapMilliseconds: 80, PacketTimeMilliseconds: 20);
        List<int> waits = Waits(DtmfPlanner.Actions(new DtmfSequence("1,2", 1000), timing));

        Assert.Contains(1000, waits);

        // 1080 мс там, где просили секунду, — это уже не то, что записал
        // оператор.
        Assert.DoesNotContain(80, waits);
    }

    [Fact]
    public void Отображение_показывает_паузы_а_не_прячет_их() =>
        Assert.Equal("*022·998#", new DtmfSequence("*022,998#").DisplayText);

    private static List<DtmfPacket> Packets(IEnumerable<DtmfAction> actions) =>
        [.. actions.OfType<DtmfAction.Send>().Select(action => action.Packet)];

    private static List<int> Waits(IEnumerable<DtmfAction> actions) =>
        [.. actions.OfType<DtmfAction.Wait>().Select(action => action.Milliseconds)];
}
