namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Джиттер-буфер.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/JitterBufferTests.swift</c>.
/// </summary>
public sealed class JitterBufferTests
{
    private static RtpPacket Packet(ushort sequence, uint? timestamp = null, byte value = 0x11)
    {
        byte[] payload = new byte[160];
        Array.Fill(payload, value);
        return new RtpPacket(0, sequence, timestamp ?? unchecked((uint)(sequence * 160)), 0x1234, payload);
    }

    private static JitterBuffer MakeBuffer(int target = 3, int maximum = 12) =>
        new(target, target, maximum);

    /// <summary>
    /// Номера настоящих кадров, без сокрытий.
    ///
    /// Выдача сама не заканчивается — дыру буфер затыкает повтором последнего
    /// хорошего, — поэтому вызовы ограничиваются снаружи.
    /// </summary>
    private static List<ushort> Drain(JitterBuffer buffer, int calls = 40)
    {
        List<ushort> result = [];
        for (int index = 0; index < calls; index++)
        {
            if (buffer.Pop() is not JitterFrame frame)
            {
                break;
            }

            if (!frame.IsConcealment)
            {
                result.Add(frame.SequenceNumber);
            }
        }

        return result;
    }

    /// <summary>
    /// Поток пакетов с заданным разбросом прихода.
    ///
    /// Метки времени идут ровно, а приходы — вразнобой: это и есть джиттер в
    /// том смысле, в каком его считает RFC 3550.
    /// </summary>
    private static void Feed(
        JitterBuffer buffer,
        int packets,
        double jitter,
        double packetTime = 0.02,
        bool drainingEvery = true)
    {
        double arrival = 1000;
        double wobble = jitter;
        for (int index = 0; index < packets; index++)
        {
            arrival += packetTime + wobble;
            wobble = -wobble;
            buffer.Push(Packet((ushort)(index + 1)), arrival);
            if (drainingEvery)
            {
                buffer.Pop();
            }
        }
    }

    [Fact]
    public void До_набора_целевой_глубины_ничего_не_отдаёт()
    {
        JitterBuffer buffer = MakeBuffer(3);

        buffer.Push(Packet(1));
        Assert.Null(buffer.Pop());
        buffer.Push(Packet(2));
        Assert.Null(buffer.Pop());

        buffer.Push(Packet(3));
        Assert.Equal<ushort?>(1, buffer.Pop()?.SequenceNumber);
    }

    [Fact]
    public void Отдаёт_кадры_по_порядку()
    {
        JitterBuffer buffer = MakeBuffer(2);
        for (ushort sequence = 10; sequence <= 14; sequence++)
        {
            buffer.Push(Packet(sequence));
        }

        Assert.Equal<ushort[]>([10, 11, 12, 13, 14], [.. Drain(buffer)]);
    }

    [Fact]
    public void Переставленные_пакеты_выстраиваются_обратно()
    {
        JitterBuffer buffer = MakeBuffer(3);

        // Пришли не по порядку — ровно то, ради чего буфер и существует.
        buffer.Push(Packet(3));
        buffer.Push(Packet(1));
        buffer.Push(Packet(2));

        Assert.Equal<ushort[]>([1, 2, 3], [.. Drain(buffer)]);
        Assert.True(buffer.Statistics.Reordered > 0);
    }

    [Fact]
    public void Потерянный_кадр_заменяется_повтором_последнего_а_не_тишиной()
    {
        JitterBuffer buffer = MakeBuffer(2);
        buffer.Push(Packet(1, value: 0x7A));
        buffer.Push(Packet(3)); // второй потерян
        buffer.Push(Packet(4));

        JitterFrame good = Assert.IsType<JitterFrame>(buffer.Pop());
        Assert.Equal(1, good.SequenceNumber);

        JitterFrame concealed = Assert.IsType<JitterFrame>(buffer.Pop());
        Assert.Equal(2, concealed.SequenceNumber);
        Assert.True(concealed.IsConcealment);

        // Длина обязана совпадать с кадром, иначе поедет выравнивание на
        // воспроизведении.
        Assert.Equal(160, concealed.Payload.Length);

        // И это именно повтор: тишина на месте потери слышна как провал, а
        // повтор сохраняет громкость и основной тон — одиночную потерю на слух
        // почти не поймать. Затухание накладывает воспроизведение.
        Assert.Equal(good.Payload, concealed.Payload);

        Assert.Equal<ushort?>(3, buffer.Pop()?.SequenceNumber);
        Assert.Equal(1, buffer.Statistics.Concealed);
    }

