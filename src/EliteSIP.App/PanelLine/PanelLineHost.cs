using System.IO;
using System.Windows.Threading;
using EliteSIP.AdminAccess;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Линия Spark целиком: конфигурация машины, предустановки, отзыв и
/// регистрация ключа.
/// </summary>
///
/// <remarks>
/// <para>
/// С 0.1.58 машина живёт по схеме «привязка по машине»: ключей активации нет,
/// номер, SIP-пароль, площадку, предустановку и пароль настроек присылает
/// Spark конфигурацией <c>config/&lt;id&gt;</c>. Раз в пятнадцать минут (и при
/// запуске) линия спрашивает <c>config/</c> и <c>revoked/</c>; файл
/// предустановок — по-прежнему в общем двухчасовом такте с обновлениями.
/// </para>
/// <para>
/// <b>Оффлайн.</b> Машина, на которой в «Управлении» сохранили правки,
/// перестаёт слушать Spark: конфигурация и предустановки не применяются,
/// отзыв — применяется всё равно. Вернуть её — <see cref="ReturnOnline"/>.
/// </para>
/// <para>
/// Всё приехавшее применяется на потоке окна: запросы идут на пуле, а
/// применение трогает настройки, телефон и окна — и сброс по отзыву закрывает
/// приложение, чего с чужого потока не сделать.
/// </para>
/// </remarks>
internal sealed class PanelLineHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly AdminAccessState _access;
    private readonly Action<string> _log;
    private readonly Func<bool> _isBlocked;
    private readonly Dispatcher _dispatcher;

    private readonly PresetService _presets;
    private readonly MachineService _machine;

    private readonly DispatcherTimer _machineTimer;

    /// <summary>
    /// Конфигурация, пришедшая посреди разговора или при открытом «Управлении».
    /// </summary>
    ///
    /// <remarks>
    /// Только в памяти: закроют приложение — та же ревизия приедет следующим
    /// опросом, потому что применённой она не записана.
    /// </remarks>
    private MachineConfig? _deferredConfig;

    /// <summary>Отзыв, пришедший посреди разговора: сбросить сразу после него.</summary>
    private Revocation? _deferredRevocation;

    private readonly Action _reset;

    internal PanelLineHost(
        AppSettings settings,
        AdminAccessState access,
        Func<bool> isBlocked,
        Action reset,
        Action<string> log)
    {
        _settings = settings;
        _access = access;
        _isBlocked = isBlocked;
        _reset = reset;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _presets = new PresetService(() => _settings, ApplyPreset, isBlocked, log);
        _presets.NoteContact = () => _settings.Panel.LastContactAt = DateTimeOffset.UtcNow;

        _machine = new MachineService(
            () => _settings,
            access => OnUi(() => ApplyMachineAccess(access)),
            revocation => OnUi(() => ResetByRevocation(revocation)),
            log)
        {
            ApplyConfig = config => OnUi(() => ReceiveConfig(config)),
        };

        // Пятнадцать минут: столько ждёт правка сотрудника в Spark и столько же
        // живёт отвязанная машина. Предустановки — в общем двухчасовом такте с
        // обновлениями (`UpdateService`): канал один, и два независимых срока
        // на нём разошлись бы через полгода.
        _machineTimer = new DispatcherTimer { Interval = MachineService.RevocationInterval };
        _machineTimer.Tick += async (_, _) =>
        {
            _log($"такт конфигурации и отзыва; следующий через {MachineService.RevocationInterval.TotalMinutes:0} мин");
            await CheckMachineAsync().ConfigureAwait(true);
        };
    }

    internal Action<bool, string?>? Report
    {
        get => _presets.Report;
        set => _presets.Report = value;
    }

    /// <summary>
    /// Перерегистрироваться: сменились номер, SIP-пароль, площадка или адрес
    /// АТС. Ставит приложение — телефон его.
    /// </summary>
    internal Action<string>? Reregister { get; set; }

    /// <summary>Сказать в панели «Администратор сменил номер: 205».</summary>
    internal Action<string>? AnnounceNumber { get; set; }

    /// <summary>Сменилось что-то, что показывают окна: онлайн, оффлайн, ревизии.</summary>
    internal event Action? StateChanged;

    internal void Start()
    {
        _machineTimer.Start();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        await RegisterLegacyMachineAsync().ConfigureAwait(true);
        await CheckMachineAsync().ConfigureAwait(true);
        await CheckAsync().ConfigureAwait(true);
    }

    /// <summary>Общий двухчасовой такт и кнопка «Проверить настройки сейчас».</summary>
    internal async Task CheckAsync()
    {
        await _presets.CheckAsync().ConfigureAwait(true);

        // Доступ старого образца нужен только машинам, поднятым ключом, пока
        // они не зарегистрировали свой ключ: остальным пароль настроек
        // приходит конфигурацией.
        if (!_settings.Panel.MachineKeyRegistered && !_settings.Panel.IsOffline)
        {
            await _machine.CheckAccessAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Свои объекты: конфигурация и отзыв.</summary>
    internal async Task CheckMachineAsync()
    {
        await _machine.CheckRevocationAsync().ConfigureAwait(true);

        if (!_settings.Panel.IsOffline)
        {
            await _machine.CheckConfigAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Вернуть машину из оффлайна под Spark — кнопка «Вернуться в онлайн».
    /// </summary>
    ///
    /// <remarks>
    /// Ревизии обнуляются намеренно: обычное правило «применяем то, что новее
    /// применённого» не вернуло бы ничего — за время оффлайна ревизия в Spark
    /// могла не меняться, а местные правки накопились. Запрос идёт сразу, а не
    /// через пятнадцать минут и не через два часа.
    /// </remarks>
    internal async Task ReturnOnline()
    {
        if (!_settings.Panel.HasChannelKey)
        {
            return;
        }

        _settings.Panel.Mode = PanelMode.Managed;
        _settings.Panel.WantsResync = true;
        _settings.Panel.AppliedConfigRevision = 0;
        _log("машина возвращена в онлайн: местные правки заменит Spark");
        StateChanged?.Invoke();

        await CheckMachineAsync().ConfigureAwait(true);
        await CheckAsync().ConfigureAwait(true);
    }

    internal void HostBecameIdle()
    {
        if (_deferredRevocation is { } revocation)
        {
            _deferredRevocation = null;
            ResetByRevocation(revocation);
            return;
        }

        if (_deferredConfig is { } config)
        {
            _deferredConfig = null;
            ReceiveConfig(config);
        }

        _presets.HostBecameIdle();
    }

    public void Dispose()
    {
        _machineTimer.Stop();
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>
    /// Машина, поднятая ключом активации: зарегистрировать свой ключ машины.
    /// </summary>
    ///
    /// <remarks>
    /// Ключ сохраняется до запроса, а повтор предъявляет тот же: Spark
    /// принимает его один раз. Не вышло по сети — повтор на следующем запуске;
    /// 401 — ключ канала не тот или машину отвязали, и тогда её судьбу решит
    /// отзыв, а не этот код.
    /// </remarks>
    private async Task RegisterLegacyMachineAsync()
    {
        var panel = _settings.Panel;
        if (!panel.HasChannelKey || panel.MachineKeyRegistered)
        {
            return;
        }

        var channelKey = panel.ChannelKey();
        if (channelKey is null)
        {
            return;
        }

        var stored = panel.MachineKey();
        var machine = stored is null ? MachineKeyPair.Generate() : MachineKeyPair.FromBase64(stored);
        if (stored is null)
        {
            panel.SetMachineKey(machine.PrivateKeyBase64);
        }

        var outcome = await SparkPairing
            .RegisterMachineAsync(panel.InstallationID, channelKey, machine, CancellationToken.None)
            .ConfigureAwait(true);

        if (outcome is MachineRegistration.Registered)
        {
            panel.MachineKeyRegistered = true;
        }

        _log(outcome switch
        {
            MachineRegistration.Registered => "ключ машины зарегистрирован в Spark: дальше номер и настройки приходят конфигурацией",
            MachineRegistration.Rejected => "Spark не принял ключ канала при регистрации ключа машины (401) — машину отвязали или ключ не тот",
            _ => "регистрация ключа машины не дошла до Spark — повтор при следующем запуске",
        });
    }

    private void ReceiveConfig(MachineConfig config)
    {
        var panel = _settings.Panel;

        if (panel.IsOffline)
        {
            return;
        }

        if (config.Revision <= panel.AppliedConfigRevision)
        {
            panel.LastContactAt = DateTimeOffset.UtcNow;
            return;
        }

        if (_isBlocked())
        {
            _deferredConfig = config;
            _log($"конфигурация {config.Revision} ждёт: разговор или открытое «Управление»");
            return;
        }

        var change = _settings.Apply(config, _access, _log);
        _log($"конфигурация {config.Revision} применена: номер {config.Number}");
        StateChanged?.Invoke();

        if (change.NewNumber is { } number)
        {
            AnnounceNumber?.Invoke(number);
        }

        if (change.RegistrationChanged)
        {
            Reregister?.Invoke("конфигурация из Spark");
        }

        // Другая предустановка — сейчас, а не через два часа.
        if (change.PresetChanged)
        {
            _ = _presets.CheckAsync();
        }
    }

    private void ApplyPreset(PresetBundle.Entry entry)
    {
        var wasResync = _settings.Panel.WantsResync;
        var domainBefore = _settings.Account.Domain;

        _settings.Apply(ManagedFields.Parse(entry.Fields));

        _settings.Panel.PresetName = entry.Name;
        _settings.Panel.AppliedRevision = entry.Revision;
        _settings.Panel.AppliedAt = DateTimeOffset.UtcNow;

        // Просьба выполнена — снимается здесь и только здесь. Оставленный
        // признак означал бы, что машина переприменяет предустановку каждые два
        // часа, затирая ей же разрешённое локальное.
        _settings.Panel.WantsResync = false;

        // Адрес регистрации — из пары адресов предустановки по площадке. До
        // 0.1.58 предустановка меняла пару адресов, а регистрация оставалась на
        // прежнем: новый адрес АТС начинал работать только после перезапуска.
        var wanted = _settings.Account.Site is WorkplaceSite.Remote
            ? _settings.Pbx.RemoteAddress
            : _settings.Pbx.OfficeAddress;

        if (wanted.Length > 0)
        {
            _settings.Account.Domain = wanted;
        }

        // И у неактивных профилей: иначе переключение на второй номер
        // регистрировалось бы по прежнему адресу АТС.
        foreach (var profile in _settings.Profiles)
        {
            var address = profile.Site is WorkplaceSite.Remote ? _settings.Pbx.RemoteAddress : _settings.Pbx.OfficeAddress;
            if (address.Length > 0)
            {
                profile.Domain = address;
            }
        }

        _log(wasResync
            ? $"предустановка переприменена по просьбе машины: «{entry.Name}», ревизия {entry.Revision}"
            : $"предустановка применена: «{entry.Name}», ревизия {entry.Revision}");

        StateChanged?.Invoke();

        if (_settings.Account.Domain != domainBefore)
        {
            Reregister?.Invoke("адрес АТС из предустановки");
        }
    }

    private void ApplyMachineAccess(MachineAccess access)
    {
        if (access.AdminPassword.Length == 0 || _settings.Panel.IsOffline)
        {
            return;
        }

        if (_settings.Admin.ToCredential()?.Matches(access.AdminPassword) is true)
        {
            return;
        }

        try
        {
            // Не через `SetPassword`: тот требует открытого режима — иначе
            // кнопка «сменить пароль» была бы обходом пароля. Здесь пароль
            // меняет не человек за столом, а Spark.
            var credential = AdminCredential.Create(access.AdminPassword);
            _settings.Admin.From(credential);
            _access.Restore(credential);

            _log("административный пароль приехал из Spark");
        }
        catch (AdminAccessException error)
        {
            _log($"административный пароль из Spark не применён: {error.Message}");
        }
    }

    private void ResetByRevocation(Revocation revocation)
    {
        if (_isBlocked())
        {
            // Отложенный сброс держится в памяти и срабатывает сразу по концу
            // разговора, а не через пятнадцать минут следующего опроса.
            _deferredRevocation = revocation;
            _log($"машина отвязана в Spark, сброс ждёт конца разговора: {revocation.InstallationID}");
            return;
        }

        // Строка пишется до сброса, хотя журнал он и стирает: в те несколько
        // мгновений, что она живёт, её видит открытая «Диагностика».
        _log($"машина отвязана в Spark — полная чистка: {revocation.InstallationID}");

        _reset();
    }
}

/// <summary>Полный сброс машины: по отзыву и по кнопке.</summary>
internal static class MachineReset
{
    /// <summary>
    /// Стирает всё, что машина накопила.
    /// </summary>
    ///
    /// <remarks>
    /// Файлами, а не обнулением объектов в памяти: история и журнал живут в
    /// своих файлах, и обход по настройкам их не тронул бы. Что не стёрлось —
    /// пишется в возвращаемый список, но чистка от этого не останавливается:
    /// занятый журнал не повод оставить на диске пароль SIP.
    ///
    /// Ключи машины и канала уходят вместе с настройками и <c>pairing.json</c>:
    /// после сброса машина показывает новый код и привязывается заново.
    /// </remarks>
    internal static IReadOnlyList<string> Wipe()
    {
        var directory = Path.GetDirectoryName(AppSettings.DefaultPath)!;
        var failures = new List<string>();

        foreach (var name in new[]
        {
            Path.GetFileName(AppSettings.DefaultPath),
            PairingStore.FileName,
            "history.db",
            "history.db-wal",
            "history.db-shm",
            "activation-draft.dat",
            "elitesip.log",
            "crash.log",
        })
        {
            try
            {
                File.Delete(Path.Combine(directory, name));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failures.Add(name);
            }
        }

        // Ротированные файлы журнала: elitesip-<время>.log.
        try
        {
            foreach (var rotated in Directory.GetFiles(directory, "elitesip-*.log"))
            {
                try
                {
                    File.Delete(rotated);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failures.Add(Path.GetFileName(rotated));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failures.Add("elitesip-*.log");
        }

        return failures;
    }
}
