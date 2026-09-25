using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace EliteSIP.PanelLink.Tests;

/// <summary>
/// Манифест выпуска.
/// </summary>
///
/// <remarks>
/// Здесь, в отличие от пакета активации и файла предустановок, образцы свои — и
/// это не отступление от правила, а его следствие. Те собирает панель, чужая
/// сторона, и подделать их у себя значило бы проверять согласие с самим собой.
/// Манифест собирает <b>наш же</b> релизный скрипт, и обе стороны здесь наши;
/// настоящая сверка сторон делается в самом скрипте — он после подписи зовёт
/// проверку тем же кодом, которым манифест читает клиент.
/// </remarks>
public sealed class ReleaseManifestTests
{
    private static readonly (PanelPublicKey Public, Ed25519PrivateKeyParameters Private) Pair = MakePair();

    [Fact]
    public void Подписанный_манифест_разбирается()
    {
        var manifest = ReleaseManifest.Verified(Signed(Payload()), Pair.Public);

        Assert.Equal(1, manifest.Format);
        Assert.Equal(new Version(1, 4, 2), manifest.Version);
        Assert.Equal("https://get.elitesip.vip/releases/EliteSIP-1.4.2.exe", manifest.Url.ToString());
        Assert.Equal(64, manifest.Sha256.Length);
        Assert.Equal(9_000_000, manifest.Size);
    }

    /// <summary>
    /// Подделанный манифест обязан отвергаться — ровно ради этого линия и
    /// подписана.
    /// </summary>
    ///
    /// <remarks>
    /// Подменивший манифест на канале подменяет адрес установщика и его
    /// отпечаток разом, то есть подсовывает свой файл как наш выпуск. Подпись —
    /// единственное, что этому мешает: сам установщик лежит там же, где манифест.
    /// </remarks>
    [Fact]
    public void Подделанный_манифест_отвергается()
    {
        var envelope = JsonDocument.Parse(Signed(Payload())).RootElement;
        var payload = Convert.FromBase64String(envelope.GetProperty("payload").GetString()!);

        // Меняем адрес установщика, подпись оставляем прежней.
        var tampered = Encoding.UTF8.GetString(payload)
            .Replace("get.elitesip.vip", "get.elitesip.vlp", StringComparison.Ordinal);

        var forged = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered)),
            signature = envelope.GetProperty("signature").GetString(),
        }));

        var error = Assert.Throws<PanelLinkException>(
            () => ReleaseManifest.Verified(forged, Pair.Public));

        Assert.Equal(PanelLinkFailure.SignatureDidNotMatch, error.Failure);
    }

    [Fact]
    public void Чужой_ключ_не_подходит()
    {
        var error = Assert.Throws<PanelLinkException>(
            () => ReleaseManifest.Verified(Signed(Payload()), MakePair().Public));

        Assert.Equal(PanelLinkFailure.SignatureDidNotMatch, error.Failure);
    }

    /// <summary>
    /// Адрес установщика принимается только по https.
    /// </summary>
    ///
    /// <remarks>
    /// Отпечаток поймал бы подмену и после закачки, но узнавать об этом после
    /// скачивания хуже, чем не начинать: манифест подписан, а вот сам канал по
    /// http подменяется целиком.
    /// </remarks>
    [Fact]
    public void Адрес_не_по_https_отвергается()
    {
        var payload = Payload().Replace("https://", "http://", StringComparison.Ordinal);

        var error = Assert.Throws<PanelLinkException>(
            () => ReleaseManifest.Verified(Signed(payload), Pair.Public));

        Assert.Equal(PanelLinkFailure.MalformedBundle, error.Failure);
    }

    [Theory]
    [InlineData("\"sha256\": \"короткий\"")]
    [InlineData("\"sha256\": \"zzzz1111111111111111111111111111111111111111111111111111111111zz\"")]
    public void Негодный_отпечаток_отвергается_сразу(string replacement)
    {
        var payload = Payload().Replace(
            "\"sha256\": \"" + Digest + "\"", replacement, StringComparison.Ordinal);

        var error = Assert.Throws<PanelLinkException>(
            () => ReleaseManifest.Verified(Signed(payload), Pair.Public));

        Assert.Equal(PanelLinkFailure.MalformedBundle, error.Failure);
    }

    /// <summary>Манифест более новой версии — не отказ, а «нам отсюда нечего взять».</summary>
    [Fact]
    public void Манифест_новой_версии_говорит_об_этом_отдельно()
    {
        var payload = Payload().Replace("\"format\": 1", "\"format\": 9", StringComparison.Ordinal);

        var error = Assert.Throws<PanelLinkException>(
            () => ReleaseManifest.Verified(Signed(payload), Pair.Public));

        Assert.Equal(PanelLinkFailure.BundleTooNew, error.Failure);
    }

    /// <summary>
    /// Равная версия не предлагается: это та же самая сборка.
    /// </summary>
    ///
    /// <remarks>
    /// Откат тоже не предлагается — решение «выпуск плохой» принимает человек, а
    /// линия обновлений умеет только вперёд.
    /// </remarks>
    [Fact]
    public void Обновление_предлагается_только_вперёд()
    {
        var manifest = ReleaseManifest.Verified(Signed(Payload()), Pair.Public);

        Assert.True(manifest.IsNewerThan(new Version(1, 4, 1)));
        Assert.False(manifest.IsNewerThan(new Version(1, 4, 2)));
        Assert.False(manifest.IsNewerThan(new Version(1, 5, 0)));
    }

    [Theory]
    [InlineData("https://get.elitesip.vip/releases/current.json", true)]
    [InlineData("https://GET.elitesip.vip:443/releases/current.json", true)]
    [InlineData("https://update.elitesip.vip:8081/releases/current.json", false)]
    [InlineData("https://get.elitesip.vip:8081/releases/current.json", false)]
    [InlineData("http://get.elitesip.vip/releases/current.json", false)]
    public void Установщик_берётся_только_с_хоста_манифеста(string manifestAddress, bool expected)
    {
        var manifest = ReleaseManifest.Verified(Signed(Payload()), Pair.Public);

        // Пара Basic едет вместе с запросом установщика, и на чужой хост —
        // включая тот же хост на другом порту — её отправлять нельзя. Случай
        // переезда канала: перенесённый манифест смотрит на get.elitesip.vip, а
        // клиент взял его с update.elitesip.vip:8081.
        Assert.Equal(expected, manifest.IsServedFrom(new Uri(manifestAddress)));
    }

    private const string Digest =
        "9f2c4a1b9d3e5f60a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718";

    private static string Payload() =>
        $$"""
        {
          "format": 1,
          "version": "1.4.2",
          "url": "https://get.elitesip.vip/releases/EliteSIP-1.4.2.exe",
          "sha256": "{{Digest}}",
          "size": 9000000,
          "published_at": "2026-09-09T10:00:00Z",
          "notes": "стук по портам и TLS"
        }
        """;

    /// <summary>Конверт, собранный так же, как его собирает релизный скрипт.</summary>
    private static byte[] Signed(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);

        Ed25519Signer signer = new();
        signer.Init(forSigning: true, Pair.Private);
        signer.BlockUpdate(bytes, 0, bytes.Length);

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            payload = Convert.ToBase64String(bytes),
            signature = Convert.ToBase64String(signer.GenerateSignature()),
        }));
    }

    private static (PanelPublicKey Public, Ed25519PrivateKeyParameters Private) MakePair()
    {
        Ed25519PrivateKeyParameters key = new(new SecureRandom());

        return (new PanelPublicKey(key.GeneratePublicKey().GetEncoded()), key);
    }
}
