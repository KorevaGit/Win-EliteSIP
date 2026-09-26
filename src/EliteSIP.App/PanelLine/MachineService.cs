using System.Net;
using System.Net.Http;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// То, что принадлежит одной машине: её административный пароль и её отзыв.
/// </summary>
///
/// <remarks>
/// Отдельно от <see cref="PresetService"/>, хотя ходит на тот же канал тем же
/// ключом. Причина в сроках: файл предустановок машина спрашивает раз в два
/// часа, а отзыв — раз в пятнадцать минут, потому что отзыв срабатывает ровно с
/// задержкой опроса, и на двухчасовом сроке уволенный сотрудник работал бы ещё
/// два часа после нажатия «отозвать».
///
/// <b>Ходит она помашинным ключом, а не общей парой из файла заводской
/// настройки.</b> Общая лежит открытым текстом в каждом приложении и открывает
/// теперь только выпуски.
/// </remarks>
internal sealed class MachineService
{
    /// <summary>
    /// Как часто спрашивать про отзыв.
    /// </summary>
    ///
    /// <remarks>
    /// Пятнадцать минут. Объект крошечный, ответ «не отзывали» — это 404 в
    /// несколько байт: тридцать машин дают меньше трёх тысяч запросов в сутки,
    /// три процента бесплатной квоты канала.
    /// </remarks>
    internal static readonly TimeSpan RevocationInterval = TimeSpan.FromMinutes(15);

    private readonly Func<AppSettings> _settings;
    private readonly Action<MachineAccess> _applyAccess;
    private readonly Action<Revocation> _reset;
    private readonly Action<string> _log;

    /// <summary>
    /// Что сейчас спрашивается — по приставке, а не одним признаком на всё.
    /// </summary>
    ///
    /// <remarks>
    /// В оригинале признак сперва был один на обе линии, и они мешали друг
    /// другу: такты у них разные — отзыв каждые пятнадцать минут, доступ в общем
    /// цикле, — и при наложении второй запрос молча отбрасывался. Заметнее всего
    /// на запуске, где оба идут почти одновременно: административный пароль мог
    /// не приехать до следующего такта, а машина при этом выглядела настроенной.
    /// </remarks>
    private readonly HashSet<string> _asking = [];

    /// <param name="settings">настройки машины: прочитать ключ канала и себя.</param>
    /// <param name="applyAccess">применить приехавший административный пароль.</param>
    /// <param name="reset">сбросить машину. Зовётся только по подписанному отзыву.</param>
    internal MachineService(
        Func<AppSettings> settings,
        Action<MachineAccess> applyAccess,
        Action<Revocation> reset,
        Action<string> log)
    {
        _settings = settings;
        _applyAccess = applyAccess;
        _reset = reset;
        _log = log;
    }

    /// <summary>
    /// Что делать с открытой конфигурацией. Ставит линия: применить сразу или
    /// отложить до конца разговора решает она.
    /// </summary>
    internal Action<MachineConfig>? ApplyConfig { get; set; }

    /// <summary>
    /// Спросить канал про свою конфигурацию из Spark.
    /// </summary>
    ///
    /// <remarks>
    /// 404 — Spark ещё не выложил или машину отвязали: молчать и ждать. Не
    /// открылась своим ключом, чужая машина, не та подпись — в журнал и не
    /// применять. Ревизию «новее применённой» сверяет тот, кто применяет: сюда
    /// не заходит состояние, которое может поменяться, пока идёт запрос.
    /// </remarks>
    internal Task CheckConfigAsync()
        => FetchAsync("config", (data, publicKey, installationID) =>
        {
            var key = _settings().Panel.MachineKey();
            if (key is null)
            {
                // Машина, поднятая ключом активации, ещё не зарегистрировала
                // свой: конфигурации для неё нет, живёт по access/.
                return;
            }

            try
            {
                var config = MachineConfig.Open(data, publicKey, installationID, MachineKeyPair.FromBase64(key));
                ApplyConfig?.Invoke(config);
            }
            catch (PanelLinkException error)
            {
                _log($"конфигурация ОТБРОШЕНА: {error.Message}");
            }
        });

    /// <summary>Спросить канал про свой доступ. Идёт в общем такте с предустановками.</summary>
    internal Task CheckAccessAsync()
        => FetchAsync("access", (data, publicKey, installationID) =>
        {
            try
            {
                _applyAccess(MachineAccess.Verified(data, publicKey, installationID));
            }
            catch (PanelLinkException error)
            {
                _log($"доступ ОТБРОШЕН: {error.Message}");
            }
        });

