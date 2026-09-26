using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Admin;

/// <summary>
/// Раздел «Поддержка» в «Управлении»: связь машины со Spark.
/// </summary>
///
/// <remarks>
/// <para>
/// Единственный раздел, где кнопки действуют сразу: «Проверить настройки
/// сейчас» и «Вернуться в онлайн» не правки, которые ждут «Сохранить», а
/// действия.
/// </para>
/// <para>
/// С 0.1.58 ключей активации нет, и перепрошивки ключом тоже: номер и
/// предустановку меняют в карточке сотрудника в Spark, и машина применяет их
/// сама в течение пятнадцати минут. Вместо поля ключа здесь строка «Машина» —
/// по ней администратор находит её в Spark.
/// </para>
/// </remarks>
public sealed class SupportViewModel : Observable
{
    private readonly AppSettings _settings;
    private readonly Func<Task> _checkNow;
    private readonly Func<Task> _returnOnline;

    private bool _isBusy;
    private string? _report;

    public SupportViewModel(AppSettings settings, Func<Task> checkNow, Func<Task> returnOnline)
    {
        _settings = settings;
        _checkNow = checkNow;
        _returnOnline = returnOnline;

        CheckNow = new RelayCommand(async _ => await RunAsync(_checkNow), _ => !_isBusy);
        ReturnOnline = new RelayCommand(async _ => await RunAsync(_returnOnline), _ => !_isBusy);
    }

    /// <summary>«Проверить настройки сейчас» — у машины на связи.</summary>
    public RelayCommand CheckNow { get; }

    /// <summary>«Вернуться в онлайн» — у машины в оффлайне, на месте первой.</summary>
    public RelayCommand ReturnOnline { get; }

    /// <summary>Знает ли Spark об этой машине вообще.</summary>
    public bool IsActivated => _settings.Panel.IsActivated;

    public string InstallationID => _settings.Panel.InstallationID;

    public string PresetName => _settings.Panel.PresetName;

    public int AppliedRevision => _settings.Panel.AppliedRevision;

    public int AppliedConfigRevision => _settings.Panel.AppliedConfigRevision;

    /// <summary>Машина в оффлайне и её есть чем вернуть.</summary>
    public bool IsOffline => _settings.Panel.IsOffline;

    public bool IsOnline => !IsOffline;

    /// <summary>
    /// Связь словами: «Spark, онлайн», «оффлайн, вручную» или «вручную, связи
    /// нет».
    /// </summary>
    ///
    /// <remarks>
    /// Машине без ключа канала вернуться нечем: её заводили руками или
    /// сбросили, и связь со Spark у неё появится только привязкой по коду.
    /// </remarks>
    public string LinkState => Strings.Get(
        _settings.Panel.Mode is PanelMode.Managed && _settings.Panel.HasChannelKey ? "SupportLinkOnline"
        : _settings.Panel.IsOffline ? "SupportLinkOffline"
        : "SupportLinkNone");

    /// <summary>
    /// Когда канал последний раз ответил.
    /// </summary>
    ///
    /// <remarks>
    /// Отдельно от применённой ревизии: «ревизия 12» на лежащем канале выглядит
    /// так же благополучно, как на живом, — и разницу показывает только эта
    /// отметка.
    /// </remarks>
    public string LastContact => _settings.Panel.LastContactAt is { } moment
        ? moment.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)
        : Strings.Get("SupportNeverContacted");

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
            ReturnOnline.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Ответ линии на нажатие — пока окно открыто.</summary>
    public void ReportFromLine(bool isChecking, string? outcome)
    {
        IsBusy = isChecking;

        if (outcome is not null)
        {
            Report = outcome;
        }

        Refresh();
    }

    /// <summary>Сменилось состояние линии: онлайн, оффлайн, ревизии.</summary>
    public void Refresh()
    {
        foreach (var name in new[]
        {
            nameof(IsActivated), nameof(InstallationID), nameof(PresetName),
            nameof(AppliedRevision), nameof(AppliedConfigRevision), nameof(LastContact),
            nameof(IsOffline), nameof(IsOnline), nameof(LinkState),
        })
        {
            NotifyChanged(name);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }
}
