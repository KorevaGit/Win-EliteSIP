using System.Net;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Tests;

public sealed class WorkFormatSyncTests
{
    private static PanelSettings Panel()
    {
        var panel = new PanelSettings { Mode = PanelMode.Managed, PresetID = "test", InstallationID = "test-machine", PendingWorkFormat = "remote" };
        panel.SetChannelKey("test-key");
        return panel;
    }

    [Fact]
    public async Task Последний_выбор_отправляется_сразу_после_удержанного_ответа()
    {
        var panel = Panel();
        var first = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<WorkFormatSync.Request>();
        using var sync = new WorkFormatSync(panel, (request, _) =>
        {
            requests.Add(request);
            return requests.Count == 1 ? first.Task : Task.FromResult(HttpStatusCode.NoContent);
        }, _ => { });
        var sending = sync.SendAsync();
        panel.PendingWorkFormat = "office";
        var joined = sync.SendAsync();
        Assert.Same(sending, joined);
        Assert.Single(requests);
        first.SetResult(HttpStatusCode.NoContent);
        await Task.WhenAll(sending, joined).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["remote", "office"], requests.Select(request => request.Format));
        Assert.Empty(panel.PendingWorkFormat);
    }

    [Fact]
    public async Task При_ошибке_выбор_сохраняется_без_бесконечного_цикла_повторов()
    {
        var panel = Panel();
        var calls = 0;
        using var sync = new WorkFormatSync(panel, (_, _) => Task.FromResult(++calls == 1
            ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent), _ => { });
        await sync.SendAsync();
        Assert.Equal(1, calls);
        Assert.Equal("remote", panel.PendingWorkFormat);
        await sync.SendAsync();
        Assert.Equal(2, calls);
        Assert.Empty(panel.PendingWorkFormat);
    }

    [Fact]
    public async Task Ответ_прежней_привязки_не_очищает_новый_выбор()
    {
        var panel = Panel();
        var first = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sync = new WorkFormatSync(panel, (_, _) => first.Task, _ => { });
        var sending = sync.SendAsync();
        panel.InstallationID = "new-machine";
        panel.PendingWorkFormat = "office";
        first.SetResult(HttpStatusCode.NoContent);
        await sending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("office", panel.PendingWorkFormat);
    }

    [Fact]
    public async Task Dispose_отменяет_текущий_запрос_без_очистки_выбора()
    {
        var panel = Panel();
        using var sync = new WorkFormatSync(panel, async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return HttpStatusCode.NoContent;
        }, _ => { });
        var sending = sync.SendAsync();
        sync.Dispose();
        await sending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("remote", panel.PendingWorkFormat);
    }

    [Fact]
    public async Task Закрепление_на_сервере_не_вызывает_цикл_повторных_отправок()
    {
        var panel = Panel();
        var calls = 0;
        using var sync = new WorkFormatSync(panel, (_, _) => { calls++; return Task.FromResult(HttpStatusCode.Conflict); }, _ => { });
        await sync.SendAsync();
        Assert.Equal(1, calls);
        Assert.Empty(panel.PendingWorkFormat);
    }
}