    /// <summary>
    /// Спросить канал, не отозвали ли машину. Свой такт, вчетверо чаще.
    /// </summary>
    ///
    /// <remarks>
    /// <b>Отсутствие ответа никогда не означает отзыв.</b> Нет сети, лежит
    /// Worker, 404 по адресу — машина работает дальше. Сбрасывает её только
    /// подписанный объект: иначе опечатка в правиле Cloudflare стирала бы не одну
    /// машину, а все тридцать разом.
    /// </remarks>
    internal Task CheckRevocationAsync()
        => FetchAsync("revoked", (data, publicKey, installationID) =>
        {
            try
            {
                var revocation = Revocation.Verified(data, publicKey, installationID);
                _log($"получен подписанный отзыв от {revocation.RevokedAt:u}");
                _reset(revocation);
            }
            catch (PanelLinkException error)
            {
                // Подпись не сошлась — это не отзыв, а мусор по нашему адресу.
                // Сбрасывать по нему нельзя ни в коем случае.
                _log($"отзыв ОТБРОШЕН: {error.Message}");
            }
        });

    /// <summary>Общий заход на канал за помашинным объектом.</summary>
    private async Task FetchAsync(string prefix, Action<byte[], PanelPublicKey, string> handle)
    {
        var publicKey = PresetService.ChannelPublicKey();
        if (publicKey is null)
        {
            _log("помашинные объекты выключены: в заводской настройке нет открытого ключа линии");
            return;
        }

        var panel = _settings().Panel;
        if (!panel.HasChannelKey)
        {
            return;
        }

        var channelKey = panel.ChannelKey();
        if (channelKey is null)
        {
            // Ключ не расшифровался: файл настроек принесли с чужой машины.
            // Линия молчит, машина живёт тем, что применила раньше.
            _log($"{prefix}: ключ канала не расшифровался — линия панели молчит");
            return;
        }

        var url = Provisioning.Current?.Updates?.MachineUrl(prefix, panel.InstallationID);
        if (url is null)
        {
            return;
        }

        lock (_asking)
        {
            if (!_asking.Add(prefix))
            {
                return;
            }
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            ChannelRequest.Authorize(request, panel.InstallationID, channelKey);
            ChannelRequest.Describe(request, panel.AppliedRevision, panel.AppliedConfigRevision);

            using CancellationTokenSource deadline = new(ChannelRequest.Timeout);
            using var response = await ChannelRequest.Client
                .SendAsync(request, deadline.Token)
                .ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    // Для отзыва это «не отзывали», для доступа — «панель ещё не
                    // выложила». Оба случая — молчание, а не событие.
                    return;

                case HttpStatusCode.Unauthorized:
                    // Ключ канала обрублен. Это <b>не</b> отзыв: сбрасываться по
                    // отказу в доступе нельзя — так одна ошибка на стороне канала
                    // стирала бы все машины сразу.
                    _log($"{prefix}: канал не принял ключ машины");
                    return;

                case HttpStatusCode.OK:
                    break;

                default:
                    _log($"{prefix}: канал ответил {(int)response.StatusCode}");
                    return;
            }

            var data = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            handle(data, publicKey, panel.InstallationID);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // Нет связи — обычное состояние, а не беда.
            _log($"{prefix}: канал недоступен — {error.Message}");
        }
        finally
        {
            lock (_asking)
            {
                _asking.Remove(prefix);
            }
        }
    }

    /// <summary>
    /// Забрать свой административный пароль прямо сейчас, не дожидаясь такта.
    /// </summary>
    ///
    /// <remarks>
    /// Нужно ровно в одном месте — в мастере, сразу после того, как ключ открыл
    /// пакет. Ждать общего опроса там нельзя: между концом мастера и первым
    /// заходом на канал «Управление» стояло бы открытым для всякого, а машина
    /// при этом выглядела бы настроенной.
    ///
    /// Отдельной функцией, а не методом службы: службы в этот момент ещё нет —
    /// она заводится при запуске приложения, а мастер идёт до него.
    /// </remarks>
    internal static async Task<MachineAccess> FetchAccessAsync(
        string installationID, string channelKey, CancellationToken cancellation = default)
    {
        var publicKey = PresetService.ChannelPublicKey()
            ?? throw new PanelLinkException(PanelLinkFailure.SignatureDidNotMatch);

        var url = Provisioning.Current?.Updates?.MachineUrl("access", installationID)
            ?? throw new PanelLinkException(PanelLinkFailure.MalformedBundle);

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        ChannelRequest.Authorize(request, installationID, channelKey);

        using CancellationTokenSource deadline = new(ChannelRequest.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellation);

        using var response = await ChannelRequest.Client.SendAsync(request, linked.Token)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedBundle);
        }

        var data = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);

        return MachineAccess.Verified(data, publicKey, installationID);
    }
}
