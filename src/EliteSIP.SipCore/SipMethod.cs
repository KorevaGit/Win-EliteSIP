namespace EliteSIP.SipCore;

/// <summary>
/// Методы SIP, которые реально нужны EliteSIP.
///
/// Список намеренно короткий: то, чего нет в требованиях (PUBLISH, MESSAGE для
/// чата, SUBSCRIBE для BLF), не заводим, пока не появится задача.
/// </summary>
public enum SipMethod
{
    Invite,
    Ack,
    Bye,
    Cancel,
    Register,
    Options,
    /// <summary>Перевод звонка.</summary>
    Refer,
    /// <summary>Приходит от Asterisk с результатом REFER, а также как keep-alive-ответ.</summary>
    Notify,
    /// <summary>Альтернативный способ отправки DTMF, если rfc2833 на сервере выключен.</summary>
    Info,
}

public static class SipMethodExtensions
{
    /// <summary>Имя метода в том виде, в каком оно уходит в стартовую строку и CSeq.</summary>
    public static string Name(this SipMethod method) => method switch
    {
        SipMethod.Invite => "INVITE",
        SipMethod.Ack => "ACK",
        SipMethod.Bye => "BYE",
        SipMethod.Cancel => "CANCEL",
        SipMethod.Register => "REGISTER",
        SipMethod.Options => "OPTIONS",
        SipMethod.Refer => "REFER",
        SipMethod.Notify => "NOTIFY",
        SipMethod.Info => "INFO",
        _ => method.ToString().ToUpperInvariant(),
    };

    /// <summary>
    /// Регистронезависимый разбор: в Via и CSeq метод всегда в верхнем
    /// регистре, но полагаться на это в парсере нельзя.
    ///
    /// Неизвестный метод даёт <see langword="null"/>, а не исключение: чужой
    /// PUBLISH — это не авария, а сообщение, на которое мы ответим 405.
    /// </summary>
    public static SipMethod? Parse(ReadOnlySpan<char> name)
    {
        foreach (SipMethod method in Enum.GetValues<SipMethod>())
        {
            if (name.Equals(method.Name(), StringComparison.OrdinalIgnoreCase))
            {
                return method;
            }
        }
        return null;
    }

    /// <summary>Создаёт ли метод диалог при успешном ответе.</summary>
    public static bool CreatesDialog(this SipMethod method) => method == SipMethod.Invite;

    /// <summary>Требует ли метод ACK на финальный ответ.</summary>
    public static bool RequiresAck(this SipMethod method) => method == SipMethod.Invite;
}
