namespace EliteSIP.MediaCore.Tests;

/// <summary>
/// Кого сессия слушает на своём порту.
///
/// Две ошибки, между которыми проходит граница, стоят разного, и обе дорого.
/// Принимать всех — значит отдать оператору чужой голос вперемешку с
/// собеседником. Не принимать никого, кроме первого, — значит однажды замолчать
/// навсегда: Asterisk меняет SSRC на смене моста, то есть ровно после перевода.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/RemoteSourceTests.swift</c>.
/// Проверка на живом потоке едет вместе с <c>MediaSession</c> на этап W4.
/// </summary>
public sealed class RemoteSourceFilterTests
{
    [Fact]
    public void Первый_пакет_задаёт_источник()
    {
        RemoteSourceFilter filter = new();
        Assert.Null(filter.Accepted);
        Assert.Equal(RemoteSourceVerdict.Known, filter.Admit(0x1111_1111));
        Assert.Equal(0x1111_1111u, filter.Accepted);
    }

    [Fact]
    public void Одиночный_чужой_пакет_отбрасывается()
    {
        RemoteSourceFilter filter = new();
        filter.Admit(0x1111_1111);

        Assert.Equal(RemoteSourceVerdict.Foreign, filter.Admit(0xDEAD_BEEF));
        Assert.Equal(0x1111_1111u, filter.Accepted);
    }

    [Fact]
    public void Настойчивый_источник_признаётся_своим()
    {
        RemoteSourceFilter filter = new();
        filter.Admit(0x1111_1111);

        // Первые четыре — ещё не повод.
        for (int index = 0; index < RemoteSourceFilter.AdoptionRun - 1; index++)
        {
            Assert.Equal(RemoteSourceVerdict.Foreign, filter.Admit(0x2222_2222));
        }

        Assert.Equal(RemoteSourceVerdict.Adopted, filter.Admit(0x2222_2222));
        Assert.Equal(0x2222_2222u, filter.Accepted);

        // Дальше он обычный, а не «только что признанный»: буфер чистится один
        // раз на смену, а не на каждый пакет после неё.
        Assert.Equal(RemoteSourceVerdict.Known, filter.Admit(0x2222_2222));
    }

    [Fact]
    public void Живой_собеседник_не_даёт_себя_вытеснить()
    {
        RemoteSourceFilter filter = new();
        filter.Admit(0x1111_1111);

        // Чужой поток идёт вперемешку с настоящим. Без обнуления счёта
        // кандидата он рано или поздно накопил бы свои пять пакетов и забрал
        // разговор себе — при живом и говорящем собеседнике.
        for (int index = 0; index < 20; index++)
        {
            Assert.Equal(RemoteSourceVerdict.Foreign, filter.Admit(0x2222_2222));
            Assert.Equal(RemoteSourceVerdict.Known, filter.Admit(0x1111_1111));
        }

        Assert.Equal(0x1111_1111u, filter.Accepted);
    }

    [Fact]
    public void Два_чужих_источника_не_складываются_в_один()
    {
        RemoteSourceFilter filter = new();
        filter.Admit(0x1111_1111);

        // Счёт ведётся одному кандидату, а не «всем непонятным»: иначе два
        // разных чужих потока вместе набрали бы порог, которого ни один из них
        // сам по себе не набрал.
        for (int index = 0; index < 10; index++)
        {
            Assert.Equal(RemoteSourceVerdict.Foreign, filter.Admit(0x2222_2222));
            Assert.Equal(RemoteSourceVerdict.Foreign, filter.Admit(0x3333_3333));
        }

        Assert.Equal(0x1111_1111u, filter.Accepted);
    }
}
