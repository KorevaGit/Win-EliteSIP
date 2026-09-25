using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using EliteSIP.PanelLink;
using EliteSIP.Updater;

// Обновляльщик EliteSIP: ставит выпуск от имени SYSTEM.
//
// ------------------------------------------------------------------------------
// Зачем он вообще нужен
// ------------------------------------------------------------------------------
//
// Машины заказчика заперты политикой ограниченного использования программ (SRP):
// уровень по умолчанию «Запрещено», разрешены только `Program Files`,
// `Program Files (x86)` и `Windows`. Оператор при этом не администратор — писать
// в `Program Files` он не может, повышать права тоже.
//
// Значит приложение не может обновить себя само ни при каком раскладе: скачанный
// им файл лежит там, откуда запуск запрещён, а каталог, куда надо ставить, ему
// недоступен. Обновление делает этот код — он лежит в `Program Files` (разрешено)
// и запускается задачей планировщика от SYSTEM (SRP к SYSTEM не применяется).
//
// ------------------------------------------------------------------------------
// Кому здесь верят
// ------------------------------------------------------------------------------
//
// Каталог обмена `%PROGRAMDATA%\EliteSIP` открыт оператору на запись — иначе он
// не смог бы ни скачать туда выпуск, ни оставить отметку о согласии. Поэтому
// **ничему оттуда здесь не верят**: ни отметке, ни скачанному файлу. Подпись
// Ed25519 проверяется заново, ключом из `Program Files`, куда оператор не пишет,
// и только после этого файл переносится в `Program Files` и запускается оттуда.
//
// Худшее, чего добьётся подделавший содержимое каталога обмена, — установка
// нашего же подписанного выпуска.

var log = new UpdaterLog(Paths.Log);

// Поручение от установщика: выпуск встал, поднять софтфон в сеансе оператора.
//
// Отдельным запуском, потому что обновляльщик, начавший установку, её конца
// не ждёт: он передаёт выпуск задаче установки и выходит, освобождая свои
// файлы (разбор — у `HandOverToInstallTask`).
//
// Аргумент здесь безопасен в отличие от аргументов задачи планировщика: он
// ничего не выбирает — ни что запускать, ни откуда. Запускается всегда одна и
// та же программа рядом с этим файлом, и от имени оператора, а не от SYSTEM.
// Запущенный не от SYSTEM, он просто не получит сеанс оператора.
if (args.Contains("--relaunch"))
{
    var exe = Path.Combine(AppContext.BaseDirectory, "EliteSIP.App.exe");

    // Установщик зовёт нас из своего раздела [Run], то есть ещё живым, а
    // приложение, увидев его мьютекс, уходит, чтобы не помешать установке
    // (см. `SetupInProgress` в приложении). Поэтому сперва ждём, пока
    // установщик выйдет.
    WaitForSetupToExit(TimeSpan.FromMinutes(2));

    if (UserSession.TryStartInActiveSession(exe, out var why))
    {
        log.Write($"выпуск {InstalledVersion()} установлен, приложение поднято в сеансе оператора");
        TryDelete(Paths.RelaunchMarker);
    }
    else
    {
        // Отметка остаётся: её подберёт ближайший такт задачи.
        log.Write($"выпуск {InstalledVersion()} установлен, приложение НЕ поднято ({why}) — "
            + "попробую ещё раз на следующем такте");
    }

    return 0;
}

// Задача просыпается каждые десять минут, а установка идёт минуты. Второй
// экземпляр посреди первого — это два установщика на одних файлах.
using var single = new Mutex(initiallyOwned: true, @"Global\EliteSIP.Updater", out var mine);
if (!mine)
{
    return 0;
}

try
{
    return await RunAsync().ConfigureAwait(false);
}
catch (Exception error)
{
    log.Write($"неожиданный отказ: {error}");
    return 1;
}

