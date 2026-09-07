using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Баланс отсчётов — проверка, оплаченная двумя неделями замеров W0.
///
/// Ловится ей один класс ошибок: отсчёты, которые тихо теряются или тихо
/// изобретаются по дороге. На коротком прогоне это не слышно, на длинном
/// выглядит как уход часов, а на самом деле — щелчки сто раз в секунду.
/// </summary>
public sealed class SampleBalanceTests
{
    // 48 кГц устройства → 8 кГц кодека, кадр 20 мс, задержка ядра пересчёта.
    private const double Ratio = 8000.0 / 48000.0;
    private const int SamplesPerFrame = 160;
    private const int Allowance = 16;

    [Fact]
    public void Исправный_тракт_сходится()
    {
        SampleBalance balance = New();

        // Секунда разговора: сто пакетов по 10 мс с устройства, из них
        // пятьдесят кадров по 20 мс в сеть.
        for (int packet = 0; packet < 100; packet++)
        {
            balance.NoteCaptured(480);
            balance.NoteConverted(80);
            if (packet % 2 == 1)
            {
                balance.NoteEncodedFrame();
            }
        }

        Assert.True(balance.IsBalanced, balance.Summary);
        Assert.Equal(0, balance.ConversionDiscrepancy);
        Assert.Equal(0, balance.Pending);
    }

    [Fact]
    public void Потерянные_отсчёты_видны()
    {
        // Ровно та ошибка из W0: из кольца читается больше, чем выводится, и
        // лишнее выбрасывается. Здесь это выглядит как пересчёт, который отдаёт
        // меньше положенного.
        SampleBalance balance = New();

        for (int packet = 0; packet < 100; packet++)
        {
            balance.NoteCaptured(480);
            balance.NoteConverted(78); // два отсчёта на такте пропали
        }

        Assert.False(balance.IsBalanced, balance.Summary);
        Assert.Equal(-200, balance.ConversionDiscrepancy);
    }

    [Fact]
    public void Изобретённые_отсчёты_видны()
    {
        // Обратная ошибка и более коварная: лишние отсчёты не щёлкают, а тихо
        // добавляют задержку — по два отсчёта на такт это четверть секунды за
        // минуту разговора.
        SampleBalance balance = New();

        for (int packet = 0; packet < 100; packet++)
        {
            balance.NoteCaptured(480);
            balance.NoteConverted(82);
        }

        Assert.False(balance.IsBalanced, balance.Summary);
        Assert.Equal(200, balance.ConversionDiscrepancy);
    }

    [Fact]
    public void Задержка_ядра_пересчёта_не_считается_ошибкой()
    {
        // Часть отсчётов законно висит внутри фильтра. Без поправки на это
        // исправный тракт объявлялся бы разошедшимся на первой же секунде — то
        // есть проверка сработала бы всегда и потому не значила бы ничего.
        SampleBalance balance = New();

        // Отсчёты идут пакетами по 10 мс, как их отдаёт устройство. Первый
        // пакет отдаёт меньше остальных: часть отсчётов осела внутри фильтра и
        // выйдет из него только к следующему.
        for (int packet = 0; packet < 100; packet++)
        {
            balance.NoteCaptured(480);
            balance.NoteConverted(packet == 0 ? 80 - Allowance : 80);

            // Кодер забирает кадр, когда он набрался, — как в тракте, а не по
            // счёту пакетов.
            while (balance.Pending >= SamplesPerFrame)
            {
                balance.NoteEncodedFrame();
            }
        }

        Assert.Equal(-Allowance, balance.ConversionDiscrepancy);
        Assert.True(balance.IsBalanced, balance.Summary);
    }

    [Fact]
    public void Отсчёты_не_доезжающие_до_сети_видны_по_росту_остатка()
    {
        // Пересчёт работает, кодер отстаёт: отсчёты приходят, кадры не уходят.
        // Мгновенное расхождение здесь нулевое, и поймать это можно только по
        // тому, что остаток растёт.
        SampleBalance balance = New();

        for (int packet = 0; packet < 100; packet++)
        {
            balance.NoteCaptured(480);
            balance.NoteConverted(80);
            if (packet % 4 == 3)
            {
                balance.NoteEncodedFrame();
            }
        }

        Assert.Equal(0, balance.ConversionDiscrepancy);
        Assert.False(balance.IsBalanced, balance.Summary);
        Assert.True(balance.MaximumPending >= SamplesPerFrame * 2, balance.Summary);
    }

    [Fact]
    public void Остаток_держится_в_пределах_кадра_на_исправном_тракте()
    {
        SampleBalance balance = New();

        for (int packet = 0; packet < 1000; packet++)
        {
            balance.NoteCaptured(480);
            balance.NoteConverted(80);
            if (packet % 2 == 1)
            {
                balance.NoteEncodedFrame();
            }
        }

        Assert.True(balance.MaximumPending < SamplesPerFrame * 2, balance.Summary);
        Assert.True(balance.IsBalanced, balance.Summary);
    }

    [Fact]
    public void Кадров_больше_чем_отсчётов_это_отрицательный_остаток()
    {
        // Кодер отдал в сеть больше, чем получил. Взять это неоткуда, и молчать
        // о таком нельзя даже при сходящемся расхождении пересчёта.
        SampleBalance balance = New();
        balance.NoteCaptured(480);
        balance.NoteConverted(80);
        balance.NoteEncodedFrame();

        Assert.True(balance.Pending < 0);
        Assert.False(balance.IsBalanced, balance.Summary);
    }

    [Fact]
    public void Сводка_печатает_и_приговор_и_числа()
    {
        // Урок W0: приговор стенда был неверен ровно на лучшем результате, и
        // заметить это можно было только сверив его с таблицей рядом. Поэтому
        // числа в строке обязательны.
        SampleBalance balance = New();
        balance.NoteCaptured(480);
        balance.NoteConverted(80);

        string summary = balance.Summary;
        Assert.Contains("сходится", summary, StringComparison.Ordinal);
        Assert.Contains("480", summary, StringComparison.Ordinal);
        Assert.Contains("80", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Сброс_обнуляет_счёт()
    {
        SampleBalance balance = New();
        balance.NoteCaptured(480);
        balance.NoteConverted(200);
        balance.Reset();

        Assert.Equal(0, balance.Captured);
        Assert.Equal(0, balance.Converted);
        Assert.Equal(0, balance.MaximumPending);
        Assert.True(balance.IsBalanced);
    }

    private static SampleBalance New() => new(Ratio, SamplesPerFrame, Allowance);
}
