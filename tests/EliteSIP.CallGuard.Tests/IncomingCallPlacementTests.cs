using EliteSIP.CallGuard;

namespace EliteSIP.CallGuard.Tests;

/// <summary>
/// Первый слой защиты: окно не должно появляться дважды в одной точке.
/// </summary>
///
/// <remarks>
/// До переноса у этой логики в оригинале не было ни одного теста, хотя ломается
/// об неё самый частый инструмент — кликер по фиксированным координатам.
/// </remarks>
public sealed class IncomingCallPlacementTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1040);
    private static readonly ScreenSize Panel = new(320, 212);

    private static IncomingCallPlacement Placement(double minimumTravel = 150)
        => new(Screen.Inset(24), minimumTravel);

    [Fact]
    public void ОкноВсегдаОстаётсяВнутриРазрешённойОбласти()
    {
        var placement = Placement();
        var generator = new Random(1);
        ScreenPoint? previous = null;

        for (var attempt = 0; attempt < 500; attempt++)
        {
            var origin = placement.Origin(Panel, previous, generator);
            var frame = new ScreenRect(origin, Panel);

            Assert.True(placement.Bounds.Contains(frame), $"окно вылезло за отступ: {frame}");
            previous = origin;
        }
    }

    [Fact]
    public void ПозицииНеПовторяютсяИЗаметноРасходятся()
    {
        var placement = Placement();
        var generator = new Random(2);
        var origins = new List<ScreenPoint>();
        ScreenPoint? previous = null;

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var origin = placement.Origin(Panel, previous, generator);
            origins.Add(origin);
            previous = origin;
        }

        Assert.True(
            origins.Select(origin => origin.X).Distinct().Count() > 150,
            "координаты почти не меняются — кликер по точке снова работает");

        // Требование по смещению выполняется не «в среднем», а на каждом шаге:
        // одной повторной позиции достаточно, чтобы мышечная память вернулась.
        var travels = origins.Zip(origins.Skip(1), (from, to) => to.DistanceTo(from));
        Assert.All(travels, travel => Assert.True(travel >= 150));
    }

    [Fact]
    public void ТеснаяОбластьНеЗаставляетОкноВылезтиЗаКрай()
    {
        // Экран меньше окна: соблюсти смещение невозможно, и единственное
        // правильное поведение — прижаться к углу, а не уехать за границу.
        var tight = new IncomingCallPlacement(new ScreenRect(10, 10, 200, 100), minimumTravel: 500);
        var origin = tight.Origin(Panel, new ScreenPoint(10, 10), new Random(3));

        Assert.Equal(new ScreenPoint(10, 10), origin);
    }

    [Fact]
    public void НедостижимоеТребованиеПоСмещениюНеЗацикливаетРасчёт()
    {
        // Смещение больше диагонали области: цикл обязан сдаться после
        // ограниченного числа попыток и взять лучшую из них.
        var placement = Placement(minimumTravel: 10_000);
        var origin = placement.Origin(Panel, new ScreenPoint(100, 100), new Random(4));

        Assert.True(placement.Bounds.Contains(new ScreenRect(origin, Panel)));
    }

    [Fact]
    public void ОкноВыросшееПослеРазмещенияВозвращаетсяВнутрьОбласти()
    {
        var placement = Placement();
        var generator = new Random(5);
        ScreenPoint? previous = null;

        for (var attempt = 0; attempt < 500; attempt++)
        {
            // Позицию выбирали, пока окно было высотой в одну точку: настоящую
            // высоту ему считает содержимое, и приехать она может позже. Ровно
            // так окно и уезжало за нижний край.
            var chosen = placement.Origin(new ScreenSize(Panel.Width, 1), previous, generator);
            previous = chosen;

            var corrected = placement.Contained(new ScreenRect(chosen, Panel));

            Assert.True(placement.Bounds.Contains(corrected), $"окно осталось за краем: {corrected}");
            Assert.Equal(Panel, corrected.Size);
        }
    }

    [Fact]
    public void РамкеВнутриОбластиСдвигНеНужен()
    {
        var placement = Placement();
        var inside = new ScreenRect(300, 300, Panel.Width, Panel.Height);

        Assert.Equal(inside, placement.Contained(inside));
    }

    [Fact]
    public void ОкноКрупнееОбластиПрижимаетсяКУглуАНеРазъезжается()
    {
        var tight = new IncomingCallPlacement(new ScreenRect(10, 10, 200, 100), minimumTravel: 0);
        var corrected = tight.Contained(new ScreenRect(500, 500, 340, 228));

        Assert.Equal(new ScreenPoint(10, 10), corrected.Origin);
    }

    [Fact]
    public void ОдинИТотЖеГенераторДаётОднуИТуЖеПозицию()
    {
        var placement = Placement();

        Assert.Equal(
            placement.Origin(Panel, previous: null, new Random(11)),
            placement.Origin(Panel, previous: null, new Random(11)));
    }
}
