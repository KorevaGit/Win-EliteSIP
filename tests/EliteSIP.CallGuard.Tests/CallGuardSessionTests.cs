using System.Text.Json;
using System.Text.Json.Serialization;
using EliteSIP.CallGuard;

namespace EliteSIP.CallGuard.Tests;

/// <summary>Защита от автокликеров.</summary>
public sealed class CallGuardSessionTests
{
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Политика с включённым цифровым подтверждением: по умолчанию оно
    /// выключено, а проверять выбор цели надо именно на нём.
    /// </summary>
    private static CallGuardPolicy WithDigits => new() { TargetCount = 3 };

    /// <summary>
    /// Генератор с заданным зерном — чтобы «случайное» задание повторялось от
    /// прогона к прогону и проверялось точно, а не «примерно».
    /// </summary>
    private static CallGuardSession Session(CallGuardPolicy? policy = null, int seed = 7)
        => new(policy ?? new CallGuardPolicy(), Start, new Random(seed));

    /// <summary>Курсор, прошедший по окну как рука, а не как телепорт.</summary>
    private static void MoveCursorLikeHuman(CallGuardSession session, int steps = 10)
    {
        for (var step = 0; step <= steps; step++)
        {
            session.NoteCursor(new ScreenPoint(step * 12.0, step * 5.0));
        }
    }

    private static CallGuardAttempt MouseAttempt(
        CallGuardSession session,
        int milliseconds,
        char? target = null,
        bool isSynthetic = false)
        => new(
            target ?? session.Challenge.Answer,
            isSynthetic,
            Start + TimeSpan.FromMilliseconds(milliseconds));

    // MARK: задание

    [Fact]
    public void ПоУмолчаниюЦифровогоПодтвержденияНетЕстьТолькоСлучайность()
    {
        var session = Session();

        // Основная мера — случайная позиция окна: она ничего не стоит оператору.
        // Выбор цифры стоит внимания на каждом вызове, поэтому включается
        // отдельно и осознанно.
        Assert.False(session.Challenge.HasChoice);
        Assert.True(session.Report.WasGuardEnabled);
    }

    [Fact]
    public void КнопкаАктивнаСразуИМгновенныйКликЖивойРукиПринимается()
    {
        var session = Session();
        MoveCursorLikeHuman(session);

        // Ноль миллисекунд от появления окна. Локальной задержки активации нет
        // намеренно: она стоила оператору внимания на каждом вызове, а кликеру —
        // одной строки ожидания. Ровное время реакции ловит статистика панели.
        var verdict = session.Evaluate(MouseAttempt(session, 0));

        Assert.True(verdict.IsAccepted);
        Assert.Equal(0, session.Report.ReactionMilliseconds);
        Assert.False(session.Report.LooksAutomated);
    }

    [Fact]
    public void ЗаданиеСобираетсяИзНепересекающихсяЦелей()
    {
        for (var seed = 0; seed < 32; seed++)
        {
            var session = new CallGuardSession(WithDigits, Start, new Random(seed));

            Assert.Equal(3, session.Challenge.Targets.Count);
            Assert.Equal(3, session.Challenge.Targets.Distinct().Count());
            Assert.Contains(session.Challenge.Answer, session.Challenge.Targets);
        }
    }

    [Fact]
    public void ВыключеннаяЗащитаНеЗадаётВыбора()
    {
        var session = Session(CallGuardPolicy.Disabled);

        Assert.False(session.Challenge.HasChoice);
        Assert.False(session.Report.WasGuardEnabled);
    }

    [Fact]
    public void СломаннаяПолитикаНеВыключаетЗащитуМолча()
    {
        var broken = new CallGuardPolicy
        {
            TunesRandomnessByHand = true,
            TunesLivenessByHand = true,
            TargetCount = 0,
            RequiredCursorTravel = -50,
            MinimumTravel = -1,
        };

        var normalized = broken.Normalized();

        Assert.Equal(1, normalized.TargetCount);
        Assert.Equal(0, normalized.RequiredCursorTravel);
        Assert.Equal(0, normalized.MinimumTravel);
    }

