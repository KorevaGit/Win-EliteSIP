using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Threading;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Автообновление рабочего места.
/// </summary>
///
/// <remarks>
/// <b>Что здесь происходит по порядку.</b>
///
/// <list type="number">
///   <item><description>Раз в два часа с разбросом приложение спрашивает канал
///   и, если там новее, молча качает установщик в фоне. Оператор об этом не
///   знает и знать не должен: качать — нечего решать.</description></item>
///   <item><description>Скачанное сверяется с отпечатком из подписанного
///   манифеста. До оператора доходит только то, что проверку
///   прошло.</description></item>
///   <item><description>И только теперь появляется предложение: две кнопки,
///   «Обновить» и «Отложить». Нажатие «Обновить» ничего не начинает, а
///   завершает — файл уже лежит проверенный, поэтому и выглядит
///   мгновенным.</description></item>
/// </list>
///
/// <b>Кнопки «Пропустить эту версию» нет.</b> Это решение оригинала, и оно
/// перенесено как есть: пропускать версию нельзя вовсе. Отсрочка — ровно
/// полчаса, и предела ей нет: настойчивость напоминания заменяет принуждение, а
/// рабочее место, которое неделю жмёт «Отложить», — это вопрос настойчивости, а
/// не политики.
///
/// Полчаса отсрочки и два часа между проверками — разные сроки, и путать их не
/// надо: первое про уже скачанное, второе про поход в сеть.
///
/// <b>Предложение не показывается в разговоре.</b> Проверка та же, что
/// откладывает предустановку.
///
/// <b>Будильник здесь один на две линии.</b> Канал один, и два независимых
/// срока на нём разошлись бы через полгода, когда один поменяли, а про другой
/// забыли. Поэтому такт предустановок переехал сюда же — как и было в оригинале,
/// где расписанием Sparkle не пользовались по той же причине.
///
/// <b>Чем это отличается от оригинала.</b> Sparkle заменён своей линией: тот же
/// подписанный конверт Ed25519, что у предустановок, и обычный установщик Inno
/// Setup вместо архива с приложением. Проверок стало не меньше, а по-другому:
/// подпись манифеста наша, подпись самого установщика — Authenticode, и её
/// проверяет Windows при запуске файла.
/// </remarks>
internal sealed class UpdateService : IDisposable
{
    /// <summary>
    /// Как часто спрашивать канал.
    /// </summary>
    ///
    /// <remarks>
    /// Два часа с разбросом — это и есть верхняя граница ожидания. Приложение
    /// обязано ещё и спросить канал при запуске: на машине, которую выключают на
    /// ночь, именно запуск, а не такт, доставляет вчерашний выпуск и вчерашнюю
    /// правку макроса.
    /// </remarks>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(2);

    /// <summary>
    /// Разброс вокруг такта.
    /// </summary>
    ///
    /// <remarks>
    /// Контора сидит за одним адресом, и после сбоя питания тридцать машин
    /// просыпаются в одну секунду. Всплеск дешевле размазать здесь, чем пережить
    /// лимитом на стороне канала.
    /// </remarks>
    private static readonly TimeSpan CheckJitter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Первая проверка после запуска — не мгновенно.
    /// </summary>
    ///
    /// <remarks>
    /// На старте приложение поднимает регистрацию, звук и окна, и отправлять его
    /// при этом ещё и в сеть значит соревноваться с самим собой за первые
    /// секунды, которые человек видит.
    /// </remarks>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(5);

    /// <summary>Через сколько напомнить о скачанном после «Отложить».</summary>
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(30);

    private readonly Func<bool> _isBusy;
    private readonly Action<string?> _announce;
    private readonly Action _alsoCheckPresets;
    private readonly Action<string> _log;

    private readonly DispatcherTimer _cycle = new();
    private readonly DispatcherTimer _reminder = new() { Interval = ReminderInterval };

    private bool _isChecking;

    /// <summary>Скачанный и проверенный установщик. Пока он есть, есть и предложение.</summary>
    private string? _readyInstaller;

    /// <param name="isBusy">идёт ли разговор.</param>
    /// <param name="announce">
    /// сообщить приложению, что версия скачана и ждёт, — или что уже не ждёт.
    /// Замыкание, а не ссылка на окно: службе о нём знать нечего.
    /// </param>
    /// <param name="alsoCheckPresets">спросить канал ещё и о предустановках, тем же тактом.</param>
    internal UpdateService(
        Func<bool> isBusy,
        Action<string?> announce,
        Action alsoCheckPresets,
        Action<string> log)
    {
        _isBusy = isBusy;
        _announce = announce;
        _alsoCheckPresets = alsoCheckPresets;
        _log = log;

        _cycle.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
        _reminder.Tick += (_, _) => Offer();
    }

