using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace EliteSIP.PanelLink.Tests;

/// <summary>
/// Просьба Spark собрать архив. Конверт тот же, что у доступа и отзыва
/// (<c>preset.SignRaw</c> в Spark), поэтому здесь он подписывается своим ключом
/// по тому же образцу: payload и подпись в base64.
/// </summary>
public sealed class SupportRequestTests
{
    private const string RequestID = "0123456789abcdef0123456789abcdef";

    private static readonly Ed25519PrivateKeyParameters Signer = Generate();

    private static PanelPublicKey PublicKey
        => PanelPublicKey.FromBase64(Convert.ToBase64String(Signer.GeneratePublicKey().GetEncoded()));

    private static Ed25519PrivateKeyParameters Generate()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        return (Ed25519PrivateKeyParameters)generator.GenerateKeyPair().Private;
    }

    private static byte[] Envelope(string payload, Ed25519PrivateKeyParameters? key = null)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var signer = new Ed25519Signer();
        signer.Init(true, key ?? Signer);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return JsonSerializer.SerializeToUtf8Bytes(new { payload = bytes, signature = signer.GenerateSignature() });
    }

    private static string Payload(string installationID = Fixture.InstallationID, string requestID = RequestID)
        => $$"""{"format":1,"installation_id":"{{installationID}}","request_id":"{{requestID}}","requested_at":"2026-11-24T09:00:00Z","expires_at":"2026-11-25T09:00:00Z"}""";

    [Fact]
    public void Подписанный_запрос_разбирается()
    {
        var request = SupportRequest.Verified(Envelope(Payload()), PublicKey, Fixture.InstallationID);

        Assert.Equal(RequestID, request.RequestID);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), request.ExpiresAt);
    }

    /// <summary>По неподписанному объекту журналы не уходят.</summary>
    [Fact]
    public void Чужая_подпись_не_принимается()
        => Assert.Throws<PanelLinkException>(() =>
            SupportRequest.Verified(Envelope(Payload(), Generate()), PublicKey, Fixture.InstallationID));

    [Theory]
    [InlineData("0000000000000000", RequestID)]
    [InlineData(Fixture.InstallationID, "../../etc/passwd")]
    [InlineData(Fixture.InstallationID, "0123456789ABCDEF0123456789ABCDEF")]
    public void Запрос_чужой_машины_или_с_негодным_именем_отвергается(string installationID, string requestID)
        => Assert.Throws<PanelLinkException>(() =>
            SupportRequest.Verified(Envelope(Payload(installationID, requestID)), PublicKey, Fixture.InstallationID));
}
