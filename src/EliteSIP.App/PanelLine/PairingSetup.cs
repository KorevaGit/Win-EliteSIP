using System.Net;
using System.Net.Http;
using EliteSIP.AdminAccess;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Первая настройка машины после привязки в Spark.
/// </summary>
///
/// <remarks>
/// <para>
/// Отдельно от службы линии: мастер идёт до того, как линия заведена, и
/// настроиться машина обязана сразу, а не через пятнадцать минут опроса —
/// иначе всё это время она выглядела бы настроенной без номера.
/// </para>
/// <para>
/// Порядок — из контракта Spark: конфигурация, затем по её <c>preset_id</c>
/// файл предустановок, затем наложение. Предустановка ложится первой, потому
/// что из неё берётся пара адресов АТС, а адрес регистрации выбирается по
/// площадке из конфигурации.
/// </para>
/// </remarks>
internal static class PairingSetup
{
    /// <summary>
    /// Сколько раз спрашивать конфигурацию на 404. Spark выкладывает её до того,
    /// как отвечает «привязана», — повтор на всякий случай, как на macOS.
    /// </summary>
    private const int ConfigAttempts = 4;

    private static readonly TimeSpan ConfigRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Что забрано с сервера: конфигурация и, если нашлась, предустановка.</summary>
    internal sealed record Fetched(MachineConfig Config, PresetBundle.Entry? Preset);

    /// <summary>Забирает конфигурацию и предустановку.</summary>
    ///
    /// <exception cref="PanelLinkException">Конфигурация не пришла или не открылась.</exception>
    internal static async Task<Fetched> FetchAsync(
        string installationID, string channelKey, MachineKeyPair machine, CancellationToken cancellation)
    {
        var publicKey = PresetService.ChannelPublicKey()
            ?? throw new PanelLinkException(PanelLinkFailure.SignatureDidNotMatch);

        var configUrl = Provisioning.Current?.Updates?.MachineUrl("config", installationID)
            ?? throw new PanelLinkException(PanelLinkFailure.MalformedBundle);

        byte[]? data = null;
        for (var attempt = 1; attempt <= ConfigAttempts && data is null; attempt++)
        {
            data = await GetAsync(configUrl, installationID, channelKey, cancellation).ConfigureAwait(false);
            if (data is null && attempt < ConfigAttempts)
            {
                await Task.Delay(ConfigRetryDelay, cancellation).ConfigureAwait(false);
            }
        }

        if (data is null)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        var config = MachineConfig.Open(data, publicKey, installationID, machine);

        // Предустановки нет в файле — конфигурация всё равно ложится, а
        // предустановка подтянется первым же опросом.
        PresetBundle.Entry? preset = null;
        if (config.PresetID.Length > 0
            && Provisioning.Current?.Updates?.PresetsUrl() is { } presetsUrl
            && await GetAsync(presetsUrl, installationID, channelKey, cancellation).ConfigureAwait(false) is { } bundle)
        {
            try
            {
                preset = PresetBundle.Verified(bundle, publicKey).EntryOf(config.PresetID);
            }
            catch (PanelLinkException)
            {
                // То же, что «нет в файле»: подтянется опросом, а подделанный
                // файл опрос и запишет в журнал.
            }
        }

        return new Fetched(config, preset);
    }

    /// <summary>
    /// Накладывает забранное и переводит машину под Spark.
    /// </summary>
    internal static void Apply(
        AppSettings settings,
        Fetched fetched,
        string installationID,
        string channelKey,
        MachineKeyPair machine,
        AdminAccessState adminAccess,
        Action<string> log)
    {
        settings.Panel.InstallationID = installationID;
        settings.Panel.SetChannelKey(channelKey);
        settings.Panel.SetMachineKey(machine.PrivateKeyBase64);
        settings.Panel.MachineKeyRegistered = true;

        if (fetched.Preset is { } preset)
        {
            settings.Panel.PresetID = preset.ID;
            settings.Panel.PresetName = preset.Name;
            settings.Apply(ManagedFields.Parse(preset.Fields));
            settings.Panel.AppliedRevision = preset.Revision;
            settings.Panel.AppliedAt = DateTimeOffset.UtcNow;
        }

        settings.Apply(fetched.Config, adminAccess, log);

        // Первая настройка — не «смена номера»: сказать в панели нечего.
        settings.Panel.LastNumberNotice = string.Empty;
    }

    /// <summary>GET своего объекта. <c>null</c> — 404 (ещё не выложен).</summary>
    private static async Task<byte[]?> GetAsync(Uri url, string installationID, string channelKey, CancellationToken cancellation)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        ChannelRequest.Authorize(request, installationID, channelKey);
        ChannelRequest.Describe(request, appliedRevision: 0);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ChannelRequest.Timeout);

        using var response = await ChannelRequest.Client.SendAsync(request, deadline.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"канал ответил {(int)response.StatusCode}", null, response.StatusCode);
        }

        return await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
    }
}
