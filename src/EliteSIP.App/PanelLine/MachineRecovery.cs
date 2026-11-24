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

    /// <summary>
    /// Сколько помнить принятую заявку. Spark отвечает 202 и машине, которую
    /// ещё знает, не заводя заявки; без срока такая квитанция навсегда
    /// закрывала бы отправку в следующее окно восстановления. Сутки — длина окна.
    /// </summary>
    internal static readonly TimeSpan ReceiptLifetime = TimeSpan.FromHours(24);

    private bool _sending;

    internal sealed record Submission(byte[] Payload, IReadOnlyDictionary<string, string> Proofs);

    internal async Task CheckAsync(AppSettings settings, Action<string> log)
    {
        var panel = settings.Panel;
        if (_sending || !panel.HasChannelKey || panel.AppliedRevision < 1
            || HasFreshReceipt(panel, DateTimeOffset.UtcNow))
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
            if (window.StatusCode != HttpStatusCode.NoContent
                || panel.InstallationID != installationID)
            {
                return;
            }

            var submission = Prepare(settings, installationID, out var skipped);
            if (submission is null)
            {
                log($"восстановление Spark: заявка не собрана — {skipped}");
                return;
            }

            using var response = await Client.PostAsJsonAsync(endpoint, new { payload = submission.Payload, proofs = submission.Proofs }).ConfigureAwait(true);
            if (response.StatusCode == HttpStatusCode.Accepted && panel.InstallationID == installationID)
            {
                panel.RecoverySubmittedInstallationID = installationID;
                panel.RecoverySubmittedAt = DateTimeOffset.UtcNow;
                log("восстановление Spark: заявка принята");
            }
            else
            {
                log($"восстановление Spark: заявка отклонена ({(int)response.StatusCode})");
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException
                                      or CryptographicException or FormatException or PanelLinkException
                                      or InvalidOperationException or NotSupportedException)
        {
            log($"восстановление Spark: заявку не удалось отправить ({error.GetType().Name}: {error.Message})");
        }
        finally
        {
            _sending = false;
        }
    }

    internal static bool HasFreshReceipt(PanelSettings panel, DateTimeOffset now)
        => panel.RecoverySubmittedInstallationID == panel.InstallationID
           && now - panel.RecoverySubmittedAt < ReceiptLifetime;

    internal static Submission? Prepare(AppSettings settings, string installationID, out string skipped)
    {
        var panel = settings.Panel;
        var key = panel.MachineKey();
        var channel = panel.ChannelKey();
        if (key is null || channel is null || string.IsNullOrWhiteSpace(panel.PresetName))
        {
            skipped = key is null ? "нет ключа машины" : channel is null ? "нет ключа канала" : "нет названия предустановки";
            return null;
        }

        var profiles = settings.Profiles.Append(ProfileSwitch.Snapshot(settings)).ToArray();
        var main = profiles.FirstOrDefault(profile => profile.ProfileId == (panel.MainProfileId ?? settings.Account.ProfileId))
            ?? profiles[^1];

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
                skipped = $"у номера «{profile.Username}» нет SIP-пароля или он повторяется";
                return null;
            }
        }

        skipped = string.Empty;
        return new Submission(payload, proofs);
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
