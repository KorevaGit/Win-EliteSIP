using System.Text;
using System.Text.Json;

namespace EliteSIP.PanelLink.Tests;

/// <summary>Файл предустановок.</summary>
public sealed class PresetBundleTests
{
    [Fact]
    public void Файл_от_панели_проходит_проверку_и_разбирается()
    {
        var bundle = PresetBundle.Verified(Fixture.SignedBundle, Fixture.PublicKey);

        Assert.Equal(1, bundle.Format);
        var entry = Assert.Single(bundle.Presets);

        Assert.Equal(Fixture.PresetID, entry.ID);
        Assert.Equal("Менеджер", entry.Name);
        Assert.Equal(12, entry.Revision);
        Assert.Equal(2, entry.SchemaVersion);
    }

    /// <summary>
    /// Ровно то, ради чего линия подписывается: подделанный байт обязан
    /// отвергаться, а не применяться на всех рабочих местах.
    /// </summary>
    [Fact]
    public void Подделанный_байт_ломает_подпись()
    {
        using var envelope = JsonDocument.Parse(Fixture.SignedBundle);
        var payload = envelope.RootElement.GetProperty("payload").GetString()!;
        var signature = envelope.RootElement.GetProperty("signature").GetString()!;

        // Подменяем один знак в полезной нагрузке, оставляя подпись прежней.
        var bytes = payload.ToCharArray();
        bytes[40] = bytes[40] == 'A' ? 'B' : 'A';

        var tampered = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            payload = new string(bytes),
            signature,
        }));

        var error = Assert.Throws<PanelLinkException>(
            () => PresetBundle.Verified(tampered, Fixture.PublicKey));

        Assert.Equal(PanelLinkFailure.SignatureDidNotMatch, error.Failure);
    }

    /// <summary>Файл, подписанный не тем ключом, — это файл не от нашей панели.</summary>
    [Fact]
    public void Чужая_подпись_отвергается()
    {
        var error = Assert.Throws<PanelLinkException>(
            () => PresetBundle.Verified(Fixture.SignedBundle, MachineAccessTests.StrangerKey()));

        Assert.Equal(PanelLinkFailure.SignatureDidNotMatch, error.Failure);
    }

    /// <summary>
    /// Машина ищет себя по идентификатору: имя переименовывают, и поиск по нему
    /// разорвал бы связь у всех машин разом.
    /// </summary>
    [Fact]
    public void Своя_запись_находится_по_идентификатору_а_не_по_имени()
    {
        var bundle = PresetBundle.Verified(Fixture.SignedBundle, Fixture.PublicKey);

        Assert.NotNull(bundle.EntryOf(Fixture.PresetID));
        Assert.Null(bundle.EntryOf("6D1F5A20-0000-4000-8000-000000000002"));
        Assert.Null(bundle.EntryOf("Менеджер"));
    }

    /// <summary>Управляемые поля доносятся неразобранными — их разбирает приложение.</summary>
    [Fact]
    public void Управляемые_поля_доезжают_как_есть()
    {
        var bundle = PresetBundle.Verified(Fixture.SignedBundle, Fixture.PublicKey);
        var entry = Assert.Single(bundle.Presets);

        using var document = JsonDocument.Parse(entry.Fields);
        var addresses = document.RootElement.GetProperty("siteAddresses");

        Assert.Equal("192.168.1.2", addresses.GetProperty("office").GetString());
        Assert.Equal("crm.elitesochi.com", addresses.GetProperty("remote").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("""{"payload":"!!!"}""")]
    public void Мусор_вместо_файла_не_роняет_разбор(string garbage)
    {
        var error = Assert.Throws<PanelLinkException>(
            () => PresetBundle.Verified(Encoding.UTF8.GetBytes(garbage), Fixture.PublicKey));

        Assert.Equal(PanelLinkFailure.MalformedBundle, error.Failure);
    }
}