    [Fact]
    public void НаАвтоРасстоянияЗаводскиеЧемБыНиБылИспорченФайл()
    {
        // Ровно тот случай, ради которого признак заведён: числа в файле
        // сдвинуты — ползунком, чужой правкой, старой версией, — а слой объявлен
        // автоматическим. «Авто» обязано означать «как задумано», а не «то, что
        // осталось от прошлой правки».
        var drifted = new CallGuardPolicy
        {
            MinimumTravel = 25,
            ScreenMargin = 200,
            RequiredCursorTravel = 0,
            RequiredCursorSamples = 0,
        };

        var normalized = drifted.Normalized();
        var factory = new CallGuardPolicy();

        Assert.Equal(factory.MinimumTravel, normalized.MinimumTravel);
        Assert.Equal(factory.ScreenMargin, normalized.ScreenMargin);
        Assert.Equal(factory.RequiredCursorTravel, normalized.RequiredCursorTravel);
        Assert.Equal(factory.RequiredCursorSamples, normalized.RequiredCursorSamples);
    }

    [Fact]
    public void РучнойСлойДержитВыставленныеЧисла()
    {
        var tuned = new CallGuardPolicy
        {
            TunesRandomnessByHand = true,
            TunesLivenessByHand = true,
            MinimumTravel = 300,
            ScreenMargin = 64,
            RequiredCursorTravel = 120,
        };

        var normalized = tuned.Normalized();

        Assert.Equal(300, normalized.MinimumTravel);
        Assert.Equal(64, normalized.ScreenMargin);
        Assert.Equal(120, normalized.RequiredCursorTravel);
    }

    [Fact]
    public void ВыключеннаяЗащитаОстаётсяВыключеннойПослеПриведения()
    {
        // `Disabled` объявлен ручным намеренно: на «Авто» приведение вернуло бы
        // заводские сорок точек пути курсора — то есть выключенная защита
        // требовала бы движения мыши.
        var normalized = CallGuardPolicy.Disabled.Normalized();

        Assert.Equal(0, normalized.RequiredCursorTravel);
        Assert.Equal(0, normalized.RequiredCursorSamples);
        Assert.Equal(0, normalized.MinimumTravel);
    }

    [Fact]
    public void СтарыйФайлНастроекНеВозвращаетЗадержкуАктивации()
    {
        // Файл, записанный до удаления задержки. Ключи должны быть молча
        // проигнорированы: подхватить их значило бы вернуть поведение, от
        // которого отказались, — и вернуть его тихо, одним старым файлом.
        const string old = """
        {
          "IsEnabled": true,
          "MinimumActivationDelayMilliseconds": 900,
          "MaximumActivationDelayMilliseconds": 1500,
          "TargetCount": 1
        }
        """;

        var policy = JsonSerializer.Deserialize<CallGuardPolicy>(old)!;
        Assert.True(policy.IsEnabled);

        var session = new CallGuardSession(policy, Start, new Random(5));
        MoveCursorLikeHuman(session);
        Assert.True(session.Evaluate(MouseAttempt(session, 0)).IsAccepted);

        // И обратно: удалённые ключи не должны появиться в новом файле.
        Assert.DoesNotContain("ActivationDelay", JsonSerializer.Serialize(policy), StringComparison.Ordinal);
    }

    [Fact]
    public void НажатиеНеНаТуЦельОтклоняетсяНоРоботомНеСчитается()
    {
        var session = Session(WithDigits);
        MoveCursorLikeHuman(session);

        var wrong = session.Challenge.Targets.First(target => target != session.Challenge.Answer);
        var verdict = session.Evaluate(MouseAttempt(session, 2000, wrong));

        Assert.Equal(CallGuardRejection.WrongTarget, verdict.Rejection);

        // Промахнуться мимо кнопки — обычное человеческое дело, и поднимать по
        // этому поводу тревогу значит утопить её в ложных сигналах.
        Assert.False(session.Report.LooksAutomated);
    }

    // MARK: слой 2 — признаки живого человека