    /// <summary>Версия, которая скачана и ждёт решения. <c>null</c> — не ждёт ничего.</summary>
    internal Version? ReadyVersion { get; private set; }

    /// <summary>Идёт ли проверка прямо сейчас — для кнопки «Проверить сейчас».</summary>
    internal bool IsChecking => _isChecking;

    /// <summary>Чем кончилась последняя проверка — или что с ней сейчас.</summary>
    ///
    /// <remarks>
    /// Меняется и по ходу проверки, а не только в конце: «скачиваем, 23 из 54
    /// МБ». Прежде кнопка показывала «Проверяем…» до самого конца — а конец
    /// включал закачку полусотни мегабайт и вопрос об установке, — и на
    /// медленном канале это выглядело вечной проверкой.
    /// </remarks>
    internal string? LastResult
    {
        get => _lastResult;
        private set
        {
            _lastResult = value;
            StatusChanged?.Invoke();
        }
    }

    private string? _lastResult;

    /// <summary>Сменилось <see cref="LastResult"/> или <see cref="IsChecking"/>. В потоке интерфейса.</summary>
    internal event Action? StatusChanged;

    /// <summary>Идущая проверка — чтобы нажатие кнопки к ней присоединилось.</summary>
    private Task? _running;

    /// <summary>Сколько закачка может стоять без единого байта, прежде чем её оборвут.</summary>
    ///
    /// <remarks>
    /// Минута, а не общий срок в четверть часа: оборвавшаяся связь иначе
    /// показывала бы «скачиваем, 12 из 54 МБ» пятнадцать минут подряд.
    /// Медленный канал сюда не попадает — медленный байты всё-таки отдаёт.
    /// </remarks>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Спросить, чем показать предложение. Ставит приложение.</summary>
    ///
    /// <remarks>
    /// Возвращает <c>true</c>, если оператор согласился обновиться сейчас.
    /// Служба сама окон не показывает: у неё нет ни владельца окна, ни права
    /// решать, когда человека можно отвлечь.
    /// </remarks>
    internal Func<Version, bool>? AskToInstall { get; set; }

    /// <summary>Что делать перед установкой: снять регистрацию и закрыть приложение.</summary>
    internal Action? PrepareForRestart { get; set; }

    internal Version Installed { get; } =
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Заводит общий будильник и делает первую проверку.</summary>
    internal void Start()
    {
        if (Provisioning.Current?.Updates is null)
        {
            // Отладочная сборка на машине без заводской настройки должна
            // работать, просто без обновлений.
            _log("обновления выключены: в заводской настройке нет канала");
            return;
        }

        Reschedule();
        _cycle.Interval = FirstCheckDelay;
        _cycle.Start();
    }

