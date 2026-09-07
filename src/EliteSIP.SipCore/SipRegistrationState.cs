namespace EliteSIP.SipCore;

/// <summary>Где мы находимся в цикле регистрации.</summary>
public abstract record SipRegistrationState
{
    private SipRegistrationState()
    {
    }

    public sealed record Idle : SipRegistrationState;

    public sealed record Registering : SipRegistrationState;

    public sealed record Registered(DateTimeOffset ExpiresAt, string Contact) : SipRegistrationState;

    public sealed record Unregistering : SipRegistrationState;

    /// <summary>
    /// <paramref name="RetryAt"/> равен <see langword="null"/>, когда повтора не
    /// будет: так выглядит закрытый насовсем канал. Обещать «повтор через N с»
    /// в этом состоянии значило бы обещать то, что не сбудется.
    /// </summary>
    public sealed record Failed(string Reason, DateTimeOffset? RetryAt) : SipRegistrationState;

    public bool IsRegistered => this is Registered;
}

public enum SipLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public enum SipRegistrationErrorKind
{
    Rejected,
    AuthenticationFailed,
    TooManyAttempts,
}

/// <summary>
/// Почему регистрация не удалась — <b>типом</b>, а не строкой.
///
/// Подпись для оператора переведена, и сравнивать её в коде нельзя: разбор
/// причины сломался бы от смены языка машины. Мастеру первоначальной настройки
/// (этап W8) причина нужна именно разобранной — «неверный пароль» и «сервер
/// молчит» требуют от техподдержки разных действий.
/// </summary>
public sealed class SipRegistrationException : Exception
{
    public SipRegistrationException()
        : this(SipRegistrationErrorKind.TooManyAttempts)
    {
    }

    public SipRegistrationException(string message)
        : base(message) => Kind = SipRegistrationErrorKind.Rejected;

    public SipRegistrationException(string message, Exception innerException)
        : base(message, innerException) => Kind = SipRegistrationErrorKind.Rejected;

    public SipRegistrationException(SipRegistrationErrorKind kind, int status = 0, string reason = "")
        : base(Describe(kind, status, reason))
    {
        Kind = kind;
        Status = status;
    }

    public SipRegistrationErrorKind Kind { get; }

    public int Status { get; }

    private static string Describe(SipRegistrationErrorKind kind, int status, string reason) => kind switch
    {
        SipRegistrationErrorKind.AuthenticationFailed => "неверный логин или пароль",
        SipRegistrationErrorKind.TooManyAttempts => "слишком много попыток подряд",
        _ => status switch
        {
            403 => "неверный логин или пароль (403)",
            404 => "такого номера нет на сервере (404)",
            _ => $"сервер ответил {status.ToString(System.Globalization.CultureInfo.InvariantCulture)} {reason}",
        },
    };
}