async Task<int> RunAsync()
{
    RelaunchIfPending();

    // Отметки нет — обычное состояние, и молчать о нём надо тоже обычно: строка
    // в журнале каждые десять минут утопила бы в себе всё остальное.
    var wanted = Request.Read(Paths.Request);

    // Уборка идёт на такте, а не сразу после установки, и это не лень.
    // Обновляльщик, начавший установку, до её конца не доживает: установщик
    // заменяет его файлы и закрывает его самого (разбор — у
    // `HandOverToInstallTask`). Убирать за собой ему просто нечем, а вот
    // следующий такт уже видит, какой выпуск встал.
    PruneUpdates(wanted);

    if (wanted is null)
    {
        return 0;
    }

    log.Write($"оператор согласился на {wanted}");

    var secrets = Provisioning.Read(AppContext.BaseDirectory);
    if (secrets?.Updates is null)
    {
        log.Write("в заводской настройке рядом с программой нет канала — ставить нечего и неоткуда");
        return 1;
    }

    if (secrets.PublicKey() is not { } publicKey)
    {
        log.Write("в заводской настройке нет открытого ключа линии — проверить подпись нечем");
        return 1;
    }

    var manifest = await FetchManifestAsync(secrets.Updates, publicKey).ConfigureAwait(false);
    if (manifest is null)
    {
        return 1;
    }

    // Ставится ровно то, на что согласился оператор. Если на канале успел
    // появиться выпуск новее, отметка не подходит: спросить о нём должно
    // приложение, а не этот код, у которого спрашивать не у кого.
    if (manifest.Version != wanted)
    {
        log.Write($"на канале {manifest.Version}, а согласие было на {wanted} — жду нового согласия");
        Request.Clear(Paths.Request);
        return 0;
    }

    var installed = InstalledVersion();
    if (installed is not null && !manifest.IsNewerThan(installed))
    {
        log.Write($"уже стоит {installed} — ставить нечего");
        Request.Clear(Paths.Request);
        return 0;
    }

    var payload = await AcquireAsync(manifest, secrets.Updates).ConfigureAwait(false);
    if (payload is null)
    {
        return 1;
    }

    // Перенос в `Program Files` — и есть та граница, после которой файлу можно
    // доверять: писать сюда может только администратор и SYSTEM, а SRP отсюда
    // разрешает запуск.
    var trusted = Path.Combine(Paths.TrustedUpdates, $"EliteSIP-{manifest.Version.ToString(3)}.exe");
    Directory.CreateDirectory(Paths.TrustedUpdates);
    await File.WriteAllBytesAsync(trusted, payload).ConfigureAwait(false);

    // Отметка снимается до установки, а не после. Установка заменяет файлы под
    // нами и может кончиться чем угодно, вплоть до перезагрузки; уцелевшая
    // отметка означала бы установку по кругу каждые десять минут. Что выпуск не
    // встал, приложение увидит по своей версии и предложит снова.
    Request.Clear(Paths.Request);

    // Отметка «поднять после установки» — до установки: установщик закроет
    // софтфон, и вернуть его обязан кто-то, кто переживёт установку.
    await File.WriteAllTextAsync(Paths.RelaunchMarker, manifest.Version.ToString(3)).ConfigureAwait(false);

    if (!HandOverToInstallTask(trusted, manifest.Version))
    {
        TryDelete(Paths.RelaunchMarker);
        return 1;
    }

    // Дальше — не наше дело, и это намеренно. Установщик заменит в том числе
    // этот файл и библиотеки, которые мы держим, пока работаем; конец
    // установки и подъём приложения запишет новый обновляльщик (`--relaunch`
    // из установщика).
    log.Write($"выпуск {manifest.Version} передан задаче установки");
    return 0;
}

/// <summary>Страховочный подъём софтфона после установки.</summary>
///
/// <remarks>
/// Прямой подъём — `--relaunch` из установщика — может сорваться: за машиной
/// в тот момент никого, сеанс заблокирован, процесс унесло вместе с заданием
/// планировщика. Отметка «поднять после установки» ставится до установки и
/// снимается только удачным подъёмом, а каждый такт задачи — раз в десять
/// минут — проверяет её. Так софтфон после обновления возвращается не позже
/// следующего такта, а не «при следующем входе в систему».
///
/// Отметка лежит в `{app}\updates` — писать туда может только администратор,
/// то есть оператор не заставит SYSTEM ничего поднимать. И поднимается всегда
/// одна и та же программа рядом с этим файлом.
/// </remarks>
void RelaunchIfPending()
{
    if (!File.Exists(Paths.RelaunchMarker))
    {
        return;
    }

    // Сутки — предел: отметка от установки, которая так и не встала, не
    // должна поднимать софтфон через неделю, когда его закрыли намеренно.
    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(Paths.RelaunchMarker) > TimeSpan.FromDays(1))
    {
        TryDelete(Paths.RelaunchMarker);
        return;
    }

    var wanted = Version.TryParse(ReadText(Paths.RelaunchMarker), out var version) ? version : null;
    var installed = InstalledVersion();

    // Выпуск ещё не встал — поднимать нечего и рано.
    if (wanted is not null && (installed is null || installed.CompareTo(wanted) < 0))
    {
        return;
    }

    if (UserSession.IsRunningInActiveSession("EliteSIP.App"))
    {
        TryDelete(Paths.RelaunchMarker);
        return;
    }

    // Установка ещё идёт — поднятое сейчас приложение ушло бы само.
    if (Mutex.TryOpenExisting(@"Global\EliteSIP.Setup", out var setup))
    {
        setup.Dispose();
        return;
    }

    var exe = Path.Combine(AppContext.BaseDirectory, "EliteSIP.App.exe");
    if (UserSession.TryStartInActiveSession(exe, out var reason))
    {
        log.Write($"выпуск {installed} стоит, софтфона не было — поднят на такте задачи");
        TryDelete(Paths.RelaunchMarker);
    }
    else
    {
        log.Write($"софтфон после установки всё ещё не поднят ({reason})");
    }
}

