using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;
using AppearanceMode = EliteSIP.App.Theme.Appearance;

namespace EliteSIP.App.FirstRun;

/// <summary>Шаги мастера первого запуска.</summary>
public enum FirstRunStep
{
    Welcome,

    /// <summary>
    /// Привязка: код и QR на экране, администратор привязывает машину в Spark.
    /// </summary>
    Pair,

    /// <summary>Ручная настройка — вторая дорога, для машины без Spark.</summary>
    User,

    Appearance,
    Finale,
}

/// <summary>
/// Мастер первого запуска.
/// </summary>
///
/// <remarks>
/// <para>
/// С 0.1.58 ключей активации нет. Машина сама создаёт свои ключи и показывает
/// код и QR; администратор вводит код в Spark или сканирует QR в EliteSIP
/// Connect. Мастер ждёт длинным опросом, а дождавшись — забирает конфигурацию
/// и предустановку и настраивается сам: номер, пароль, адрес АТС и пароль
/// настроек приходят из Spark, вводить ничего не нужно.
/// </para>
/// <para>
/// Код переживает перезапуск: сессия лежит в <c>pairing.json</c>, и каждый
/// опрос продлевает её на семь дней. Машину можно поставить заранее, а
/// привязать через несколько дней — код на экране будет тот же.
/// </para>
/// </remarks>
public sealed class FirstRunViewModel : Observable, IDisposable
{
    /// <summary>Паузы между повторами при сбое сети: 5, 10, … 30 с.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings;
    private readonly AdminAccessState _access;

    private FirstRunStep _step = FirstRunStep.Welcome;
    private LanguageSetting _language;
    private AppearanceMode _theme;
    private string _username = string.Empty;
    private string _displayName = string.Empty;
    private string _sipPassword = string.Empty;
    private string _officeAddress = string.Empty;
    private string _adminPassword = string.Empty;
    private string _repeatedAdminPassword = string.Empty;

    private PairingState? _pairing;
    private CancellationTokenSource? _pairingLoop;
    private string? _pairCode;
    private ImageSource? _pairQr;
    private string? _pairStatus;
    private bool _pairFailed;
    private bool _isPaired;
    private string _pairedEmployee = string.Empty;
    private string _pairedNumber = string.Empty;

    public FirstRunViewModel(AppSettings settings, AdminAccessState access)
    {
        _settings = settings;
        _access = access;
        _language = settings.Appearance.Language;
        _theme = settings.Appearance.Theme;
    }

    public FirstRunStep Step
    {
        get => _step;
        private set
        {
            Set(ref _step, value);
            foreach (var name in new[]
            {
                nameof(ShowsWelcome), nameof(ShowsPair), nameof(ShowsUser), nameof(ShowsAppearance),
                nameof(ShowsFinale),
                nameof(CanGoBack), nameof(CanGoForward), nameof(IsLastStep),
            })
            {
                NotifyChanged(name);
            }

            // Код показывается — ждём привязки; ушли с экрана — перестаём.
            if (value is FirstRunStep.Pair)
            {
                StartPairing();
            }
            else
            {
                StopPairing();
            }
        }
    }

    public bool ShowsWelcome => _step is FirstRunStep.Welcome;

    public bool ShowsPair => _step is FirstRunStep.Pair;

    // MARK: - Привязка

    /// <summary>Есть ли канал: без заводской настройки привязывать некуда.</summary>
    public static bool HasChannel => PanelLine.Provisioning.Current?.Updates is not null;

