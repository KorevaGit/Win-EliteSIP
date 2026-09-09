using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using EliteSIP.AdminAccess;
using EliteSIP.App.Panel;

namespace EliteSIP.App.Settings;

/// <summary>Чем ходить до АТС.</summary>
public enum SipTransport
{
    Udp,
    Tcp,
    Tls,
}

/// <summary>Адреса АТС для двух площадок и коды её возможностей.</summary>
///
/// <remarks>
/// Пара адресов — это то, чем переключается «Работа» в менеджерских настройках:
/// из дома внутренний адрес недостижим, а из офиса внешний ведёт на тот же
/// сервер длинной дорогой через шлюз. Оба адреса заводит администратор, потому
/// что знает он, а выбирает между ними человек за машиной.
/// </remarks>
public sealed class PbxSettings : Observable
{
    private string _officeAddress = string.Empty;
    private string _remoteAddress = string.Empty;
    private int _port = 5060;
    private SipTransport _transport = SipTransport.Udp;
    private int _registrationExpirySeconds = 120;
    private string _transferFeatureCode = string.Empty;
    private string _conferenceFeatureCode = string.Empty;

    public string OfficeAddress
    {
        get => _officeAddress;
        set => Set(ref _officeAddress, value);
    }

    public string RemoteAddress
    {
        get => _remoteAddress;
        set => Set(ref _remoteAddress, value);
    }

    public int Port
    {
        get => _port;
        set => Set(ref _port, Math.Clamp(value, 1, 65535));
    }

    public SipTransport Transport
    {
        get => _transport;
        set => Set(ref _transport, value);
    }

    /// <summary>На сколько просить регистрацию.</summary>
    ///
    /// <remarks>
    /// Сервер вправе выдать меньше, и тогда действует его число: спорить с
    /// сервером клиенту не о чем.
    /// </remarks>
    public int RegistrationExpirySeconds
    {
        get => _registrationExpirySeconds;
        set => Set(ref _registrationExpirySeconds, Math.Clamp(value, 30, 3600));
    }

    /// <summary>Код перевода этой АТС. Пустой — кнопка перевода не работает.</summary>
    ///
    /// <remarks>
    /// Зашить его нельзя: <c>*02</c> — это перевод боевого сервера заказчика, а
    /// не общее правило Asterisk. У другой установки там своя запись.
    /// </remarks>
    public string TransferFeatureCode
    {
        get => _transferFeatureCode;
        set => Set(ref _transferFeatureCode, value);
    }

    /// <summary>Код сбора конференции. По той же причине задаётся, а не угадывается.</summary>
    public string ConferenceFeatureCode
    {
        get => _conferenceFeatureCode;
        set => Set(ref _conferenceFeatureCode, value);
    }
}

/// <summary>Очередь: номер, которым она приходит, и как её называть человеку.</summary>
///
/// <remarks>
/// Словарь заведён ради одного: на раздаче из очереди номер бесполезен —
/// оператор видит цифры, которые ничего ему не говорят. Администратор называет
/// очередь словами, и эти слова показываются и в окне входящего, и в истории.
/// </remarks>
public sealed class QueueSetting : Observable
{
    private string _number = string.Empty;
    private string _title = string.Empty;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Number
    {
        get => _number;
        set => Set(ref _number, value);
    }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }
}

/// <summary>Журнал и чистка — то, что делают на машине, когда что-то пошло не так.</summary>
public sealed class MaintenanceSettings : Observable
{
    private bool _logToFile = true;
    private bool _logsSipTrace;

    /// <summary>Вести ли журнал в файл.</summary>
    ///
    /// <remarks>
    /// Выключенный журнал означает, что собирать поддержке будет нечего, — и
    /// «Собрать логи» в менеджерских настройках об этом честно говорит.
    /// </remarks>
    public bool LogToFile
    {
        get => _logToFile;
        set => Set(ref _logToFile, value);
    }

    /// <summary>Писать ли в журнал трассу SIP.</summary>
    ///
    /// <remarks>
    /// Отдельно от самого журнала: трасса подробная и в обычной работе не
    /// нужна, а при разборе «почему не регистрируется» она первое, что просят.
    /// Не переводится — её сравнивают между машинами.
    /// </remarks>
    public bool LogsSipTrace
    {
        get => _logsSipTrace;
        set => Set(ref _logsSipTrace, value);
    }
}

/// <summary>Учётка на АТС. Пароль лежит зашифрованным DPAPI.</summary>
///
/// <remarks>
/// Сквозное правило плана: на macOS пароль профиля лежал в файле с правами
/// <c>0600</c> — сознательно не в Keychain. На Windows права файла такой защиты
/// не дают, поэтому пароль шифруется <c>ProtectedData</c> с областью
/// <c>CurrentUser</c>: файл, унесённый на другую машину или открытый другим
/// пользователем, пароля не отдаёт.
/// </remarks>
public sealed class SipCredentials : Observable
{
    private string? _protectedPassword;

    /// <summary>Зашифрованный пароль. В файл едет только он.</summary>
    public string? ProtectedPassword
    {
        get => _protectedPassword;
        set
        {
            Set(ref _protectedPassword, value);
            NotifyChanged(nameof(HasPassword));
        }
    }

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(_protectedPassword);

    /// <summary>Кладёт пароль под DPAPI. Пустой — стирает.</summary>
    public void SetPassword(string password)
        => ProtectedPassword = string.IsNullOrEmpty(password) ? null : ProtectedSecret.Protect(password);

    /// <summary>Достаёт пароль. <c>null</c> — расшифровать не удалось.</summary>
    ///
    /// <remarks>
    /// Не удалось — это законный исход, а не сбой: файл настроек, принесённый с
    /// чужой машины, расшифровке не поддаётся по построению. Значит, пароль
    /// придётся ввести заново, и сказать об этом надо словами, а не отказом
    /// регистрации без объяснений.
    /// </remarks>
    public string? Password()
        => _protectedPassword is null ? null : ProtectedSecret.Unprotect(_protectedPassword);
}

/// <summary>Очереди — список, потому что порядок в нём человеку виден.</summary>
public sealed class QueueSettings : Observable
{
    public ObservableCollection<QueueSetting> Queues { get; init; } = [];
}
