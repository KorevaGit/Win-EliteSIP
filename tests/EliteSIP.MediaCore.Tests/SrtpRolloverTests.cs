using EliteSIP.MediaCore;

namespace EliteSIP.MediaCore.Tests;

/// <summary>SRTP: переход счётчика.</summary>
///
/// <remarks>
/// Отдельный набор про переход счётчика пакетов через ноль. Это место в SRTP
/// ломается чаще всего и ломается позже всего: номер шестнадцатибитный, и при
/// 20 мс на пакет переход случается примерно через двадцать две минуты
/// разговора. Ошибка здесь выглядит не как отказ, а как внезапная тишина на
/// длинном звонке — из тех, что не воспроизводятся на стенде и списываются на
/// сеть.
///
/// Основной набор проверяет ровный переход. Здесь — то, что бывает в жизни:
/// потери и перестановки ровно на границе, когда отправитель и получатель
/// по-разному представляют, какой сейчас оборот.
/// </remarks>
public sealed class SrtpRolloverTests
{
    private static SrtpMasterKey Key => new(SrtpTests.Repeated(0x5A));

    private static RtpPacket Packet(ushort sequence, byte payload = 0x7F) => new(
        payloadType: 0,
        sequenceNumber: sequence,
        timestamp: (uint)(sequence * 160),
        ssrc: 0x0BADF00D,
        payload: Enumerable.Repeat(payload, 160).ToArray());

    [Fact]
    public void Потеря_пакета_на_границе_оборота_не_сбивает_счётчик()
    {
        SrtpContext sender = new(Key);
        SrtpContext receiver = new(Key);

        // Отправитель нумерует подряд, а до получателя 65535 не доезжает —
        // именно тот пакет, на котором происходит переход.
        List<(ushort Sequence, byte[] Data)> onTheWire = [];
        foreach (var sequence in new ushort[] { 65_533, 65_534, 65_535, 0, 1, 2 })
        {
            onTheWire.Add((sequence, sender.Protect(Packet(sequence))));
        }

        foreach (var (sequence, data) in onTheWire.Where(item => item.Sequence != 65_535))
        {
            var decoded = receiver.Unprotect(data);

            Assert.Equal(sequence, decoded.SequenceNumber);
            Assert.Equal(Enumerable.Repeat((byte)0x7F, 160), decoded.Payload.ToArray());
        }
    }

    [Fact]
    public void Опоздавший_пакет_из_прошлого_оборота_расшифровывается_верно()
    {
        SrtpContext sender = new(Key);
        SrtpContext receiver = new(Key);

        var before = sender.Protect(Packet(65_534, 0x11));
        var atEdge = sender.Protect(Packet(65_535, 0x22));
        var after = sender.Protect(Packet(0, 0x33));
        var next = sender.Protect(Packet(1, 0x44));

        // Порядок доставки: край проехал, потом пришло уже из нового оборота, и
        // только затем доковылял опоздавший из старого.
        Assert.Equal(0x11, receiver.Unprotect(before).Payload.Span[0]);
        Assert.Equal(0x33, receiver.Unprotect(after).Payload.Span[0]);
        Assert.Equal(0x44, receiver.Unprotect(next).Payload.Span[0]);

        // Ключевая проверка: получатель уже перешёл на новый оборот, и наивная
        // оценка индекса дала бы для 65535 неверный счётчик. Расшифровать его с
        // неверным индексом — значит получить шум вместо звука, причём пакет
        // пройдёт проверку подлинности только если индекс угадан верно.
        var late = receiver.Unprotect(atEdge);

        Assert.Equal(65_535, late.SequenceNumber);
        Assert.Equal(Enumerable.Repeat((byte)0x22, 160), late.Payload.ToArray());
    }

    [Fact]
    public void Разговор_длиннее_оборота_идёт_без_потерь_смысла()
    {
        SrtpContext sender = new(Key);
        SrtpContext receiver = new(Key);

        // Начинаем незадолго до границы и проходим её насквозь. Двести пакетов —
        // это четыре секунды разговора, но важен именно переход.
        var sequence = (ushort)65_400;
        for (var step = 0; step < 200; step++)
        {
            var payload = (byte)step;
            var decoded = receiver.Unprotect(sender.Protect(Packet(sequence, payload)));

            Assert.Equal(Enumerable.Repeat(payload, 160), decoded.Payload.ToArray());
            sequence++;
        }
    }

    [Fact]
    public void Повтор_из_прошлого_оборота_отбрасывается()
    {
        SrtpContext sender = new(Key);
        SrtpContext receiver = new(Key);

        var edge = sender.Protect(Packet(65_535));
        sender.Protect(Packet(0));

        Assert.Equal(65_535, receiver.Unprotect(edge).SequenceNumber);
        Assert.Equal(
            SrtpFailure.ReplayedPacket,
            Assert.Throws<SrtpException>(() => receiver.Unprotect(edge)).Failure);
    }

    [Fact]
    public void Окно_повторов_держит_перестановку_в_пределах_глубины()
    {
        SrtpContext sender = new(Key);
        SrtpContext receiver = new(Key);

        List<byte[]> wire = [];
        for (var sequence = (ushort)1000; sequence <= 1039; sequence++)
        {
            wire.Add(sender.Protect(Packet(sequence)));
        }

        // Сначала самый свежий, потом всё остальное задом наперёд: перестановка
        // на сорок пакетов — это восемьсот миллисекунд, больше любого разумного
        // джиттера, и окно обязано её пережить.
        Assert.Equal(1039, receiver.Unprotect(wire[39]).SequenceNumber);
        for (var index = 38; index >= 0; index--)
        {
            Assert.Equal(1000 + index, receiver.Unprotect(wire[index]).SequenceNumber);
        }

        // А вот теперь любой из них — уже повтор.
        Assert.Equal(
            SrtpFailure.ReplayedPacket,
            Assert.Throws<SrtpException>(() => receiver.Unprotect(wire[20])).Failure);
    }

    [Fact]
    public void Слишком_старый_пакет_отбрасывается_по_глубине_окна()
    {
        SrtpContext sender = new(Key);
        SrtpContext receiver = new(Key);

        var ancient = sender.Protect(Packet(1));
        for (var sequence = (ushort)2; sequence <= 200; sequence++)
        {
            receiver.Unprotect(sender.Protect(Packet(sequence)));
        }

        // Окно шестьдесят четыре пакета; всё, что старше, считается повтором без
        // разбора. Это требование RFC 3711 §3.3.2, а не наша экономия: помнить
        // всю историю разговора нельзя, а принимать что попало из далёкого
        // прошлого — значит открыть дорогу повторной отправке.
        Assert.Equal(
            SrtpFailure.ReplayedPacket,
            Assert.Throws<SrtpException>(() => receiver.Unprotect(ancient)).Failure);
    }

    [Fact]
    public void Чужой_ключ_не_расшифровывает_а_отвергает()
    {
        SrtpContext sender = new(Key);
        SrtpContext stranger = new(new SrtpMasterKey(SrtpTests.Repeated(0xA5)));

        // Важно, что это именно отказ проверки подлинности, а не мусор на
        // выходе: пакет с чужим ключом не должен доехать до звукового тракта ни
        // в каком виде.
        Assert.Equal(
            SrtpFailure.AuthenticationFailed,
            Assert.Throws<SrtpException>(() => stranger.Unprotect(sender.Protect(Packet(500)))).Failure);
    }
}