    [Fact]
    public void Опоздавший_пакет_отбрасывается_а_не_вставляется_задним_числом()
    {
        JitterBuffer buffer = MakeBuffer(2);
        buffer.Push(Packet(5));
        buffer.Push(Packet(6));
        Assert.Equal<ushort?>(5, buffer.Pop()?.SequenceNumber);
        Assert.Equal<ushort?>(6, buffer.Pop()?.SequenceNumber);

        // Пятый доехал, когда его время давно прошло.
        buffer.Push(Packet(5));
        Assert.Equal(1, buffer.Statistics.Late);
        Assert.Equal(0, buffer.Depth);
    }

    [Fact]
    public void Дубликаты_не_размножают_звук()
    {
        JitterBuffer buffer = MakeBuffer(2);
        buffer.Push(Packet(1));
        buffer.Push(Packet(1));
        buffer.Push(Packet(2));

        Assert.Equal(1, buffer.Statistics.Duplicated);
        Assert.Equal(2, buffer.Depth);
    }

    [Fact]
    public void Распухший_буфер_догоняет_реальное_время()
    {
        // Иначе задержка растёт и уже не возвращается: разговор превращается в
        // переписку с задержкой в секунды.
        JitterBuffer buffer = MakeBuffer(3, maximum: 6);
        for (ushort sequence = 1; sequence <= 20; sequence++)
        {
            buffer.Push(Packet(sequence));
        }

        Assert.True(buffer.Depth <= 6);
        Assert.True(buffer.Statistics.Dropped > 0);

        // Продолжаем с самых свежих кадров, а не с начала.
        JitterFrame frame = Assert.IsType<JitterFrame>(buffer.Pop());
        Assert.True(frame.SequenceNumber > 10, $"догонять надо вперёд, отдан {frame.SequenceNumber}");
    }

    [Fact]
    public void Опустевший_буфер_сначала_прячет_потерю_и_только_потом_сдаётся()
    {
        JitterBuffer buffer = MakeBuffer(2);
        buffer.Push(Packet(1));
        buffer.Push(Packet(2));
        Assert.Equal<ushort?>(1, buffer.Pop()?.SequenceNumber);
        Assert.Equal<ushort?>(2, buffer.Pop()?.SequenceNumber);

        // Короткий перерыв в потоке затыкается повтором: провал слышен, повтор
        // почти нет.
        for (int index = 0; index < JitterBuffer.MaximumConcealmentRun; index++)
        {
            Assert.True(buffer.Pop()?.IsConcealment);
        }

        Assert.Equal(1, buffer.Statistics.Underruns);

        // Дольше повторять нельзя — получится заевшая пластинка.
        Assert.Null(buffer.Pop());

        // И дальше буфер снова копит запас, а не отдаёт по одному кадру.
        buffer.Push(Packet(3));
        Assert.Null(buffer.Pop());
        buffer.Push(Packet(4));
        Assert.Equal<ushort?>(3, buffer.Pop()?.SequenceNumber);
    }

    [Fact]
    public void Переполнение_шестнадцатибитного_счётчика_не_ломает_порядок()
    {
        // Наивное сравнение a < b ломается раз на 65536 пакетов — примерно раз
        // в 22 минуты разговора. Проявляется как секунда тишины на ровном месте.
        JitterBuffer buffer = MakeBuffer(3);
        buffer.Push(Packet(65_534));
        buffer.Push(Packet(65_535));
        buffer.Push(Packet(0));
        buffer.Push(Packet(1));

        Assert.Equal<ushort[]>([65_534, 65_535, 0, 1], [.. Drain(buffer)]);
    }

    [Fact]
    public void Сравнение_номеров_с_учётом_переполнения()
    {
        Assert.True(JitterBuffer.IsOlder(1, 2));
        Assert.False(JitterBuffer.IsOlder(2, 1));

        // Через границу: 65535 старше нуля, а не новее.
        Assert.True(JitterBuffer.IsOlder(65_535, 0));
        Assert.False(JitterBuffer.IsOlder(0, 65_535));
        Assert.False(JitterBuffer.IsOlder(5, 5));
    }

    [Fact]
    public void Сброс_возвращает_буфер_в_исходное_состояние()
    {
        JitterBuffer buffer = MakeBuffer(2);
        buffer.Push(Packet(1));
        buffer.Push(Packet(2));
        buffer.Pop();

        buffer.Reset();
        Assert.True(buffer.IsEmpty);
        Assert.Null(buffer.Pop());

        // После сброса принимаются любые номера, включая меньшие прежних.
        buffer.Push(Packet(1));
        buffer.Push(Packet(2));
        Assert.Equal<ushort?>(1, buffer.Pop()?.SequenceNumber);
    }

