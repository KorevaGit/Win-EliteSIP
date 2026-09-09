using System.Text.Json;

namespace EliteSIP.PanelLink.Tests;

/// <summary>Пакет активации.</summary>
public sealed class ActivationPackageTests
{
    private static BoundActivationKey RightKey => new(ActivationKey.Parse(Fixture.Key));

    /// <summary>Полный круг с настоящей панелью: то, что она положила, здесь достаётся.</summary>
    [Fact]
    public void Пакет_от_панели_распечатывается()
    {
        var package = ActivationPackage.Open(Fixture.SealedPackage, RightKey);

        Assert.Equal(2, package.Format);
        Assert.Equal(Fixture.InstallationID, package.InstallationID);
        Assert.Equal("Пётр Смирнов", package.Employee);
        Assert.Equal("172", package.Number);
        Assert.Equal("s3cret-172", package.SipPassword);

        // Административного пароля в пакете нет вовсе: он приезжает помашинным
        // объектом MachineAccess, чтобы у него не было двух источников,
        // расходящихся при первой же смене.
        Assert.Equal(64, package.ChannelKey.Length);

        Assert.Equal(Fixture.PresetID, package.Preset.ID);
        Assert.Equal("Менеджер", package.Preset.Name);
        Assert.Equal(7, package.Preset.Revision);
        Assert.Equal(2, package.Preset.SchemaVersion);
    }

    /// <summary>
    /// Управляемые поля доносятся в целости и неразобранными: разбирает их та же
    /// дорога, что и файл предустановок.
    /// </summary>
    [Fact]
    public void Управляемые_поля_доезжают_как_есть()
    {
        var package = ActivationPackage.Open(Fixture.SealedPackage, RightKey);

        using var document = JsonDocument.Parse(package.Preset.Settings);
        var dtmf = document.RootElement.GetProperty("dtmf");
        Assert.Equal(120, dtmf.GetProperty("toneMilliseconds").GetInt32());

        var macros = dtmf.GetProperty("macros");
        Assert.Equal(1, macros.GetArrayLength());
        Assert.Equal("ЮРИСТ", macros[0].GetProperty("title").GetString());
        Assert.Equal("*02,101", macros[0].GetProperty("sequence").GetString());
        Assert.True(macros[0].GetProperty("transfersCall").GetBoolean());
    }

    /// <summary>
    /// Подбирающему незачем знать, ошибся он ключом или наткнулся на битый файл:
    /// ответ обязан быть один и тот же.
    /// </summary>
    [Fact]
    public void Чужой_ключ_и_испорченный_пакет_неотличимы_по_ответу()
    {
        BoundActivationKey wrongKey = new(ActivationKey.Parse("K7M29XQP4TFC"));
        var onWrongKey = Assert.Throws<PanelLinkException>(
            () => ActivationPackage.Open(Fixture.SealedPackage, wrongKey));

        var broken = Fixture.SealedPackage;
        broken[^1] ^= 0x01;
        var onBrokenPackage = Assert.Throws<PanelLinkException>(
            () => ActivationPackage.Open(broken, RightKey));

        Assert.Equal(PanelLinkFailure.KeyDidNotOpen, onWrongKey.Failure);
        Assert.Equal(PanelLinkFailure.KeyDidNotOpen, onBrokenPackage.Failure);
        Assert.Equal(onWrongKey.Message, onBrokenPackage.Message);
    }

    /// <summary>
    /// Подменённый заголовок обязан ломать проверку: он идёт в дополнительные
    /// данные AES-GCM именно за этим.
    /// </summary>
    [Fact]
    public void Подмена_заголовка_ломает_распечатывание()
    {
        var tampered = Fixture.SealedPackage;
        tampered[4] = (byte)'X';

        var error = Assert.Throws<PanelLinkException>(
            () => ActivationPackage.Open(tampered, RightKey));

        Assert.Equal(PanelLinkFailure.KeyDidNotOpen, error.Failure);
    }

    /// <summary>
    /// Иначе разбор уйдёт не туда: человек будет искать опечатку в ключе,
    /// которого не набирал, вместо того чтобы обновить приложение.
    /// </summary>
    [Fact]
    public void Пакет_более_новой_версии_отвечает_отдельно()
    {
        var newer = Fixture.SealedPackage;
        newer[5] = (byte)'9';

        var error = Assert.Throws<PanelLinkException>(
            () => ActivationPackage.Open(newer, RightKey));

        Assert.Equal(PanelLinkFailure.PackageTooNew, error.Failure);
    }

    /// <summary>
    /// А пакет более старой версии — нет: обновлять приложение в этом случае
    /// незачем, отстала панель.
    /// </summary>
    [Fact]
    public void Пакет_старой_версии_не_зовёт_обновляться()
    {
        var older = Fixture.SealedPackage;
        older[5] = (byte)'1';

        var error = Assert.Throws<PanelLinkException>(
            () => ActivationPackage.Open(older, RightKey));

        Assert.Equal(PanelLinkFailure.KeyDidNotOpen, error.Failure);
    }

    /// <summary>
    /// Привязка входит в соль, значит и в ключ шифрования: даже добравшись до
    /// пакета, чужая машина его не откроет.
    /// </summary>
    [Fact]
    public void Пакет_без_привязки_не_открывается_привязанным_ключом()
    {
        BoundActivationKey bound = new(ActivationKey.Parse(Fixture.Key), Fixture.InstallationID);

        var error = Assert.Throws<PanelLinkException>(
            () => ActivationPackage.Open(Fixture.SealedPackage, bound));

        Assert.Equal(PanelLinkFailure.KeyDidNotOpen, error.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(20)]
    public void Обрубок_не_роняет_разбор(int length)
    {
        var truncated = Fixture.SealedPackage[..length];

        Assert.Throws<PanelLinkException>(() => ActivationPackage.Open(truncated, RightKey));
    }
}
