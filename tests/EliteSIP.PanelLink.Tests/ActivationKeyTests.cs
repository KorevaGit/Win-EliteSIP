namespace EliteSIP.PanelLink.Tests;

/// <summary>Ключ активации.</summary>
public sealed class ActivationKeyTests
{
    /// <summary>
    /// Ключ диктуют по телефону и вставляют из мессенджера вместе с пробелами.
    /// </summary>
    [Fact]
    public void Разбор_терпим_к_тому_как_ключ_ввели()
    {
        var canonical = ActivationKey.Parse("K7M29XQP4TFB").Canonical;

        // Список нарочно повторяет список оригинала — вместе с парой CR LF, на
        // которой первый заход там споткнулся: ключ из мессенджера на Windows
        // отвергался как непохожий на ключ.
        string[] written =
        [
            "K7M2-9XQP-4TFB",
            "k7m2 9xqp 4tfb",
            " K7M2\n9XQP\t4TFB ",
            "K7M2-9XQP-4TFB\r\n",
            "k7m2.9xqp,4tfb",
            "K7M2 9XQP—4TFB",
            "«K7M2-9XQP-4TFB»",
        ];

        foreach (var variant in written)
        {
            Assert.Equal(canonical, ActivationKey.Parse(variant).Canonical);
        }
    }

    /// <summary>
    /// Этих букв в алфавите нет вовсе, и прочитавший ноль как «о» иначе получал
    /// бы отказ, не понимая почему.
    /// </summary>
    [Fact]
    public void O_читается_нулём_I_и_L_единицей()
    {
        Assert.Equal("07M29XQP4TFB", ActivationKey.Parse("O7M29XQP4TFB").Canonical);
        Assert.Equal("17M29XQP4TFB", ActivationKey.Parse("I7M29XQP4TFB").Canonical);
        Assert.Equal("17M29XQP4TFB", ActivationKey.Parse("L7M29XQP4TFB").Canonical);
    }

    [Theory]
    [InlineData("K7M29XQP4TF")]
    [InlineData("K7M29XQP4TFBX")]
    [InlineData("K7M29XQP4TF!")]
    [InlineData("")]
    public void Не_ключ_отвергается_по_составу_и_по_длине(string input)
    {
        var error = Assert.Throws<PanelLinkException>(() => ActivationKey.Parse(input));

        Assert.Equal(PanelLinkFailure.MalformedKey, error.Failure);
    }

    /// <summary>
    /// Адрес считают обе стороны, и совпасть они обязаны до знака: иначе машина
    /// пойдёт за пакетом не туда.
    /// </summary>
    [Fact]
    public void Адрес_пакета_совпадает_с_тем_что_посчитала_панель()
    {
        BoundActivationKey bound = new(ActivationKey.Parse(Fixture.Key));

        Assert.Equal(Fixture.ObjectName, bound.ObjectName);
    }

    /// <summary>
    /// Привязка входит в соль, а значит и в адрес: ключ перепрошивки, введённый
    /// не на той машине, уходит по другому адресу и пакета там не находит —
    /// вместо того чтобы скачать его и сжечь на проверке внутри.
    /// </summary>
    [Fact]
    public void Привязка_к_машине_меняет_адрес()
    {
        var key = ActivationKey.Parse(Fixture.Key);

        BoundActivationKey free = new(key);
        BoundActivationKey mine = new(key, "8f2c0000");
        BoundActivationKey yours = new(key, "8f2c0001");

        Assert.NotEqual(free.ObjectName, mine.ObjectName);
        Assert.NotEqual(yours.ObjectName, mine.ObjectName);
        Assert.Equal(mine.ObjectName, new BoundActivationKey(key, "8f2c0000").ObjectName);
    }

    [Fact]
    public void Показывается_группами_как_в_панели()
    {
        Assert.Equal("K7M2-9XQP-4TFB", ActivationKey.Parse("K7M29XQP4TFB").Grouped);
    }
}