    [Fact]
    public void Пока_повторять_нечего_дыра_затыкается_тишиной_кодека()
    {
        // Случай редкий, но настоящий: первый же ожидаемый кадр не доехал, и
        // хорошего кадра для повтора ещё не было. Тишина должна быть в текущем
        // кодеке — в A-law и µ-law это разные байты, и перепутать их значит
        // получить ровный треск вместо паузы.
        JitterBuffer buffer = new(1, 1, codec: AudioCodec.Pcma);
        buffer.Push(Packet(5));
        buffer.Push(Packet(7));
        buffer.Pop(); // пятый

        JitterFrame concealed = Assert.IsType<JitterFrame>(buffer.Pop()); // шестого нет
        Assert.True(concealed.IsConcealment);
        Assert.Equal(160, concealed.Payload.Length);
    }

    [Fact]
    public void Поток_с_потерями_и_перестановками_проигрывается_без_сбоев_порядка()
    {
        JitterBuffer buffer = MakeBuffer(3, maximum: 10);

        // Каждый седьмой теряется, каждый третий приходит с опережением.
        List<ushort> incoming = [];
        for (ushort sequence = 1; sequence <= 60; sequence++)
        {
            if (sequence % 7 != 0)
            {
                incoming.Add(sequence);
            }
        }

        for (int index = 0; index < incoming.Count - 1; index += 3)
        {
            (incoming[index], incoming[index + 1]) = (incoming[index + 1], incoming[index]);
        }

        // Забираем ровно по одному кадру на пакет — так работает настоящее
        // воспроизведение, у которого свой ровный такт в 20 мс. Если вместо
        // этого выбирать буфер досуха, он после каждого недобора
        // пересинхронизируется, и пропуски просто перескакиваются вместо того,
        // чтобы маскироваться.
        List<ushort> played = [];
        foreach (ushort sequence in incoming)
        {
            buffer.Push(Packet(sequence));
            if (buffer.Pop() is JitterFrame frame)
            {
                played.Add(frame.SequenceNumber);
            }
        }

        while (buffer.Pop() is JitterFrame frame)
        {
            played.Add(frame.SequenceNumber);
        }

        // Главное свойство: на выход номера идут строго по возрастанию, без
        // повторов и без провалов назад.
        Assert.Equal(played.Order(), played);
        Assert.Equal(played.Count, played.Distinct().Count());
        Assert.True(played.Count > 40, $"проиграно всего {played.Count} кадров");
        Assert.True(buffer.Statistics.Concealed > 0, "потери должны быть замаскированы");
    }

    [Fact]
    public void На_ровной_сети_глубина_опускается_до_нижней_границы()
    {
        JitterBuffer buffer = new(6, 2, 12);

        // 300 пакетов — это шесть секунд, дольше окна спокойствия в пять.
        Feed(buffer, 300, jitter: 0);

        Assert.True(buffer.JitterMilliseconds < 1);
        Assert.True(buffer.TargetDepth < 6, "ровный поток не должен держать запас на шесть кадров");
    }

    [Fact]
    public void Всплеск_джиттера_поднимает_глубину_сразу()
    {
        JitterBuffer buffer = new(2, 2, 12);

        // Разброс ±30 мс — это полтора кадра в каждую сторону, на таком буфер из
        // двух кадров гарантированно недобирает.
        Feed(buffer, 120, jitter: 0.03);

        Assert.True(buffer.JitterMilliseconds > 20, $"джиттер {buffer.JitterMilliseconds} мс");
        Assert.True(buffer.TargetDepth > 2, $"запас обязан вырасти, глубина {buffer.TargetDepth}");
    }

    [Fact]
    public void Глубина_не_выходит_за_заданные_границы()
    {
        JitterBuffer buffer = new(3, 2, 5);

        // Разброс в четверть секунды — заведомо больше потолка.
        Feed(buffer, 200, jitter: 0.25);
        Assert.True(buffer.TargetDepth <= 5);

        JitterBuffer calm = new(3, 3, 12);
        Feed(calm, 400, jitter: 0);
        Assert.True(calm.TargetDepth >= 3, "ниже нижней границы опускаться нельзя");
    }

    [Fact]
    public void Переполнение_метки_времени_не_выглядит_как_всплеск_джиттера()
    {
        // Метка времени тридцатидвухбитная и переполняется примерно раз в шесть
        // суток непрерывного разговора, но начальное значение случайно по
        // RFC 3550, так что переход может случиться на любой минуте. Наивная
        // разность даёт джиттер в сутки и раздувает буфер до потолка.
        JitterBuffer buffer = new(2, 2, 12);
        double arrival = 500;
        uint timestamp = uint.MaxValue - 480;

        for (int index = 0; index < 50; index++)
        {
            arrival += 0.02;
            timestamp = unchecked(timestamp + 160);
            buffer.Push(Packet((ushort)(index + 1), timestamp), arrival);
            buffer.Pop();
        }

        Assert.True(buffer.JitterMilliseconds < 5, $"джиттер {buffer.JitterMilliseconds} мс");
        Assert.Equal(2, buffer.TargetDepth);
    }
}
