using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.Tests;

public sealed class MachineRecoveryTests
{
    private static AppSettings Bound()
    {
        var settings = new AppSettings();
        settings.Panel.InstallationID = "fcea51e12f05bb25ae984511e6565884";
        settings.Panel.PresetID = "86928E3F-DCAB-4608-85F2-3F58C42DD0FE";
        settings.Panel.PresetName = "КЦ";
        settings.Panel.AppliedRevision = 9;
        settings.Panel.SetChannelKey("channel-key");
        settings.Panel.SetMachineKey(MachineKeyPair.Generate().PrivateKeyBase64);
        settings.Account.Username = "176";
        settings.Credentials.SetPassword("sip-secret");
        return settings;
    }

    [Fact]
    public void Заявка_подписана_паролем_номера()
    {
        var settings = Bound();

        var submission = MachineRecovery.Prepare(settings, settings.Panel.InstallationID, out var skipped);

        Assert.NotNull(submission);
        Assert.Empty(skipped);
        using var payload = JsonDocument.Parse(submission.Payload);
        Assert.Equal("176", payload.RootElement.GetProperty("extension").GetString());
        var message = Encoding.UTF8.GetBytes("elitesip-recovery-v1\n").Concat(submission.Payload).ToArray();
        Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("sip-secret"), message)), submission.Proofs["176"]);
    }

    [Fact]
    public void Чужой_основной_профиль_не_останавливает_заявку()
    {
        var settings = Bound();
        settings.Panel.MainProfileId = Guid.NewGuid();

        Assert.NotNull(MachineRecovery.Prepare(settings, settings.Panel.InstallationID, out _));
    }

    [Fact]
    public void Без_пароля_причина_называется()
    {
        var settings = Bound();
        settings.Credentials.SetPassword(string.Empty);

        Assert.Null(MachineRecovery.Prepare(settings, settings.Panel.InstallationID, out var skipped));
        Assert.Contains("176", skipped);
    }

    [Fact]
    public void Квитанция_без_срока_и_старше_суток_не_закрывает_отправку()
    {
        var panel = Bound().Panel;
        var now = DateTimeOffset.UtcNow;
        panel.RecoverySubmittedInstallationID = panel.InstallationID;

        Assert.False(MachineRecovery.HasFreshReceipt(panel, now));

        panel.RecoverySubmittedAt = now.AddHours(-1);
        Assert.True(MachineRecovery.HasFreshReceipt(panel, now));

        panel.RecoverySubmittedAt = now.AddHours(-25);
        Assert.False(MachineRecovery.HasFreshReceipt(panel, now));
    }
}