    /// <summary>Спросить канал прямо сейчас — кнопка в «Диагностике».</summary>
    internal async Task CheckNowAsync()
    {
        // Проверка уже идёт (такт или прошлое нажатие) — ждём её, а не
        // уходим молча: иначе нажатие посреди фоновой закачки не показывало
        // ничего, и кнопка выглядела мёртвой.
        if (_running is { IsCompleted: false } running)
        {
            await running.ConfigureAwait(true);
            return;
        }

        await CheckAsync(userInitiated: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Показать предложение, если есть что предлагать и можно сейчас.
    /// </summary>
    ///
    /// <remarks>
    /// Зовётся и по концу разговора: предложение, отложенное на время звонка, не
    /// должно ждать следующего получаса.
    /// </remarks>
    internal void Offer()
    {
        if (ReadyVersion is not { } version || _readyInstaller is null || AskToInstall is null)
        {
            return;
        }

        if (_isBusy())
        {
            // Не «Отложить», а «сейчас нельзя»: напоминание вернётся, как только
            // разговор кончится.
            return;
        }

        // Вопрос уже на экране — второй не нужен.
        //
        // Окно вопроса модальное и держит свой цикл сообщений, а внутри него
        // живут таймеры и привязки: конец разговора, напоминание, кнопка
        // «Установить» и завершившаяся проверка звали `Offer` снова, и
        // оператор получал два одинаковых окна подряд.
        if (_isOffering)
        {
            return;
        }

        _reminder.Stop();

        bool accepted;
        _isOffering = true;
        try
        {
            accepted = AskToInstall(version);
        }
        finally
        {
            _isOffering = false;
        }

        if (!accepted)
        {
            _log($"обновление {version} отложено на {ReminderInterval.TotalMinutes:0} минут");
            _reminder.Start();
            return;
        }

        Install();
    }

    /// <summary>Висит ли вопрос об установке на экране прямо сейчас.</summary>
    private bool _isOffering;

    public void Dispose()
    {
        _cycle.Stop();
        _reminder.Stop();
    }

    /// <summary>
    /// Передаёт обновление обновляльщику.
    /// </summary>
    ///
    /// <remarks>
    /// Прежде здесь запускался установщик. Под политикой заказчика так нельзя:
    /// скачанный файл лежит в каталоге, откуда запуск запрещён, а ставить надо
    /// в <c>Program Files</c>, куда оператор не пишет. Установку делает
    /// <c>EliteSIP.Updater</c> от SYSTEM — см. <see cref="UpdateHandoff"/>.
    ///
    /// <b>Уходим с дороги только если задачу удалось разбудить.</b> Иначе она
    /// проснётся сама в течение десяти минут, и всё это время оператор сидел бы
    /// без телефона неизвестно почему. Пусть лучше нас закроет установщик:
    /// брошенная регистрация переживёт себя минуту-другую, а вот десять минут
    /// тишины оператор объяснить не сможет.
    /// </remarks>
    private void Install()
    {
        if (ReadyVersion is not { } version)
        {
            return;
        }

        if (!UpdateHandoff.Request(version, _log))
        {
            return;
        }

        _log($"обновление {version} передано обновляльщику");

        if (UpdateHandoff.Trigger(_log))
        {
            PrepareForRestart?.Invoke();
        }
    }

    private async Task TickAsync()
    {
        Reschedule();

        await CheckAsync(userInitiated: false).ConfigureAwait(true);

        // Предустановки — тем же тактом и после обновлений: если сейчас
        // окажется, что надо ставить новую версию, применять к старой ещё и
        // новую предустановку незачем.
        _alsoCheckPresets();
    }

    /// <summary>Ставит следующий такт: два часа плюс разброс.</summary>
    private void Reschedule()
    {
        var jitter = TimeSpan.FromSeconds(
            RandomNumberGenerator.GetInt32((int)CheckJitter.TotalSeconds));

        _cycle.Interval = CheckInterval + jitter;
    }

    private Task CheckAsync(bool userInitiated)
    {
        if (_running is { IsCompleted: false } running)
        {
            return running;
        }

        _running = RunCheckAsync(userInitiated);
        return _running;
    }

    private async Task RunCheckAsync(bool userInitiated)
    {
        if (_isChecking)
        {
            return;
        }

        var channel = Provisioning.Current?.Updates;
        var url = channel?.ReleasesUrl();
        // Ключ линии выпусков, а не панели: в бою это разные ключи, и до
        // 10 сентября 2026 порт путал их между собой.
        var publicKey = PresetService.ReleasesPublicKey();

        if (channel is null || url is null || publicKey is null)
        {
            LastResult = Resources.Strings.Get("UpdatesLineOff");
            return;
        }

        _isChecking = true;
        LastResult = Resources.Strings.Get("UpdatesChecking");

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(
                "Authorization", Provisioning.BasicHeader(channel.User, channel.Password));

            using CancellationTokenSource deadline = new(ChannelRequest.Timeout);
            using var response = await ChannelRequest.Client.SendAsync(request, deadline.Token)
                .ConfigureAwait(true);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _log($"обновления: канал ответил {(int)response.StatusCode}");
                LastResult = Resources.Strings.Format("PresetsChannelSaid", (int)response.StatusCode);
                return;
            }

            var data = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(true);
            var manifest = ReleaseManifest.Verified(data, publicKey);

            if (!manifest.IsNewerThan(Installed))
            {
                LastResult = Resources.Strings.Get("UpdatesUpToDate");

                if (userInitiated)
                {
                    _log($"обновления: установлена свежая версия {Installed.ToString(3)}");
                }

                return;
            }

            // Уже скачанное не качается второй раз: между тактами предложение
            // может просто ждать ответа оператора.
            if (ReadyVersion == manifest.Version && _readyInstaller is not null)
            {
                LastResult = Resources.Strings.Format("UpdatesReady", manifest.Version.ToString(3));
                Offer();
                return;
            }

            _log($"обновления: есть {manifest.Version.ToString(3)}, качаем");
            LastResult = Resources.Strings.Format("UpdatesDownloading", manifest.Version.ToString(3), 0, Megabytes(manifest.Size));

            // Свой срок, а не общий: тем же двадцатисекундным мерялась закачка
            // в полсотни мегабайт и обрывалась на девятнадцатой секунде.
            using CancellationTokenSource download = new(ChannelRequest.DownloadTimeout);

            var installer = await DownloadAsync(manifest, channel, download.Token).ConfigureAwait(true);
            if (installer is null)
            {
                LastResult = Resources.Strings.Get("UpdatesDownloadFailed");
                return;
            }

            _readyInstaller = installer;
            ReadyVersion = manifest.Version;
            LastResult = Resources.Strings.Format("UpdatesReady", manifest.Version.ToString(3));

            _log($"обновление {manifest.Version.ToString(3)} скачано и проверено");
            _announce(manifest.Version.ToString(3));

            Offer();
        }
        catch (PanelLinkException error)
        {
            // Подпись не сошлась — манифест отбрасывается целиком и молча не
            // остаётся: подделанный байт обязан быть виден в журнале.
            _log($"обновления ОТБРОШЕНЫ: {error.Message}");
            LastResult = Resources.Strings.Get("PresetsSignatureFailed");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            _log($"обновления: канал недоступен — {error.Message}");
            LastResult = Resources.Strings.Get("PresetsChannelDown");
        }
        finally
        {
            _isChecking = false;
            StatusChanged?.Invoke();
        }
    }