    [Fact]
    public void КликБезДвиженияКурсораНеПринимается()
    {
        var session = Session();

        // Ровно то, что делает SendInput: курсор оказывается в точке одним
        // событием, пути нет.
        session.NoteCursor(new ScreenPoint(400, 300));

        var verdict = session.Evaluate(MouseAttempt(session, 2000));

        Assert.Equal(CallGuardRejection.NoCursorMovement, verdict.Rejection);
        Assert.Equal(0, session.Report.CursorTravel);
        Assert.True(session.Report.LooksAutomated);
    }

    [Fact]
    public void ПрыжокКурсораВДвеТочкиНеСчитаетсяДвижением()
    {
        var session = Session();
        session.NoteCursor(new ScreenPoint(0, 0));
        session.NoteCursor(new ScreenPoint(500, 500));

        // Пути много, а движений — одно: длинный прыжок дешевле подделать, чем
        // десяток мелких шагов, поэтому одного порога по длине мало.
        Assert.True(session.Report.CursorTravel > 40);
        Assert.Equal(1, session.Report.CursorSamples);
        Assert.Equal(CallGuardRejection.NoCursorMovement, session.Evaluate(MouseAttempt(session, 2000)).Rejection);
    }

    [Fact]
    public void СинтетическоеНажатиеПоУмолчаниюПроходитНоОстаётсяВОтчёте()
    {
        var session = Session();
        MoveCursorLikeHuman(session);

        var verdict = session.Evaluate(MouseAttempt(session, 2000, isSynthetic: true));

        // Признак подделывается драйвером, поэтому он не барьер. Но у честного
        // оператора он не встречается никогда — значит место ему в телеметрии.
        Assert.True(verdict.IsAccepted);
        Assert.Equal(1, session.Report.Rejections[CallGuardRejection.Synthetic]);
        Assert.True(session.Report.LooksAutomated);
    }

    [Fact]
    public void СоВключённымОтсевомСинтетическоеНажатиеНеПроходит()
    {
        var session = Session(new CallGuardPolicy { RejectsSyntheticEvents = true });
        MoveCursorLikeHuman(session);

        var verdict = session.Evaluate(MouseAttempt(session, 2000, isSynthetic: true));

        Assert.Equal(CallGuardRejection.Synthetic, verdict.Rejection);
    }

    [Fact]
    public void ВыключеннаяЗащитаПринимаетДажеМгновенныйСинтетическийКлик()
    {
        var session = Session(CallGuardPolicy.Disabled);
        var attempt = new CallGuardAttempt('1', IsSynthetic: true, At: Start);

        Assert.True(session.Evaluate(attempt).IsAccepted);

        // Отчёт при этом честно говорит, что защиты не было: по нему видно, что
        // звонок принят без проверок.
        Assert.False(session.Report.WasGuardEnabled);
        Assert.Equal("защита выключена", session.Report.Summary());
    }

    // MARK: отчёт

    [Fact]
    public void ОтчётНакапливаетВсеОтклонённыеПопытки()
    {
        var session = Session(WithDigits);
        var wrong = session.Challenge.Targets.First(target => target != session.Challenge.Answer);

        // Два нажатия без движения курсора, одно мимо цели, и только потом
        // честная попытка живой руки.
        session.Evaluate(MouseAttempt(session, 10));
        session.Evaluate(MouseAttempt(session, 20));
        session.Evaluate(MouseAttempt(session, 2000, wrong));
        MoveCursorLikeHuman(session);
        session.Evaluate(MouseAttempt(session, 2100));

        var report = session.Report;

        Assert.Equal(2, report.Rejections[CallGuardRejection.NoCursorMovement]);
        Assert.Equal(1, report.Rejections[CallGuardRejection.WrongTarget]);
        Assert.Equal(3, report.RejectedAttempts);
        Assert.Equal(2100, report.ReactionMilliseconds);
        Assert.Contains("реакция 2100 мс", report.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void ОтчётПереживаетКодирование()
    {
        var session = Session();
        MoveCursorLikeHuman(session);
        session.Evaluate(MouseAttempt(session, 10));
        session.Evaluate(MouseAttempt(session, 2000));

        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var json = JsonSerializer.Serialize(session.Report, options);
        var restored = JsonSerializer.Deserialize<CallGuardReport>(json, options);

        Assert.Equal(session.Report, restored);
    }
}