/// <summary>Убирает выпуски, которые уже никому не нужны.</summary>
///
/// <remarks>
/// <para>
/// <b>Зачем это вообще понадобилось.</b> Выпуск самодостаточный и весит под
/// шестьдесят мегабайт, а ложится он дважды: скачанное приложением в общем
/// каталоге и перенесённое сюда, в <c>Program Files</c>. До 0.1.56 не убиралось
/// ни то ни другое — на машине разработки за один день проверок накопился почти
/// гигабайт в двух каталогах. На боевой машине выпуски редки, но растёт это без
/// предела, а места на таких машинах обычно немного.
/// </para>
/// <para>
/// <b>Свой каталог, и только свой.</b> Общий чистит приложение — не из
/// вежливости, а потому, что каталог обмена открыт оператору на запись. Всё,
/// что SYSTEM удаляет по пути, который оператор может подменить связкой, — это
/// удаление чужого файла его руками. Здесь же пишет только администратор.
/// </para>
/// <para>
/// <b>Один установщик остаётся.</b> Тот, чей выпуск стоит сейчас: по нему чинят
/// установку, не ходя в сеть, и стоит это ровно одного файла.
/// </para>
/// <para>
/// <b>Ничего моложе часа не трогается.</b> Между «задача установки заведена» и
/// «установщик открыл файл» проходят секунды; стереть выпуск в эту щель значило
/// бы отменить обновление, на которое оператор уже согласился. Запущенный файл
/// Windows и так удалить не даст, но полагаться на одну эту защиту здесь
/// незачем.
/// </para>
/// </remarks>
void PruneUpdates(Version? pending)
{
    var installed = InstalledVersion();

    string[] installers;
    try
    {
        installers = Directory.Exists(Paths.TrustedUpdates)
            ? Directory.GetFiles(Paths.TrustedUpdates, "EliteSIP-*.exe")
            : [];
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        return;
    }

    foreach (var installer in installers)
    {
        var version = VersionOf(installer);

        if (version is null
            || SameRelease(version, installed)
            || SameRelease(version, pending)
            || IsFresh(installer))
        {
            continue;
        }

        try
        {
            File.Delete(installer);
            log.Write($"убран выпуск {version}: он больше не нужен");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Занят — значит его прямо сейчас ставят. Уберём на следующем такте.
        }
    }

    PruneInstallLogs();
}

