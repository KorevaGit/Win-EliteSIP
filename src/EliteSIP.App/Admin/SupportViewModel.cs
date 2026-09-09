using System.Net.Http;
using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.Admin;

/// <summary>
/// Раздел «Поддержка»: всё, что машина знает о панели, и три действия над этим.
/// </summary>
///
/// <remarks>
/// <b>Действует сразу, а не по «Сохранить».</b> Остальное «Управление»
/// придерживает правки черновиком, потому что там правят машину. Здесь не
/// правят: спрашивают канал, меняют режим и вводят ключ перепрошивки — три
/// обращения наружу, у которых «Отменить» не значит ничего. Придержанный ключ
/// перепрошивки был бы прямо вреден: он сгорает в момент проверки, и «Отменить»
/// после него отменял бы только настройку, но не сожжённый ключ.
/// </remarks>
public sealed class SupportViewModel : Observable
{
    private readonly AppSettings _settings;
    private readonly AdminAccessState _access;
    private readonly Func<Task> _checkNow;
    private readonly Func<bool> _isInCall;

    private string _key = string.Empty;
    private bool _isBusy;
    private string? _report;

    /// <param name="checkNow">спросить канал прямо сейчас — оба такта разом.</param>
    /// <param name="isInCall">
    /// идёт ли разговор. Перепрошивка снимает регистрацию и поднимает её заново,
    /// и делать это посреди звонка нельзя.
    /// </param>
    public SupportViewModel(
        AppSettings settings,
        AdminAccessState access,
        Func<Task> checkNow,
        Func<bool> isInCall)
    {
        _settings = settings;
        _access = access;
        _checkNow = checkNow;
        _isInCall = isInCall;

        CheckNow = new RelayCommand(async _ => await CheckAsync(), _ => !_isBusy);
        Reflash = new RelayCommand(async _ => await ReflashAsync(), _ => CanReflash);
    }

    public RelayCommand CheckNow { get; }

    public RelayCommand Reflash { get; }

    /// <summary>Знает ли панель об этой машине вообще.</summary>
    public bool IsActivated => _settings.Panel.IsActivated;

    public string InstallationID => _settings.Panel.InstallationID;

    public string PresetName => _settings.Panel.PresetName;

    public int AppliedRevision => _settings.Panel.AppliedRevision;

    /// <summary>
    /// Когда канал последний раз ответил.
    /// </summary>
    ///
    /// <remarks>
    /// Отдельно от применённой ревизии, и это главная строка раздела: файл
    /// предустановок меняется раз в месяцы, а спрашивают его раз в два часа.
    /// «Ревизия 12» на лежащем канале выглядит так же благополучно, как на
    /// живом, — и разницу показывает только эта отметка.
    /// </remarks>
    public string LastContact => _settings.Panel.LastContactAt is { } moment
        ? moment.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)
        : Strings.Get("SupportNeverContacted");

    /// <summary>
    /// Слушает ли машина панель.
    /// </summary>
    ///
    /// <remarks>
    /// Возврат под предустановку просит переприменить её заново — иначе он не
    /// возвращает ничего: ревизия за время жизни своим умом не менялась, а
    /// обычное правило линии — «применяем то, что новее применённого».
    /// </remarks>
    public bool IsManaged
    {
        get => _settings.Panel.Mode is PanelMode.Managed;
        set
        {
            _settings.Panel.Mode = value ? PanelMode.Managed : PanelMode.Manual;

            if (value)
            {
                _settings.Panel.WantsResync = true;
            }

            NotifyChanged();
            NotifyChanged(nameof(CanBeManaged));
        }
    }

    /// <summary>
    /// Есть ли чем слушать панель.
    /// </summary>
    ///
    /// <remarks>
    /// Машина, поднятая руками, панели не знает вовсе, и переключатель на ней
    /// обещал бы связь, которой неоткуда взяться. Заводится такая связь только
    /// ключом — своим или перепрошивочным.
    /// </remarks>
    public bool CanBeManaged => _settings.Panel.PresetID.Length > 0;

    /// <summary>Ключ перепрошивки. Тот же формат, что у ключа активации.</summary>
    public string Key
    {
        get => _key;
        set
        {
            Set(ref _key, value);
            Report = null;
            Reflash.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Чем кончилось последнее обращение к каналу.</summary>
    public string? Report
    {
        get => _report;
        private set
        {
            Set(ref _report, value);
            NotifyChanged(nameof(HasReport));
        }
    }

    public bool HasReport => _report is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            Set(ref _isBusy, value);
            CheckNow.RaiseCanExecuteChanged();
            Reflash.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Перепрошивать можно только вне разговора.
    /// </summary>
    ///
    /// <remarks>
    /// Не «применится, когда положите трубку», а «сейчас нельзя»: ключ сгорает в
    /// момент проверки, и отложенная перепрошивка означала бы сожжённый ключ,
    /// висящий в памяти до конца звонка. Оригинал откладывал уже <i>применение</i>
    /// приехавшего пакета; здесь не даётся начать.
    /// </remarks>
    public bool CanReflash => !_isBusy && _key.Trim().Length > 0 && !_isInCall();

    /// <summary>Ответ линии на кнопку. Зовётся службой предустановок.</summary>
    public void ReportFromLine(bool isChecking, string? outcome)
    {
        IsBusy = isChecking;

        if (outcome is not null)
        {
            Report = outcome;
        }

        Refresh();
    }

    private async Task CheckAsync()
    {
        IsBusy = true;
        try
        {
            await _checkNow().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>
    /// Перепрошивка: пакет по ключу, выписанному на эту самую машину.
    /// </summary>
    ///
    /// <remarks>
    /// Идентификатор машины входит в вывод адреса пакета, поэтому чужой ключ
    /// уходит по другому адресу и не находит там ничего — вместо того чтобы
    /// скачать пакет и сжечь ключ на проверке внутри.
    /// </remarks>
    private async Task ReflashAsync()
    {
        if (!CanReflash)
        {
            return;
        }

        IsBusy = true;
        Report = null;

        try
        {
            var key = ActivationKey.Parse(_key);
            var package = await ActivationService
                .FetchAsync(key, _settings.Panel.InstallationID)
                .ConfigureAwait(true);

            MachineAccess? access = null;
            try
            {
                access = await MachineService
                    .FetchAccessAsync(package.InstallationID, package.ChannelKey)
                    .ConfigureAwait(true);
            }
            catch (Exception error) when (error is PanelLinkException or HttpRequestException
                                              or TaskCanceledException)
            {
                // Пароль догонит машину тактом линии — см. мастер.
            }

            _settings.Apply(package, access, _access);

            Key = string.Empty;
            Report = Strings.Format("SupportReflashed", package.Number, package.Preset.Name);
        }
        catch (PanelLinkException error)
        {
            Report = error.Message;
        }
        catch (ActivationChannelException error)
        {
            Report = error.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var name in new[]
        {
            nameof(IsActivated), nameof(InstallationID), nameof(PresetName),
            nameof(AppliedRevision), nameof(LastContact), nameof(IsManaged),
            nameof(CanBeManaged), nameof(CanReflash),
        })
        {
            NotifyChanged(name);
        }
    }
}
