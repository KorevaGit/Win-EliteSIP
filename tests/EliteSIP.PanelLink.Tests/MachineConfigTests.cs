using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace EliteSIP.PanelLink.Tests;

/// <summary>
/// Конфигурация машины: подпись, своя машина, расшифровка своим ключом.
/// </summary>
///
/// <remarks>
/// <para>
/// Запечатывает здесь <see cref="Spark"/> — повтор <c>SealForMachine</c> и
/// <c>sign</c> из <c>spark.elitesochi.com/backend/internal/elitesip</c>
/// целиком на BouncyCastle: свой X25519, свой HKDF, свой GCM. Клиент открывает
/// теми же формулами, но другой реализацией (<c>HKDF</c> и <c>AesGcm</c> из
/// .NET), так что расхождение в любом шаге — порядке соли, строке вывода,
/// дополнительных данных — роняет проверку.
/// </para>
/// <para>
/// Это не замена вектору <c>ConfigFixture</c> из macOS-репозитория (собран
/// настоящим кодом Spark на Go): его стоит добавить, как только он окажется
/// под рукой.
/// </para>
/// </remarks>
public sealed class MachineConfigTests
{
    private const string InstallationID = "4def1c28841b17be078d88f914636d04";

    [Fact]
    public void Конфигурация_открывается_своим_ключом_и_даёт_все_поля()
    {
        var machine = MachineKeyPair.Generate();
        var spark = new Spark();

        var config = MachineConfig.Open(
            spark.Config(machine.PublicKey, InstallationID, revision: 3, Content()),
            spark.PublicKey,
            InstallationID,
            machine);

        Assert.Equal(InstallationID, config.InstallationID);
        Assert.Equal(3, config.Revision);
        Assert.Equal("Смирнов П.", config.Employee);
        Assert.Equal("172", config.Number);
        Assert.Equal("пароль-sip", config.SipPassword);
        Assert.Equal("office", config.WorkFormat);
        Assert.Equal("preset-1", config.PresetID);
        Assert.Equal("Менеджер", config.PresetName);
        Assert.Equal("", config.AdminPassword);
        Assert.Empty(config.Lines);
    }

    [Fact]
    public void Несколько_номеров_приходят_списком_основной_первым()
    {
        var machine = MachineKeyPair.Generate();
        var spark = new Spark();

        var content = Content() with
        {
            lines =
            [
                new { id = "main", number = "300", sip_password = "a", label = "Колл-центр" },
                new { id = "line-17", number = "301", sip_password = "b", label = "Линия 2" },
            ],
        };

        var config = MachineConfig.Open(
            spark.Config(machine.PublicKey, InstallationID, 4, content), spark.PublicKey, InstallationID, machine);

        Assert.Equal(2, config.Lines.Count);
        Assert.Equal(new MachineConfig.Line("main", "300", "a", "Колл-центр"), config.Lines[0]);
        Assert.Equal("line-17", config.Lines[1].ID);
    }

    [Fact]
    public void Чужой_ключ_машины_конфигурацию_не_открывает()
    {
        var spark = new Spark();
        var data = spark.Config(MachineKeyPair.Generate().PublicKey, InstallationID, 1, Content());

        var error = Assert.Throws<PanelLinkException>(
            () => MachineConfig.Open(data, spark.PublicKey, InstallationID, MachineKeyPair.Generate()));

        Assert.Equal(PanelLinkFailure.KeyDidNotOpen, error.Failure);
    }

    [Fact]
    public void Конфигурация_чужой_машины_отбрасывается_хотя_подпись_верна()
    {
        var machine = MachineKeyPair.Generate();
        var spark = new Spark();
        var data = spark.Config(machine.PublicKey, InstallationID, 1, Content());

        var error = Assert.Throws<PanelLinkException>(
            () => MachineConfig.Open(data, spark.PublicKey, "00000000000000000000000000000000", machine));

        Assert.Equal(PanelLinkFailure.MalformedBundle, error.Failure);
    }

    [Fact]
    public void Чужая_подпись_отбрасывается()
    {
        var machine = MachineKeyPair.Generate();
        var data = new Spark().Config(machine.PublicKey, InstallationID, 1, Content());

        var error = Assert.Throws<PanelLinkException>(
            () => MachineConfig.Open(data, new Spark().PublicKey, InstallationID, machine));

        Assert.Equal(PanelLinkFailure.SignatureDidNotMatch, error.Failure);
    }