/// <summary>
/// Оставляет три последних журнала установки.
/// </summary>
///
/// <remarks>
/// Они по сто пятьдесят килобайт и копятся тем же чередом, что и выпуски.
/// Три — потому что разбирают всегда последнюю установку, а две прежние нужны
/// ровно для того, чтобы было с чем сравнить.
/// </remarks>
void PruneInstallLogs()
{
    const int keep = 3;

    try
    {
        if (!Directory.Exists(Paths.TrustedUpdates))
        {
            return;
        }

        var stale = new DirectoryInfo(Paths.TrustedUpdates)
            .GetFiles("install-*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(keep);

        foreach (var file in stale)
        {
            TryDelete(file.FullName);
        }
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        // Каталога нет или он закрыт — убирать нечего и нечем.
    }
}

/// <summary>Версия из имени файла: <c>EliteSIP-0.1.56.exe</c> → 0.1.56.</summary>
static Version? VersionOf(string path)
{
    const string prefix = "EliteSIP-";

    var name = Path.GetFileNameWithoutExtension(path);

    return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && Version.TryParse(name[prefix.Length..], out var version)
            ? version
            : null;
}

/// <summary>
/// Один ли это выпуск.
/// </summary>
///
/// <remarks>
/// Сравнение по трём числам, а не целиком: в имени файла версия трёхчастная
/// (<c>0.1.56</c>), а у поставленного файла она четырёхчастная (<c>0.1.56.0</c>),
/// и обычное равенство их никогда не сведёт.
/// </remarks>
static bool SameRelease(Version? left, Version? right) =>
    left is not null && right is not null
        && left.Major == right.Major
        && left.Minor == right.Minor
        && left.Build == right.Build;

/// <summary>Моложе часа — значит, может ещё пригодиться идущей установке.</summary>
static bool IsFresh(string path)
{
    try
    {
        return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(1);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        // Не прочиталось — считаем свежим и не трогаем.
        return true;
    }
}

/// <summary>Ждёт, пока установщик EliteSIP не отпустит свой мьютекс.</summary>
static void WaitForSetupToExit(TimeSpan limit)
{
    var deadline = DateTime.UtcNow + limit;

    while (DateTime.UtcNow < deadline)
    {
        if (!Mutex.TryOpenExisting(@"Global\EliteSIP.Setup", out var mutex))
        {
            return;
        }

        mutex.Dispose();
        Thread.Sleep(500);
    }
}

static string? ReadText(string path)
{
    try
    {
        return File.ReadAllText(path).Trim();
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}

static void TryDelete(string path)
{
    try
    {
        File.Delete(path);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        // Не снялась — снимет следующий такт.
    }
}

/// <summary>Ставит выпуск отдельной задачей и сразу возвращается.</summary>
///
/// <remarks>
/// <para>
/// <b>Почему не дочерним процессом.</b> До 0.1.48 установщик запускался
/// отсюда и ждался. Но этот процесс лежит в `Program Files\EliteSIP` и держит
/// свои файлы и общие библиотеки .NET — ровно те, что установщик обязан
/// заменить. Установщик упирался в занятый файл и показывал окно «файл
/// занят» в нулевом сеансе, где его никто не увидит; мы ждали его, он ждал
/// ответа. На живой машине 11 сентября 2026 так висели 0.1.45 и 0.1.47, а
/// задача обновления числилась «выполняется» и отклоняла все следующие такты
/// — обновления просто переставали проверяться.
/// </para>
/// <para>
/// Своя задача SYSTEM, а не просто запуск без ожидания: дочерний процесс
/// живёт в объекте задания планировщика, и судьба его после нашего выхода
/// зависит от настроек, которых мы не видим. Отдельная задача от нас не
/// зависит вовсе.
/// </para>
/// <para>
/// Задачу регистрируем сами и с явными настройками: `schtasks` по умолчанию
/// запрещает старт от батареи, и на ноутбуке установка тихо не начиналась бы.
/// Срок — полчаса: зависшая установка снимается сама и не держит задачу.
/// </para>
/// <para>
/// Журнал установщика — в `{app}\updates`, куда пишет только администратор:
/// в общий каталог SYSTEM писать не должен, там оператор может подложить
/// ссылку на чужой файл.
/// </para>
/// </remarks>
bool HandOverToInstallTask(string installer, Version version)
{
    var installLog = Path.Combine(Paths.TrustedUpdates, $"install-{version.ToString(3)}.log");

    // `/VERYSILENT /SUPPRESSMSGBOXES`: ни окна, ни вопросов. Если установщик
    // что-то спросит, ответ по умолчанию он даст сам — спрашивать в нулевом
    // сеансе некого.
    var arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{installLog}\"";

    string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    var script = string.Join("; ",
        "$ErrorActionPreference = 'Stop'",
        $"$a = New-ScheduledTaskAction -Execute {Quote(installer)} -Argument {Quote(arguments)}",
        "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries "
            + "-ExecutionTimeLimit (New-TimeSpan -Minutes 30)",
        "$p = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest",
        "Register-ScheduledTask -TaskPath '\\EliteSIP\\' -TaskName 'Install' -Action $a -Settings $s "
            + "-Principal $p -Force | Out-Null",
        "Start-ScheduledTask -TaskPath '\\EliteSIP\\' -TaskName 'Install'");

    // Закодированной командой, а не строкой: пути с пробелами и кавычками
    // внутри аргументов не переживают двух слоёв разбора командной строки.
    var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
    };

    start.ArgumentList.Add("-NoProfile");
    start.ArgumentList.Add("-NonInteractive");
    start.ArgumentList.Add("-ExecutionPolicy");
    start.ArgumentList.Add("Bypass");
    start.ArgumentList.Add("-EncodedCommand");
    start.ArgumentList.Add(encoded);

    try
    {
        using var process = Process.Start(start);
        if (process is null)
        {
            log.Write("задача установки не заведена: PowerShell не запустился");
            return false;
        }

        var errors = process.StandardError.ReadToEnd();

        if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds) || process.ExitCode != 0)
        {
            log.Write($"задача установки не заведена: {errors.Trim()}");
            return false;
        }

        return true;
    }
    catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
    {
        log.Write($"задача установки не заведена: {error.Message}");
        return false;
    }
}

