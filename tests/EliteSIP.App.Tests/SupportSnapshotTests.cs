using EliteSIP.App.Settings;
using EliteSIP.App.Shell;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class SupportSnapshotTests
{
    [Theory]
    [InlineData("Application: EliteSIP.App.exe", true)]
    [InlineData("Faulting app elitesip.exe", true)]
    [InlineData("BackendAudit.exe; stack EliteSIP.Audio.WasapiVoiceAudioEngine", false)]
    public void В_архив_попадают_только_системные_сбои_клиента(string xml, bool expected)
        => Assert.Equal(expected, SupportCrashEvidence.IsClientEvent(xml));

    [Fact]
    public void SIP_трасса_включена_по_умолчанию_но_явное_отключение_сохраняется()
    {
        Assert.True(new MaintenanceSettings().LogsSipTrace);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<MaintenanceSettings>("{}")!.LogsSipTrace);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<MaintenanceSettings>("{\"LogsSipTrace\":false}")!.LogsSipTrace);
    }

    [Fact]
    public void Снимок_содержит_машину_сеть_и_звук_без_секретов() => WpfHost.Run(() =>
    {
        var settings = new AppSettings();
        settings.Account.Username = "176";
        settings.Account.Domain = "pbx.test";
        settings.Credentials.SetPassword("sip-secret-must-not-leak");
        var text = SupportSnapshot.Capture(settings, includeAudioInventory: false);
        Assert.Contains(Environment.MachineName, text, StringComparison.Ordinal);
        Assert.Contains("[Сеть]", text, StringComparison.Ordinal);
        Assert.Contains("[Звук]", text, StringComparison.Ordinal);
        Assert.Contains("pbx.test", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sip-secret-must-not-leak", text, StringComparison.Ordinal);
        Assert.True(settings.Maintenance.LogsSipTrace);
    });
}
