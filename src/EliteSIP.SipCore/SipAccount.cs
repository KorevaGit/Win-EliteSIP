namespace EliteSIP.SipCore;

/// <summary>
/// Учётная запись SIP.
///
/// Пароля здесь нет: он лежит в профиле и передаётся отдельно, в учётных данных
/// digest-аутентификации. Так его нельзя случайно записать в журнал вместе со
/// всей структурой — а её печатают и в диагностике, и в журнале.
/// </summary>
public sealed class SipAccount
{
    /// <summary>Внутренний номер — он же user-part в address-of-record.</summary>
    public required string Username { get; set; }

    /// <summary>Логин для аутентификации, если он отличается от номера.</summary>
    public string? AuthUsername { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Домен для From, To и Request-URI регистрации.</summary>
    public required string Domain { get; set; }

    /// <summary>Куда фактически подключаться, если это не <see cref="Domain"/>.</summary>
    public string? ServerHost { get; set; }

    public ushort? ServerPort { get; set; }

    public SipTransport Transport { get; set; } = SipTransport.Tls;

    /// <summary>Запрашиваемый срок регистрации в секундах.</summary>
    public int RegistrationExpires { get; set; } = 300;

    public SipEndpoint SignalingEndpoint => new(
        string.IsNullOrEmpty(ServerHost) ? Domain : ServerHost,
        ServerPort ?? Transport.DefaultPort());

    /// <summary>
    /// Request-URI для REGISTER.
    ///
    /// Схема всегда <c>sip:</c>, даже на TLS. <c>sips:</c> формально строже, но
    /// chan_sip на него реагирует непредсказуемо, а защиту здесь обеспечивает
    /// сам транспорт, а не буква в URI.
    /// </summary>
    public SipUri RegistrarUri => new(Domain);

    /// <summary>Address-of-record: то, что стоит в From и To регистрации.</summary>
    public SipUri AddressOfRecord => new(Domain, user: Username);

    public string EffectiveAuthUsername => string.IsNullOrEmpty(AuthUsername) ? Username : AuthUsername;

    /// <summary>
    /// Имя, которое уходит в <c>From</c>.
    ///
    /// Пустое поле означает не «имени нет», а «имя равно номеру»: по
    /// согласованному плану номер аккаунта служит и user-part, и отображаемым
    /// именем, и локальной меткой профиля. Без этой подстановки <c>From</c>
    /// уходил бы вовсе без display-name, и на плече агента вместо номера
    /// оператора сервер видел бы пустоту.
    /// </summary>
    public string EffectiveDisplayName => DisplayName.Length == 0 ? Username : DisplayName;

    public bool IsUsable => Username.Length > 0 && Domain.Length > 0;
}