    /// <summary>См. <see cref="HasChannel"/>; экземплярным — ради привязки разметки.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Пометьте члены как статические",
        Justification = "Статическое свойство разметка не находит: привязка ищет его у объекта.")]
    public bool HasNoChannel => !HasChannel;

    /// <summary>Куда писать о ходе привязки. <c>null</c> — некуда.</summary>
    internal Action<string>? Log { get; init; }

    /// <summary>Код с экрана: «482-915».</summary>
    public string? PairCode
    {
        get => _pairCode;
        private set
        {
            Set(ref _pairCode, value);
            NotifyChanged(nameof(HasPairCode));
        }
    }

    public bool HasPairCode => _pairCode is not null && !_isPaired;

    /// <summary>QR строки <c>elitesip://pair?s=…&amp;f=…</c> для EliteSIP Connect.</summary>
    public ImageSource? PairQr
    {
        get => _pairQr;
        private set => Set(ref _pairQr, value);
    }

    /// <summary>Что происходит: «ждём привязки», «нет связи со Spark, повтор через 10 с».</summary>
    public string? PairStatus
    {
        get => _pairStatus;
        private set
        {
            Set(ref _pairStatus, value);
            NotifyChanged(nameof(HasPairStatus));
        }
    }

    public bool HasPairStatus => _pairStatus is not null;

    /// <summary>Строка статуса тревожная — красным.</summary>
    public bool PairFailed
    {
        get => _pairFailed;
        private set => Set(ref _pairFailed, value);
    }

    /// <summary>Машина привязана и настроена — дальше пускает «Далее».</summary>
    public bool IsPaired
    {
        get => _isPaired;
        private set
        {
            Set(ref _isPaired, value);
            NotifyChanged(nameof(CanGoForward));
            NotifyChanged(nameof(HasPairCode));
        }
    }

    /// <summary>Кому привязали — имя сотрудника из Spark.</summary>
    public string PairedEmployee
    {
        get => _pairedEmployee;
        private set => Set(ref _pairedEmployee, value);
    }

    public string PairedNumber
    {
        get => _pairedNumber;
        private set => Set(ref _pairedNumber, value);
    }

    /// <summary>
    /// «Это не я — показать новый код»: сотрудник продиктовал не тот код или
    /// показал экран чужой машины. Ключи те же, сессия новая.
    /// </summary>
    public void RequestNewCode()
    {
        if (_isPaired || _pairing is null)
        {
            return;
        }

        _pairing.Forget();
        SavePairing();
        StopPairing();
        StartPairing();
    }

    /// <summary>Человек выбрал завести машину руками.</summary>
    ///
    /// <remarks>
    /// Не «пропустить»: пропущенный шаг возвращаются доделать, а этот выбор —
    /// вторая дорога целиком, и назад с неё ведёт кнопка «Назад».
    /// </remarks>
    public void UseManualSetup() => Step = FirstRunStep.User;

    /// <summary>Закрытие мастера: опрос не должен пережить окно.</summary>
    public void Dispose() => StopPairing();

    private void StartPairing()
    {
        if (_isPaired || !HasChannel || _pairingLoop is not null)
        {
            return;
        }

        _pairingLoop = new CancellationTokenSource();
        _ = PairAsync(_pairingLoop.Token);
    }

    private void StopPairing()
    {
        _pairingLoop?.Cancel();
        _pairingLoop?.Dispose();
        _pairingLoop = null;
    }

    /// <summary>
    /// Весь путь привязки: ключи, сессия, длинный опрос, первая настройка,
    /// подтверждение. Живёт, пока открыт экран кода.
    /// </summary>
    private async Task PairAsync(CancellationToken cancellation)
    {
        _pairing ??= PairingStore.Load();

        var retry = TimeSpan.FromSeconds(5);

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var (machine, channelKey) = _pairing.EnsureKeys();

                var session = CurrentSession();
                if (session is null)
                {
                    PairStatus = Strings.Get("FirstRunPairOpening");
                    PairFailed = false;

                    session = await SparkPairing.StartAsync(machine, channelKey, cancellation).ConfigureAwait(true);
                    _pairing.Remember(session);
                    SavePairing();
                    Log?.Invoke($"привязка: открыта сессия, код {session.Code}");
                }

                ShowSession(session);
                PairStatus = Strings.Get("FirstRunPairWaiting");
                PairFailed = false;

                var status = await SparkPairing.PollAsync(session, cancellation).ConfigureAwait(true);
                retry = TimeSpan.FromSeconds(5);

                switch (status.State)
                {
                    case PairState.Waiting:
                        continue;

                    case PairState.Expired:
                        // Истекла или Spark её не знает — новая сессия теми же
                        // ключами, новый код на экране.
                        Log?.Invoke("привязка: сессия истекла, открываю новую");
                        _pairing.Forget();
                        SavePairing();
                        continue;

                    case PairState.Claimed:
                    case PairState.Delivered:
                        if (status.InstallationID is not { Length: > 0 } installationID)
                        {
                            continue;
                        }

                        PairStatus = Strings.Get("FirstRunPairSettingUp");
                        await CompletePairingAsync(session, status, installationID, machine, channelKey, cancellation)
                            .ConfigureAwait(true);
                        return;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException
                                              or PairingException or PanelLinkException
                                              or System.IO.IOException)
            {
                // Нет связи со Spark или сервером обновлений — обычное
                // состояние, а не авария: повтор с нарастающей паузой.
                Log?.Invoke($"привязка: {error.Message} — повтор через {retry.TotalSeconds:0} с");
                PairStatus = Strings.Format("FirstRunPairRetry", error.Message, (int)retry.TotalSeconds);
                PairFailed = true;

                try
                {
                    await Task.Delay(retry, cancellation).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task CompletePairingAsync(
        PairSession session,
        PairStatus status,
        string installationID,
        MachineKeyPair machine,
        string channelKey,
        CancellationToken cancellation)
    {
        var fetched = await PairingSetup.FetchAsync(installationID, channelKey, machine, cancellation)
            .ConfigureAwait(true);

        PairingSetup.Apply(_settings, fetched, installationID, channelKey, machine, _access, message => Log?.Invoke(message));

        // Подтверждение — после того, как настройки легли: «delivered» для
        // Spark значит «машина забрала», и раньше времени его говорить нельзя.
        try
        {
            await SparkPairing.AckAsync(session, cancellation).ConfigureAwait(true);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or PairingException)
        {
            // Подтверждение не дошло — машина всё равно настроена, а Spark
            // увидит её по первому же опросу конфигурации.
            Log?.Invoke($"привязка: подтверждение не дошло — {error.Message}");
        }

        PairingStore.Clear();
        _pairing = null;

        PairedEmployee = fetched.Config.Employee.Length > 0 ? fetched.Config.Employee : status.Label ?? string.Empty;
        PairedNumber = fetched.Config.Number.Length > 0 ? fetched.Config.Number : status.Extension ?? string.Empty;
        PairStatus = null;
        IsPaired = true;

        Log?.Invoke($"машина привязана в Spark: {installationID}, номер {PairedNumber}");
    }

    private PairSession? CurrentSession()
    {
        if (_pairing is not { HasSession: true } state
            || state.PollSecret() is not { } secret
            || state.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return new PairSession(state.SessionID!, secret, state.Code ?? string.Empty, state.Qr ?? string.Empty,
            state.ExpiresAt ?? DateTimeOffset.UtcNow);
    }

    private void ShowSession(PairSession session)
    {
        if (PairCode == session.Code)
        {
            return;
        }

        PairCode = session.Code;
        PairQr = session.Qr.Length > 0 ? QrImage.Render(session.Qr) : null;
    }

    private void SavePairing()
    {
        if (_pairing is null)
        {
            return;
        }

        try
        {
            PairingStore.Save(_pairing);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            // Не сохранилось — код сменится при перезапуске, а привязке это не
            // мешает.
            Log?.Invoke($"привязка: сессия не сохранилась — {error.Message}");
        }
    }

    public bool ShowsUser => _step is FirstRunStep.User;

    public bool ShowsAppearance => _step is FirstRunStep.Appearance;

    public bool ShowsFinale => _step is FirstRunStep.Finale;

    public bool IsLastStep => _step is FirstRunStep.Finale;

    public bool CanGoBack => _step is not FirstRunStep.Welcome;

    /// <summary>Можно ли шагнуть дальше.</summary>
    ///
    /// <remarks>
    /// С экрана кода дальше пускает только состоявшаяся привязка: уходят с
    /// него не «Далее», а «Настроить вручную» — две кнопки, ведущие в разные
    /// места, не должны выглядеть одной.
    /// </remarks>
    public bool CanGoForward => _step switch
    {
        FirstRunStep.Pair => IsPaired,
        FirstRunStep.User => IsUserComplete,
        _ => true,
    };

    public bool IsUserComplete
        => _username.Trim().Length > 0
            && _sipPassword.Length > 0
            && _officeAddress.Trim().Length > 0
            && (_adminPassword.Length == 0 || _adminPassword == _repeatedAdminPassword);

    public bool AdminPasswordsDiffer
        => _repeatedAdminPassword.Length > 0 && _adminPassword != _repeatedAdminPassword;

    public LanguageSetting Language
    {
        get => _language;
        set => Set(ref _language, value);
    }

    public AppearanceMode Theme
    {
        get => _theme;
        set => Set(ref _theme, value);
    }

    public string Username
    {
        get => _username;
        set
        {
            Set(ref _username, value);
            NotifyUser();
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string SipPassword
    {
        get => _sipPassword;
        set
        {
            Set(ref _sipPassword, value);
            NotifyUser();
        }
    }

    /// <summary>Адрес АТС из офиса. Второй адрес заводит администратор позже.</summary>
    ///
    /// <remarks>
    /// Мастер спрашивает один адрес, а не пару: человек, заводящий машину,
    /// знает тот, из которого он сейчас работает. Вторую площадку добавляют в
    /// «Управлении» — тогда, когда она появляется.
    /// </remarks>
    public string OfficeAddress
    {
        get => _officeAddress;
        set
        {
            Set(ref _officeAddress, value);
            NotifyUser();
        }
    }

    /// <summary>Административный пароль. Пустой — машина остаётся открытой.</summary>
    ///
    /// <remarks>
    /// Не обязателен, и это решение: незащищённая машина — законное состояние
    /// (пароль снимают руками), а мастер, который не пускает дальше без
    /// пароля, заставляет придумать его на месте — то есть придумать плохой.
    /// </remarks>
    public string AdminPassword
    {
        get => _adminPassword;
        set
        {
            Set(ref _adminPassword, value);
            NotifyUser();
        }
    }

    public string RepeatedAdminPassword
    {
        get => _repeatedAdminPassword;
        set
        {
            Set(ref _repeatedAdminPassword, value);
            NotifyUser();
        }
    }

    /// <summary>Меняется ли язык — от этого зависит, нужен ли перезапуск.</summary>
    public bool LanguageChanges => _language != _settings.Appearance.Language;

    public void Forward()
    {
        if (!CanGoForward || _step is FirstRunStep.Finale)
        {
            return;
        }

        Step = _step switch
        {
            // Привязка отменяет экран учётки целиком: номер, пароль и адрес
            // приехали из Spark.
            FirstRunStep.Pair => FirstRunStep.Appearance,

            _ => _step + 1,
        };
    }

    public void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        Step = _step switch
        {
            FirstRunStep.Appearance when IsPaired => FirstRunStep.Pair,

            // С экрана учётки назад — на код: человек мог уйти «настроить
            // вручную» и передумать.
            FirstRunStep.User => FirstRunStep.Pair,

            _ => _step - 1,
        };
    }

    /// <summary>Применяет ручную настройку и помечает машину настроенной.</summary>
    ///
    /// <remarks>
    /// Привязанная машина уже настроена — ей остаются язык, тема и пометка.
    /// Пароли на ручной дороге уходят последними: они превращают машину в
    /// защищённую, и делать это раньше, чем записана учётка, значит запереть
    /// полупустое рабочее место.
    /// </remarks>
    public void Complete()
    {
        StopPairing();

        _settings.Appearance.Theme = _theme;
        _settings.Appearance.Language = _language;

        if (_isPaired)
        {
            _settings.Setup.IsCompleted = true;
            return;
        }

        _settings.Account.Username = _username.Trim();
        _settings.Account.DisplayName = _displayName.Trim();
        _settings.Account.Site = WorkplaceSite.Office;
        _settings.Pbx.OfficeAddress = _officeAddress.Trim();
        _settings.Account.Domain = _officeAddress.Trim();

        _settings.Credentials.SetPassword(_sipPassword);

        if (_adminPassword.Length > 0)
        {
            _access.SetPassword(_adminPassword);
            _settings.Admin.From(_access.Credential);
        }

        // Пометка «настроено» — последней: до неё запись могла оборваться, и
        // машина с половиной настроек обязана встретить мастер снова, а не
        // панель без телефона.
        _settings.Setup.IsCompleted = true;
    }

    private void NotifyUser()
    {
        NotifyChanged(nameof(IsUserComplete));
        NotifyChanged(nameof(CanGoForward));
        NotifyChanged(nameof(AdminPasswordsDiffer));
    }
}

/// <summary>QR-картинка для WPF из <see cref="QrCode"/>.</summary>
internal static class QrImage
{
    /// <summary>
    /// Рисует код чёрным по белому с полем в четыре модуля — независимо от
    /// темы: сканеры читают тёмное на светлом, а на тёмной теме инвертированный
    /// код часть телефонов не видит.
    /// </summary>
    internal static ImageSource Render(string text)
    {
        var qr = QrCode.Encode(text);
        const int quiet = 4;
        var size = qr.Size + (quiet * 2);

        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[(y * size) + x] = qr[x - quiet, y - quiet] ? (byte)0 : (byte)255;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bitmap.Freeze();
        return bitmap;
    }
}