    /// <summary>
    /// Качает установщик и сверяет его с отпечатком из манифеста.
    /// </summary>
    ///
    /// <remarks>
    /// Сверка обязательна и делается до того, как файл кто-нибудь запустит:
    /// подпись манифеста говорит, что отпечаток наш, а отпечаток — что файл тот
    /// самый. Не сошёлся — файл стирается тут же: оставленный на диске
    /// установщик неизвестного происхождения хуже, чем отсутствие обновления.
    /// </remarks>
    /// <summary>Байты в мегабайтах, целыми: «23 из 54 МБ».</summary>
    private static long Megabytes(long bytes) => (bytes + 524_288) / 1_048_576;

    private async Task<string?> DownloadAsync(
        ReleaseManifest manifest,
        Provisioning.UpdateChannel channel,
        CancellationToken cancellation)
    {
        // В общий каталог, а не в профиль пользователя: отсюда выпуск заберёт
        // обновляльщик, работающий от SYSTEM, а до профиля оператора ему не
        // дотянуться — у SYSTEM свой. Файл здесь остаётся данными: запускать его
        // не будет ни приложение, ни обновляльщик, пока тот не перенесёт его в
        // `Program Files`, проверив подпись заново.
        var directory = UpdateHandoff.UpdatesDirectory;

        var path = Path.Combine(directory, $"EliteSIP-{manifest.Version.ToString(3)}.exe");

        try
        {
            Directory.CreateDirectory(directory);

            using HttpRequestMessage request = new(HttpMethod.Get, manifest.Url);
            request.Headers.TryAddWithoutValidation(
                "Authorization", Provisioning.BasicHeader(channel.User, channel.Password));

            // Заголовки — отдельно от тела: ответ читается потоком, кусками,
            // чтобы было что показать по ходу и было по чему заметить простой.
            using var response = await ChannelRequest.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(true);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _log($"обновления: установщик не отдан, канал ответил {(int)response.StatusCode}");
                return null;
            }

            var total = response.Content.Headers.ContentLength ?? manifest.Size;
            var version = manifest.Version.ToString(3);

            await using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(true);
            using MemoryStream buffer = new(total > 0 ? (int)Math.Min(total, int.MaxValue) : 0);

            var chunk = new byte[81920];
            var lastShown = DateTime.MinValue;

            while (true)
            {
                // Свой срок на каждый кусок: простой дольше минуты — это
                // оборванная связь, а не медленный канал.
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                stall.CancelAfter(StallTimeout);

                int read;
                try
                {
                    read = await body.ReadAsync(chunk, stall.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    _log($"обновления: закачка стоит дольше {StallTimeout.TotalSeconds:0} с "
                        + $"на {Megabytes(buffer.Length)} из {Megabytes(total)} МБ — обрываю");
                    throw new IOException("канал перестал отдавать данные");
                }

                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);

                // Не на каждый кусок: сотня обновлений надписи в секунду —
                // это занятый поток интерфейса, а глазу хватает двух.
                if (DateTime.UtcNow - lastShown > TimeSpan.FromMilliseconds(500))
                {
                    lastShown = DateTime.UtcNow;
                    LastResult = Resources.Strings.Format(
                        "UpdatesDownloading", version, Megabytes(buffer.Length), Megabytes(total));
                }
            }

            var bytes = buffer.ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(bytes));

            if (!string.Equals(digest, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _log("обновления ОТБРОШЕНЫ: отпечаток установщика не сошёлся");
                return null;
            }

            await File.WriteAllBytesAsync(path, bytes, cancellation).ConfigureAwait(true);

            return path;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
                                          or IOException or UnauthorizedAccessException)
        {
            _log($"обновления: установщик не скачался — {error.Message}");

            try
            {
                File.Delete(path);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Недокачанный файл переживёт себя до следующей закачки — она
                // перезапишет его целиком.
            }

            return null;
        }
    }
}
