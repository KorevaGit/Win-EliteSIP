namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Приведение набранного номера.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/DialedNumberTests.swift</c>.
/// </summary>
public sealed class DialedNumberTests
{
    [Fact]
    public void Номер_из_CRM_теряет_оформление_но_не_цифры() =>
        Assert.Equal("79180001122", DialedNumber.Normalized("+7 (918) 000-11-22"));

    [Fact]
    public void Неразрывный_пробел_и_узкий_пробел_вырезаются_наравне_с_обычным() =>
        // Их подкладывают веб-страницы, из которых номер копируют, и на глаз
        // они неотличимы от обычного пробела — а ломают разбор так же.
        Assert.Equal("79180001122", DialedNumber.Normalized("8 918 0001122"));

    [Fact]
    public void Звёздочка_и_решётка_остаются_это_коды_сервисов_АТС()
    {
        Assert.Equal("*97", DialedNumber.Normalized("*97"));
        Assert.Equal("#1", DialedNumber.Normalized("#1"));
    }

    [Fact]
    public void Плюс_сохраняется_только_в_начале()
    {
        Assert.Equal("79180001122", DialedNumber.Normalized("+79180001122"));

        // `8+123` — не международный номер, а опечатка; сохранять в ней плюс
        // значит сохранять опечатку.
        Assert.Equal("8123", DialedNumber.Normalized("8+123"));
        Assert.Equal("+7912", DialedNumber.Normalized("++7912"));
    }

    [Fact]
    public void Буквы_и_знаки_препинания_не_проходят()
    {
        Assert.Equal("172", DialedNumber.Normalized("доб. 172"));
        Assert.Equal("1725", DialedNumber.Normalized("tel:172;ext=5"));
    }

    [Fact]
    public void Пустой_и_один_плюс_звонком_не_считаются()
    {
        Assert.False(DialedNumber.IsDialable(""));
        Assert.False(DialedNumber.IsDialable("   "));
        Assert.False(DialedNumber.IsDialable("+"));
        Assert.False(DialedNumber.IsDialable("(   ) --"));
        Assert.True(DialedNumber.IsDialable("172"));
    }

    [Fact]
    public void Российский_номер_приводится_к_записи_маршрута()
    {
        // Три записи одного номера — один результат. Ровно это оператор и
        // получает из CRM, от клиента и из собственной привычки.
        Assert.Equal("79180001122", DialedNumber.Normalized("+79180001122"));
        Assert.Equal("79180001122", DialedNumber.Normalized("89180001122"));
        Assert.Equal("79180001122", DialedNumber.Normalized("79180001122"));
        Assert.Equal("79180001122", DialedNumber.Normalized("8 (918) 000-11-22"));
    }

    [Fact]
    public void Всё_что_не_российский_номер_правило_не_трогает()
    {
        // Добавочный: три цифры, и восьмёрка в начале ничего не значит.
        Assert.Equal("172", DialedNumber.Normalized("172"));
        Assert.Equal("807", DialedNumber.Normalized("807"));

        // Международный не российский: плюс обязан остаться, иначе номер
        // уедет на межгород как местный.
        Assert.Equal("+12125550123", DialedNumber.Normalized("+12125550123"));

        // Сервисный код АТС.
        Assert.Equal("*97", DialedNumber.Normalized("*97"));

        // Одиннадцать цифр, но код страны не наш — не наше дело.
        Assert.Equal("+49180001122", DialedNumber.Normalized("+49180001122"));

        // `+8…` — опечатка, а не междугородная восьмёрка: подменять в ней код
        // страны значит звонить наугад.
        Assert.Equal("+89180001122", DialedNumber.Normalized("+89180001122"));
    }

    [Theory]
    [InlineData("+7 (918) 000-11-22")]
    [InlineData("8 918 000 11 22")]
    [InlineData("172")]
    [InlineData("*97")]
    [InlineData("+12125550123")]
    public void Нормализация_идемпотентна(string raw)
    {
        // Поле нормализует на каждый ввод символа, то есть применяет правило к
        // уже нормализованному значению снова и снова. Второй проход обязан
        // ничего не менять, иначе номер «уезжает» по мере набора.
        string once = DialedNumber.Normalized(raw);
        Assert.Equal(once, DialedNumber.Normalized(once));
    }
}
