using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace EliteSIP.PanelLink.Tests;

/// <summary>Помашинный доступ.</summary>
public sealed class MachineAccessTests
{
    [Fact]
    public void Объект_от_панели_проходит_проверку_и_разбирается()
    {
        var access = MachineAccess.Verified(
            Fixture.MachineAccessEnvelope, Fixture.PublicKey, Fixture.InstallationID);

        Assert.Equal(Fixture.InstallationID, access.InstallationID);
        Assert.Equal(Fixture.PresetID, access.PresetID);
        Assert.Equal("пароль-предустановки", access.AdminPassword);
    }

    /// <summary>
    /// Подписанный объект чужой машины — это чужой административный пароль.
    /// Принимать его молча нельзя, даже если канал его почему-то отдал.
    /// </summary>
    [Fact]
    public void Чужой_доступ_не_принимается_хотя_подпись_сходится()
    {
        var error = Assert.Throws<PanelLinkException>(() => MachineAccess.Verified(
            Fixture.MachineAccessEnvelope, Fixture.PublicKey, "0000000000000000"));

        Assert.Equal(PanelLinkFailure.MalformedBundle, error.Failure);
    }

    [Fact]
    public void Подделанный_байт_ломает_проверку()
    {
        var broken = Fixture.MachineAccessEnvelope;
        broken[^20] ^= 0x01;

        Assert.Throws<PanelLinkException>(() => MachineAccess.Verified(
            broken, Fixture.PublicKey, Fixture.InstallationID));
    }

    [Fact]
    public void Чужой_ключ_подписи_не_подходит()
    {
        var error = Assert.Throws<PanelLinkException>(() => MachineAccess.Verified(
            Fixture.MachineAccessEnvelope, StrangerKey(), Fixture.InstallationID));

        Assert.Equal(PanelLinkFailure.SignatureDidNotMatch, error.Failure);
    }

    /// <summary>Ключ чужой панели: настоящая пара Ed25519, просто не наша.</summary>
    internal static PanelPublicKey StrangerKey()
    {
        Ed25519PrivateKeyParameters stranger = new(new SecureRandom());

        return new PanelPublicKey(stranger.GeneratePublicKey().GetEncoded());
    }
}

/// <summary>Отзыв.</summary>
public sealed class RevocationTests
{
    [Fact]
    public void Отзыв_от_панели_проходит_проверку()
    {
        var revocation = Revocation.Verified(
            Fixture.RevocationEnvelope, Fixture.PublicKey, Fixture.InstallationID);

        Assert.Equal(Fixture.InstallationID, revocation.InstallationID);
    }

    /// <summary>
    /// Сброс запускает только подписанный отзыв. Неподписанный объект — это то,
    /// что подсунет любой, кто дотянется до бакета или до сети между.
    /// </summary>
    [Fact]
    public void Неподписанное_не_сбрасывает_машину()
    {
        var bare = Encoding.UTF8.GetBytes(
            """{"format":1,"installation_id":"8f2c4a1b9d3e5f60","revoked_at":"2026-08-25T12:00:00Z"}""");

        Assert.Throws<PanelLinkException>(() => Revocation.Verified(
            bare, Fixture.PublicKey, Fixture.InstallationID));
    }

    /// <summary>Иначе подсунутый объект соседней машины сбрасывал бы эту.</summary>
    [Fact]
    public void Отзыв_чужой_машины_не_сбрасывает_нашу()
    {
        var error = Assert.Throws<PanelLinkException>(() => Revocation.Verified(
            Fixture.RevocationEnvelope, Fixture.PublicKey, "0000000000000000"));

        Assert.Equal(PanelLinkFailure.MalformedBundle, error.Failure);
    }
}
