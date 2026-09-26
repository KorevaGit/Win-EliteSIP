using System.Net;
using System.Net.Http;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Линия предустановок: как правки из панели доезжают до рабочего места.
/// </summary>
///
/// <remarks>
/// <b>Приложение к панели не обращается.</b> Оно тянет подписанный файл с того
/// же канала раздачи, что и обновления. Панель стоит на локальном сервере
/// конторы и наружу не смотрит; узнаёт она о машинах только по следам, которые
/// оставляет раздача. Ни постоянного соединения, ни команд с сервера, ни
/// телеметрии здесь нет и не появится.
///
/// <b>Нет связи — работает локальный режим, и это не аварийное состояние.</b>
/// Софтфон нужен для звонков, а не для того, чтобы синхронизироваться.
///
/// Такт задаёт линия обновлений — <c>UpdateService</c>, — а эта служба только
/// делает работу. Канал один, и два независимых срока на нём разошлись бы через
/// полгода, когда один поменяли, а про другой забыли. Так же было и в
/// оригинале.
/// </remarks>
internal sealed class PresetService
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<PresetBundle.Entry> _apply;
    private readonly Func<bool> _isBlocked;
    private readonly Action<string> _log;

    /// <summary>Отложенное применение: файл проверен и разобран, но человек говорит.</summary>
    private PresetBundle.Entry? _deferred;

    private bool _isFetching;

    /// <param name="settings">настройки машины: применённая ревизия и ключ канала.</param>
    /// <param name="apply">
    /// применить запись. Замыкание, а не ссылка на модель: этой службе о ней
    /// знать нечего.
    /// </param>
    /// <param name="isBlocked">
    /// можно ли применять прямо сейчас. Обновление предустановки
    /// <b>обязательное</b>, кнопки «Отложить» нет и быть не должно: без него
    /// меняется адрес АТС, и машина просто не звонит. Но ждать оно умеет, и
    /// поводов ровно два — идёт разговор либо открыто «Управление». Второе
    /// потому, что там правки копятся в памяти и записываются разом по
    /// «Сохранить»: применить предустановку в этот момент значит либо потерять
    /// её по «Отменить», либо затереть ею несохранённые правки администратора.
    /// </param>
    internal PresetService(
        Func<AppSettings> settings,
        Action<PresetBundle.Entry> apply,
        Func<bool> isBlocked,
        Action<string> log)
    {
        _settings = settings;
        _apply = apply;
        _isBlocked = isBlocked;
        _log = log;
    }

    /// <summary>Отметить, что канал ответил.</summary>
    ///
    /// <remarks>
    /// Отдельно от применения: связь была и тогда, когда применять оказалось
    /// нечего, — а это как раз обычный случай.
    /// </remarks>
    internal Action? NoteContact { get; set; }

    /// <summary>
    /// Доложить, идёт ли проверка и чем кончилась прошлая.
    /// </summary>
    ///
    /// <remarks>
    /// Нужно кнопке «Проверить настройки сейчас»: без ответа она молчит, и
    /// нажавший не знает, случилось ли что-нибудь вообще.
    /// </remarks>
    internal Action<bool, string?>? Report { get; set; }

    /// <summary>
    /// Открытый ключ линии из файла заводской настройки.
    /// </summary>
    ///
    /// <remarks>
    /// Один на всё подписанное: файл предустановок, помашинный доступ, отзыв.
    /// Второй ключ означал бы второй способ однажды перепутать, какой из них
    /// чей.
    ///
    /// Пустой — линия выключена целиком. Так и задумано: ключ вписывается перед
    /// первой выкладкой, и до тех пор приложение обязано работать, а не падать.
    /// </remarks>
    internal static PanelPublicKey? ChannelPublicKey() => Parse(Provisioning.Current?.PresetsPublicKey);

    /// <summary>
    /// Открытый ключ линии выпусков — им проверяется манифест обновления.
    /// </summary>
    ///
    /// <remarks>
    /// Отдельно от ключа панели: в бою это разные ключи, см.
    /// <see cref="Provisioning.Secrets.ReleasesPublicKey"/>.
    /// </remarks>
    internal static PanelPublicKey? ReleasesPublicKey() => Parse(Provisioning.Current?.ReleasesPublicKey);

    private static PanelPublicKey? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            return PanelPublicKey.FromBase64(raw);
        }
        catch (PanelLinkException)
        {
            return null;
        }
    }

    /// <summary>Спросить канал.</summary>
    internal async Task CheckAsync()
    {
        var publicKey = ChannelPublicKey();
        if (publicKey is null)
        {
            _log("предустановки выключены: в заводской настройке нет открытого ключа линии");
            Report?.Invoke(false, Resources.Strings.Get("PresetsLineOff"));
            return;
        }

        var panel = _settings().Panel;
        if (!panel.IsManaged)
        {
            _log("предустановки не применяются: машина в ручном режиме");
            Report?.Invoke(false, Resources.Strings.Get("PresetsManualMode"));
            return;
        }

        if (!panel.HasChannelKey)
        {
            // Машина, поднятая ключом старого образца: панель её знает, а ключа
            // канала у неё нет — ходить нечем, пока не перепрошьют.
            _log("предустановки: у машины нет ключа канала");
            Report?.Invoke(false, Resources.Strings.Get("PresetsNoChannelKey"));
            return;
        }

        var channelKey = panel.ChannelKey();
        var url = Provisioning.Current?.Updates?.PresetsUrl();
        if (channelKey is null || url is null)
        {
            _log("предустановки выключены: нет канала или ключ не расшифровался");
            Report?.Invoke(false, Resources.Strings.Get("PresetsNoChannelKey"));
            return;
        }

        if (_isFetching)
        {
            return;
        }

        _isFetching = true;
        Report?.Invoke(true, null);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            ChannelRequest.Authorize(request, panel.InstallationID, channelKey);
            ChannelRequest.Describe(request, panel.AppliedRevision, panel.AppliedConfigRevision);

            using CancellationTokenSource deadline = new(ChannelRequest.Timeout);
            using var response = await ChannelRequest.Client.SendAsync(request, deadline.Token)
                .ConfigureAwait(true);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                // 401 здесь означает, что панель обрубила ключ машины. Это
                // <b>не</b> повод сбрасываться: сброс запускает только
                // подписанный отзыв — иначе одна ошибка на стороне канала стёрла
                // бы все машины разом.
                _log($"предустановки: канал ответил {(int)response.StatusCode}");
                Report?.Invoke(false, Resources.Strings.Format("PresetsChannelSaid", (int)response.StatusCode));
                return;
            }

            var data = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(true);
            Receive(data, publicKey);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // Нет связи — обычное состояние, а не беда.
            _log($"предустановки: канал недоступен — {error.Message}");
            Report?.Invoke(false, Resources.Strings.Get("PresetsChannelDown"));
        }
        finally
        {
            _isFetching = false;
        }
    }

    /// <summary>Разбирает ответ канала.</summary>
    private void Receive(byte[] data, PanelPublicKey publicKey)
    {
        // Канал ответил — отмечаем до разбора: связь состоялась, даже если файл
        // окажется негодным, и администратору важно отличить «канал молчит» от
        // «канал отвечает, а подпись не сходится».
        NoteContact?.Invoke();

        PresetBundle bundle;
        try
        {
            bundle = PresetBundle.Verified(data, publicKey);
        }
        catch (PanelLinkException error)
        {
            // Подпись не сошлась — файл отбрасывается целиком и молча не
            // остаётся: подделанный байт обязан быть виден в журнале.
            _log($"предустановки ОТБРОШЕНЫ: {error.Message}");
            Report?.Invoke(false, Resources.Strings.Get("PresetsSignatureFailed"));
            return;
        }

        var panel = _settings().Panel;
        var entry = bundle.EntryOf(panel.PresetID);
        if (entry is null)
        {
            // Себя в файле нет — предустановку могли заархивировать. Машина
            // продолжает жить с тем, что применила раньше.
            _log("предустановки: своей записи в файле нет");
            Report?.Invoke(false, Resources.Strings.Get("PresetsNoEntry"));
            return;
        }

        // Обычное правило — «применяем то, что новее применённого». Просьба
        // переприменить его отменяет, и это единственный случай, когда та же
        // самая ревизия накладывается второй раз. Отменять правило пришлось
        // потому, что без отмены возврат машины под предустановку не возвращал
        // ничего: ревизия за время жизни своим умом не менялась, локальные
        // правки накопились, а проверка отвечала «настройки уже свежие».
        if (!panel.WantsResync && entry.Revision <= panel.AppliedRevision)
        {
            Report?.Invoke(false, Resources.Strings.Get("PresetsAlreadyFresh"));
            return;
        }

        ApplyOrDefer(entry);
    }

    /// <summary>Применяет ревизию — или откладывает.</summary>
    private void ApplyOrDefer(PresetBundle.Entry entry)
    {
        if (_isBlocked())
        {
            _deferred = entry;
            _log($"предустановка {entry.Revision} ждёт: разговор или открытое «Управление»");
            Report?.Invoke(false, Resources.Strings.Format("PresetsWaiting", entry.Revision));
            return;
        }

        _deferred = null;
        _apply(entry);
        Report?.Invoke(false, Resources.Strings.Format("PresetsApplied", entry.Revision));
    }

    /// <summary>Помеха ушла — доложить отложенное.</summary>
    ///
    /// <remarks>
    /// Зовётся тем же, что следит за концом разговора, и закрытием «Управления».
    /// </remarks>
    internal void HostBecameIdle()
    {
        if (_deferred is { } entry)
        {
            ApplyOrDefer(entry);
        }
    }
}
