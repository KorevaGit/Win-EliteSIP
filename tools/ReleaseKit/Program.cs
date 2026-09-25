using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteSIP.PanelLink;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

// Инструмент выпуска: ключи линии, подпись манифеста и его проверка.
//
// Отдельной программой, а не куском релизного скрипта, по двум причинам. Первая:
// подписывать надо Ed25519, а PowerShell его не умеет — пришлось бы тащить в
// скрипт то же самое, только неудобнее. Вторая важнее: проверка после подписи
// делается кодом клиента (`ReleaseManifest.Verified`), и вот это невозможно
// повторить в скрипте вовсе. Скрипт, который проверяет свой манифест своей же
// реализацией, проверяет согласие с самим собой.
//
// Закрытый ключ линии в репозиторий не кладётся никогда — см. `keygen`.

// Вывод в UTF-8 явно: консоль Windows по умолчанию отдаёт вывод в кодовой
// странице 866 или 1251, и русские строки в Git Bash или в журнале сборки
// превращаются в мусор — то есть ровно там, где их и читают.
Console.OutputEncoding = Encoding.UTF8;

return args switch
{
    ["keygen", var directory] => Keygen(directory),
    ["sign", var manifest, var key, var output] => Sign(manifest, key, output),
    ["verify", var envelope, var publicKey] => Verify(envelope, publicKey),
    ["hash", var file] => Hash(file),
    ["public", var key] => PublicOf(key),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        releasekit — подпись линии выпусков.

          keygen <каталог>                     новая пара ключей линии
          sign <манифест.json> <ключ> <выход>  подписать манифест
          verify <конверт.json> <ключ.pub>     проверить кодом клиента
          hash <файл>                          SHA-256 файла, шестнадцатеричный
          public <ключ>                        открытая половина ключа, base64

        Ключ подписи — файл с закрытым ключом Ed25519 в base64, 32 байта.
        В репозиторий он не кладётся: у него одно место — там же, где пара
        авторизации канала, и только у того, кто выпускает.
        """);

    return 2;
}

static int Keygen(string directory)
{
    Directory.CreateDirectory(directory);

    Ed25519PrivateKeyParameters key = new(new SecureRandom());

    var privatePath = Path.Combine(directory, "releases.key");
    var publicPath = Path.Combine(directory, "releases.pub");

    File.WriteAllText(privatePath, Convert.ToBase64String(key.GetEncoded()));
    File.WriteAllText(publicPath, Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));

    Console.WriteLine($"закрытый ключ: {privatePath}");
    Console.WriteLine($"открытый ключ: {publicPath}");
    Console.WriteLine();
    Console.WriteLine("Открытый вписывается в provisioning.json полем presetsPublicKey —");
    Console.WriteLine("тем же, которым подписаны предустановки: линия одна.");
    Console.WriteLine("Закрытый не коммитить. Потеря закрытого означает, что уже");
    Console.WriteLine("установленные машины перестанут принимать выпуски вовсе.");

    return 0;
}

/// <summary>Открытая половина закрытого ключа — чтобы сверить её с ключом клиента.</summary>
static int PublicOf(string keyPath)
{
    var key = new Ed25519PrivateKeyParameters(
        Convert.FromBase64String(File.ReadAllText(keyPath).Trim()), 0);

    Console.WriteLine(Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));
    return 0;
}

static int Sign(string manifestPath, string keyPath, string outputPath)
{
    var payload = File.ReadAllBytes(manifestPath);
    var key = new Ed25519PrivateKeyParameters(
        Convert.FromBase64String(File.ReadAllText(keyPath).Trim()), 0);

    Ed25519Signer signer = new();
    signer.Init(forSigning: true, key);
    signer.BlockUpdate(payload, 0, payload.Length);

    // Тот же конверт, что у предустановок и помашинных объектов: подпись
    // считается по байтам содержимого, а само содержимое едет рядом как есть.
    // Так проверка не зависит ни от канонизации JSON, ни от того, как посредник
    // переставит ключи.
    var envelope = JsonSerializer.Serialize(new
    {
        payload = Convert.ToBase64String(payload),
        signature = Convert.ToBase64String(signer.GenerateSignature()),
    });

    File.WriteAllText(outputPath, envelope);
    Console.WriteLine($"подписано: {outputPath}");

    return 0;
}

static int Verify(string envelopePath, string publicKeyPath)
{
    var data = File.ReadAllBytes(envelopePath);
    var publicKey = PanelPublicKey.FromBase64(File.ReadAllText(publicKeyPath).Trim());

    try
    {
        var manifest = ReleaseManifest.Verified(data, publicKey);

        Console.WriteLine($"версия:    {manifest.Version.ToString(3)}");
        Console.WriteLine($"установщик: {manifest.Url}");
        Console.WriteLine($"отпечаток: {manifest.Sha256}");
        Console.WriteLine($"размер:    {manifest.Size.ToString("N0", CultureInfo.CurrentCulture)} байт");
        Console.WriteLine("проверено кодом клиента.");

        return 0;
    }
    catch (PanelLinkException error)
    {
        Console.Error.WriteLine($"манифест не прошёл проверку: {error.Message}");

        return 1;
    }
}

static int Hash(string path)
{
    using var file = File.OpenRead(path);

    Console.WriteLine(Convert.ToHexString(SHA256.HashData(file)));

    return 0;
}