    [Fact]
    public void Формат_новее_поддерживаемого_не_применяется()
    {
        var machine = MachineKeyPair.Generate();
        var spark = new Spark();
        var data = spark.Config(machine.PublicKey, InstallationID, 1, Content(), format: 2);

        var error = Assert.Throws<PanelLinkException>(
            () => MachineConfig.Open(data, spark.PublicKey, InstallationID, machine));

        Assert.Equal(PanelLinkFailure.BundleTooNew, error.Failure);
    }

    [Fact]
    public void Неизвестные_поля_не_мешают()
    {
        var machine = MachineKeyPair.Generate();
        var spark = new Spark();

        var config = MachineConfig.Open(
            spark.Config(machine.PublicKey, InstallationID, 1, new { number = "205", future_field = 7 }),
            spark.PublicKey,
            InstallationID,
            machine);

        Assert.Equal("205", config.Number);
    }

    [Fact]
    public void Ключ_машины_сохраняется_и_восстанавливается()
    {
        var machine = MachineKeyPair.Generate();
        var restored = MachineKeyPair.FromBase64(machine.PrivateKeyBase64);

        Assert.Equal(machine.PublicKey, restored.PublicKey);
        Assert.Equal(32, machine.PublicKey.Length);
    }

    [Fact]
    public void Ключ_канала_строкой_base64url_и_хеш_hex()
    {
        var key = ChannelKey.Generate();

        Assert.Equal(43, key.Length);
        Assert.DoesNotContain('=', key);
        Assert.DoesNotContain('+', key);
        Assert.DoesNotContain('/', key);

        // Вектор SHA-256 от строки: так хеш считает и сверяет Spark.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ChannelKey.Hash("abc"));
    }

    private static ConfigContent Content() => new(
        employee: "Смирнов П.",
        number: "172",
        sip_password: "пароль-sip",
        work_format: "office",
        preset_id: "preset-1",
        preset_name: "Менеджер",
        admin_password: "",
        lines: null);

    // Имена полей — как в JSON Spark (machineConfig).
#pragma warning disable IDE1006, SA1300
    private sealed record ConfigContent(
        string employee,
        string number,
        string sip_password,
        string work_format,
        string preset_id,
        string preset_name,
        string admin_password,
        object[]? lines);
#pragma warning restore IDE1006, SA1300

    /// <summary>Сторона Spark: ключ подписи и запечатывание под машину.</summary>
    private sealed class Spark
    {
        private readonly Ed25519PrivateKeyParameters _signing = new(new SecureRandom());

        public PanelPublicKey PublicKey => new(_signing.GeneratePublicKey().GetEncoded());

        public byte[] Config(byte[] machinePublic, string installationID, int revision, object content, int format = 1)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(content);
            var sealedBytes = Seal(machinePublic, plain);

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format,
                installation_id = installationID,
                revision,
                issued_at = "2026-09-26T10:01:50Z",
                @sealed = Convert.ToBase64String(sealedBytes),
            });

            var signer = new Ed25519Signer();
            signer.Init(forSigning: true, _signing);
            signer.BlockUpdate(payload, 0, payload.Length);

            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload = Convert.ToBase64String(payload),
                signature = Convert.ToBase64String(signer.GenerateSignature()),
            });
        }

        /// <summary><c>SealForMachine</c>: эфемерный ‖ nonce ‖ шифротекст ‖ тег.</summary>
        private static byte[] Seal(byte[] machinePublic, byte[] plain)
        {
            var ephemeral = new X25519PrivateKeyParameters(new SecureRandom());
            var ephemeralPublic = ephemeral.GeneratePublicKey().GetEncoded();

            var agreement = new X25519Agreement();
            agreement.Init(ephemeral);
            var shared = new byte[32];
            agreement.CalculateAgreement(new X25519PublicKeyParameters(machinePublic), shared, 0);

            var hkdf = new HkdfBytesGenerator(new Sha256Digest());
            hkdf.Init(new HkdfParameters(
                shared,
                [.. ephemeralPublic, .. machinePublic],
                Encoding.ASCII.GetBytes("elitesip.pair.v1")));
            var key = new byte[32];
            hkdf.GenerateBytes(key, 0, key.Length);

            var nonce = new byte[12];
            new SecureRandom().NextBytes(nonce);

            var gcm = new GcmBlockCipher(new AesEngine());
            gcm.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, Encoding.ASCII.GetBytes("ESIPP1")));
            var output = new byte[gcm.GetOutputSize(plain.Length)];
            var written = gcm.ProcessBytes(plain, 0, plain.Length, output, 0);
            gcm.DoFinal(output, written);

            return [.. ephemeralPublic, .. nonce, .. output];
        }
    }
}