async Task<ReleaseManifest?> FetchManifestAsync(Provisioning.UpdateChannel channel, PanelPublicKey publicKey)
{
    if (channel.ReleasesUrl() is not { } url)
    {
        log.Write("адрес манифеста не сложился");
        return null;
    }

    try
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", channel.BasicHeader());

        using var response = await Http.Client.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            log.Write($"манифест не отдан, канал ответил {(int)response.StatusCode}");
            return null;
        }

        var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        return ReleaseManifest.Verified(data, publicKey);
    }
    catch (PanelLinkException error)
    {
        // Подпись не сошлась — это не «канал шалит», это подменённый манифест.
        log.Write($"манифест ОТБРОШЕН: {error.Message}");
        return null;
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
    {
        log.Write($"канал недоступен: {error.Message}");
        return null;
    }
}

/// <summary>Берёт выпуск: сперва скачанный приложением, иначе качает сам.</summary>
async Task<byte[]?> AcquireAsync(ReleaseManifest manifest, Provisioning.UpdateChannel channel)
{
    var staged = Path.Combine(Paths.SharedUpdates, $"EliteSIP-{manifest.Version.ToString(3)}.exe");

    if (File.Exists(staged))
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(staged).ConfigureAwait(false);
            if (Matches(bytes, manifest.Sha256))
            {
                log.Write("взят выпуск, скачанный приложением");
                return bytes;
            }

            // Не сошёлся — то ли докачан не до конца, то ли подменён. Разбирать
            // незачем: качаем сами.
            log.Write("скачанное приложением не сошлось по отпечатку — качаю сам");
        }
        catch (IOException error)
        {
            log.Write($"скачанное приложением не прочиталось: {error.Message}");
        }
    }

    // Пару Basic — только хосту манифеста (разбор у `ReleaseManifest.IsServedFrom`).
    if (channel.ReleasesUrl() is not { } manifestAddress || !manifest.IsServedFrom(manifestAddress))
    {
        log.Write($"выпуск {manifest.Version} лежит на другом хосте ({manifest.Url.Authority}), "
            + "чем манифест — не качаю");
        return null;
    }

    try
    {
        using HttpRequestMessage request = new(HttpMethod.Get, manifest.Url);
        request.Headers.TryAddWithoutValidation("Authorization", channel.BasicHeader());

        using var response = await Http.Client.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            log.Write($"выпуск не отдан, канал ответил {(int)response.StatusCode}");
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (!Matches(bytes, manifest.Sha256))
        {
            log.Write("выпуск ОТБРОШЕН: отпечаток не сошёлся");
            return null;
        }

        return bytes;
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
    {
        log.Write($"выпуск не скачался: {error.Message}");
        return null;
    }
}

static bool Matches(byte[] bytes, string expected)
    => string.Equals(
        Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase);

/// <summary>Версия, которая стоит сейчас. <c>null</c> — прочитать не удалось.</summary>
static Version? InstalledVersion()
{
    try
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EliteSIP.App.exe");
        var raw = FileVersionInfo.GetVersionInfo(exe).FileVersion;

        return Version.TryParse(raw, out var version) ? version : null;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}


/// <summary>Общий клиент. Кэш запрещён — как и у приложения.</summary>
internal static class Http
{
    internal static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        // Четверть часа: выпуск весит полсотни мегабайт, и на плохом канале
        // конторы это минуты. У приложения тот же срок — см. `ChannelRequest`.
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders =
        {
            CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
            },
        },
    };
}
