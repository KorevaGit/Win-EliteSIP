namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Сторож за опросом сервера.
///
/// Ловит расхождение, которое иначе не видно ниоткуда: регистрация свежая,
/// клиент показывает «На линии», а обратной дороги нет и звонки не приходят.
/// Разбирается это по журналу задним числом, когда лиды уже ушли соседям.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/QualifyWatchTests.swift</c>.
/// </summary>
public sealed class QualifyWatchTests
{
    /// <summary>Боевая картина: qualify раз в минуту.</summary>
    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Одного_опроса_мало_судить_не_по_чему()
    {
        var watch = new QualifyWatch();
        watch.NoteQualify(At(0));

        // Интервал неизвестен, и молчать сервер может законно — например потому,
        // что qualify у него выключен вовсе.
        Assert.Null(watch.SilenceIfLost(At(3600)));
    }

    [Fact]
    public void Привычный_интервал_выучивается_со_второго_опроса()
    {
        var watch = new QualifyWatch();
        watch.NoteQualify(At(0));
        watch.NoteQualify(At(60));

        Assert.Equal(Minute, watch.LearnedInterval);
    }

    [Fact]
    public void Один_пропущенный_опрос_тревогой_не_считается()
    {
        var watch = new QualifyWatch();
        watch.NoteQualify(At(0));
        watch.NoteQualify(At(60));

        // Два интервала молчания — это один потерянный пакет, а не потерянная
        // дорога. Дёргать регистрацию из-за него значит менять редкую беду на
        // частую.
        Assert.Null(watch.SilenceIfLost(At(60 + 120)));
    }

    [Fact]
    public void Три_пропущенных_опроса_потеря_дороги()
    {
        var watch = new QualifyWatch();
        watch.NoteQualify(At(0));
        watch.NoteQualify(At(60));

        Assert.Equal(TimeSpan.FromSeconds(180), watch.SilenceIfLost(At(60 + 180)));
    }

    [Fact]
    public void Потери_не_задирают_порог_интервал_берётся_минимальный()
    {
        var watch = new QualifyWatch();
        watch.NoteQualify(At(0));
        watch.NoteQualify(At(60));

        // Провал, ровно как в архиве 18 августа: 210 секунд тишины, потом опрос.
        watch.NoteQualify(At(270));

        // Если бы интервалом считался последний промежуток, порог стал бы десятью
        // минутами — сторож ослеп бы ровно после первого провала.
        Assert.Equal(Minute, watch.LearnedInterval);
        Assert.Equal(TimeSpan.FromSeconds(180), watch.SilenceIfLost(At(270 + 180)));
    }

    [Fact]
    public void Внеочередной_опрос_после_регистрации_не_занижает_порог()
    {
        var watch = new QualifyWatch();
        watch.NoteQualify(At(0));
        watch.NoteQualify(At(60));

        // Asterisk опрашивает пир сразу после REGISTER — промежуток в секунды.
        watch.NoteQualify(At(62));

        // Без нижней границы порог стал бы шестью секундами, и сторож
        // перерегистрировал бы машину каждые несколько секунд.
        Assert.Equal(QualifyWatch.MinimumInterval, watch.LearnedInterval);
        Assert.Null(watch.SilenceIfLost(At(62 + 60)));
    }

    [Fact]
    public void Тревога_сдвигает_отсчёт_в_цикл_она_не_уходит()
    {
        var watch = new QualifyWatch();
        watch.NoteQualify(At(0));
        watch.NoteQualify(At(60));

        Assert.NotNull(watch.SilenceIfLost(At(240)));

        // Сервер, до которого не достучаться вовсе, не должен превращаться в
        // поток перерегистраций.
        Assert.Null(watch.SilenceIfLost(At(241)));
        Assert.Null(watch.SilenceIfLost(At(300)));
        Assert.NotNull(watch.SilenceIfLost(At(420)));
    }

    [Fact]
    public void Удавшаяся_регистрация_считается_доказательством_дороги()
    {
        var watch = new QualifyWatch();
        watch.NoteQualify(At(0));
        watch.NoteQualify(At(60));

        // Ответ сервера на наш REGISTER дошёл — значит дорога есть, и молчание
        // опроса отсчитывается заново.
        watch.NoteReachable(At(200));
        Assert.Null(watch.SilenceIfLost(At(300)));

        // При этом частоту опроса регистрация не переучивает.
        Assert.Equal(Minute, watch.LearnedInterval);
    }
}
