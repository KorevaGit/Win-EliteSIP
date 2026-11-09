using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.MediaCore;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

internal sealed class MachineRecovery
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private bool _sending;

    internal async Task CheckAsync(AppSettings settings, Action<string> log)
    {
        var panel = settings.Panel;
        if (_sending || !panel.IsManaged || !panel.HasChannelKey || panel.AppliedRevision < 1
            || panel.RecoverySubmittedInstallationID == panel.InstallationID)
        {
            return;
        }

        var baseUri = Provisioning.Current?.PairBase ?? new Uri(Provisioning.DefaultPairUrl);
        if (baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        var installationID = panel.InstallationID;
        var endpoint = new Uri(baseUri, "api/pair/recovery");
        _sending = true;
        try
        {
            using var window = await Client.GetAsync(endpoint).ConfigureAwait(true);
            if (window.StatusCode != HttpStatusCode.NoContent || !panel.IsManaged
                || panel.InstallationID != installationID)
            {
                return;
            }

            var key = panel.MachineKey();
            var channel = panel.ChannelKey();
            if (key is null || channel is null || string.IsNullOrWhiteSpace(panel.PresetName))
            {
                return;
            }

            var profiles = settings.Profiles.Append(ProfileSwitch.Snapshot(settings)).ToArray();
            var main = profiles.FirstOrDefault(profile => profile.ProfileId == (panel.MainProfileId ?? settings.Account.ProfileId));
            if (main is null)
            {
                return;
            }

            var extra = profiles.Where(profile => profile.ProfileId != main.ProfileId)
                .OrderBy(profile => profile.Username, StringComparer.Ordinal).ToArray();
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                installation_id = installationID,
                channel_key_hash = ChannelKey.Hash(channel),
                public_key = MachineKeyPair.FromBase64(key).PublicKeyBase64,
                device_name = Environment.MachineName,
                extension = main.Username,
                label = main.DisplayName,
                work_format = main.Site == WorkplaceSite.Remote ? "remote" : "office",
                work_format_locked = panel.IsWorkFormatLocked,
                preset_id = panel.PresetID,
                preset_name = panel.PresetName,
                preset_revision = panel.AppliedRevision,
                config_revision = panel.AppliedConfigRevision,
                fields = PresetFields(settings),
                lines = extra.Select(profile => new { extension = profile.Username, label = profile.DisplayName }),
            });

            var message = new byte["elitesip-recovery-v1\n"u8.Length + payload.Length];
            "elitesip-recovery-v1\n"u8.CopyTo(message);
            payload.CopyTo(message, "elitesip-recovery-v1\n"u8.Length);
            var proofs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                var password = profile.Password();
                if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(profile.Username)
                    || !proofs.TryAdd(profile.Username, Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(password), message))))
                {
                    return;
                }
            }

            using var response = await Client.PostAsJsonAsync(endpoint, new { payload, proofs }).ConfigureAwait(true);
            if (response.StatusCode == HttpStatusCode.Accepted && panel.InstallationID == installationID)
            {
                panel.RecoverySubmittedInstallationID = installationID;
                log("восстановление Spark: заявка принята");
            }
            else if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
            {
                log($"восстановление Spark: заявка отклонена ({(int)response.StatusCode})");
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException
                                      or CryptographicException or FormatException or PanelLinkException)
        {
            log("восстановление Spark: заявку не удалось отправить");
        }
        finally
        {
            _sending = false;
        }
    }

    private static object PresetFields(AppSettings settings)
    {
        var guard = settings.IncomingCall;
        var timing = new DtmfTiming();
        return new
        {
            dtmf = new
            {
                toneMilliseconds = timing.ToneMilliseconds,
                gapMilliseconds = timing.GapMilliseconds,
                pauseMilliseconds = DtmfSequence.DefaultPauseMilliseconds,
                macros = settings.Dtmf.Macros.Select(macro => new
                {
                    id = macro.Id,
                    title = macro.Title,
                    sequence = macro.Sequence,
                    transfersCall = macro.TransfersCall,
                }),
                macroColumns = settings.Dtmf.MacroColumns,
                macroHeight = settings.Dtmf.MacroHeight,
                macroHeightIsManual = settings.Dtmf.MacroHeightIsManual,
            },
            incomingCall = new
            {
                isEnabled = guard.IsEnabled,
                isRandomPositionEnabled = guard.IsRandomPositionEnabled,
                tunesRandomnessByHand = guard.TunesRandomnessByHand,
                minimumTravel = guard.MinimumTravel,
                screenMargin = guard.ScreenMargin,
                targetCount = guard.TargetCount,
                requiresCursorMovement = guard.RequiresCursorMovement,
                tunesLivenessByHand = guard.TunesLivenessByHand,
                requiredCursorTravel = guard.RequiredCursorTravel,
                requiredCursorSamples = guard.RequiredCursorSamples,
                rejectsSyntheticEvents = guard.RejectsSyntheticEvents,
            },
            conference = new { featureCode = settings.Pbx.ConferenceFeatureCode, roomExtension = "8000" },
            siteAddresses = new { office = settings.Pbx.OfficeAddress, remote = settings.Pbx.RemoteAddress },
            portKnock = new
            {
                spacingSeconds = settings.PortKnock.SpacingSeconds,
                repeatIntervalSeconds = settings.PortKnock.RepeatIntervalSeconds,
                steps = settings.PortKnock.Steps.Select(step => new
                {
                    host = step.Host,
                    payloadBytes = step.PayloadBytes,
                    count = step.Count,
                }),
            },
            acceptsAnyTLSCertificate = settings.Pbx.AcceptsAnyTlsCertificate,
            transport = settings.Pbx.Transport == SipTransport.Tls ? "tls" : "udp",
            autoAnswer = guard.AutoAnswer,
            autoAnswerNumbers = guard.AutoAnswerNumbers,
            masksMobileNumbers = guard.MasksMobileNumbers,
        };
    }
}
