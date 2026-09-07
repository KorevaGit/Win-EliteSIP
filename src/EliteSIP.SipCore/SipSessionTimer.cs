using System.Globalization;

namespace EliteSIP.SipCore;

/// <summary>
/// Кто обязан обновлять сессию.
///
/// <see cref="Uac"/> — тот, кто позвонил, <see cref="Uas"/> — тот, кому
/// позвонили. Это роли в исходном INVITE, и они не меняются на протяжении
/// диалога, как бы потом ни ходили повторные INVITE.
/// </summary>
public enum SipRefresher
{
    Uac,
    Uas,
}

/// <summary>
/// Договорённость об обновлении сессии, RFC 4028.
///
/// Нужна против «зависших» разговоров: если одна сторона исчезла, не прислав
/// BYE — упало питание, оборвался VPN, — вторая иначе держит линию, порты RTP и
/// строку в CDR до бесконечности. Таймер даёт обеим сторонам общий срок, после
/// которого разговор считается мёртвым, если его никто не подтвердил.
///
/// Боевой Asterisk настроен «Session Timers: Accept» — сам он таймер не
/// предлагает, но принимает предложенный и подтверждает своё участие. Значит всё
/// начинается с нас: не предложим — таймера не будет вовсе.
/// </summary>
public readonly record struct SipSessionTimer(int Expires, SipRefresher Refresher)
{
    /// <summary>
    /// Через сколько обновлять, если обновляем мы.
    ///
    /// Половина срока по RFC 4028 §10. Половина, а не «незадолго до конца»,
    /// именно затем, чтобы одно потерянное обновление не обрывало разговор:
    /// второй заход успевает пройти до истечения.
    /// </summary>
    public TimeSpan RefreshAfter => TimeSpan.FromSeconds(Math.Max(Expires / 2, 1));

    /// <summary>
    /// Через сколько считать сессию мёртвой, если обновляет собеседник.
    ///
    /// Полный срок: собеседник обязан был прислать обновление на середине, и к
    /// концу срока у него был ещё один шанс. Раньше времени рвать разговор
    /// нельзя — это была бы наша ошибка, а не его.
    /// </summary>
    public TimeSpan ExpireAfter => TimeSpan.FromSeconds(Math.Max(Expires, 2));

    /// <summary>Значение заголовка <c>Session-Expires</c>.</summary>
    public string HeaderValue =>
        $"{Expires.ToString(CultureInfo.InvariantCulture)};refresher={(Refresher == SipRefresher.Uas ? "uas" : "uac")}";

    /// <summary>
    /// Разбирает <c>Session-Expires: 1800;refresher=uas</c>.
    ///
    /// Без параметра <c>refresher</c> возвращает <see langword="null"/> для
    /// роли — сторона, которая его не прислала, ничего о ролях не сказала, и
    /// додумывать за неё нельзя: ошибка здесь означает, что обновлять не будет
    /// никто либо будут оба.
    /// </summary>
    public static (int Expires, SipRefresher? Refresher)? Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string[] parts = value.Split(';', 2);
        if (!int.TryParse(parts[0].TrimSip(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int expires)
            || expires <= 0)
        {
            return null;
        }

        if (parts.Length < 2)
        {
            return (expires, null);
        }

        foreach (SipParameter parameter in SipLexer.ParseParameters(parts[1]))
        {
            if (!string.Equals(parameter.Name, "refresher", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(parameter.Value, "uac", StringComparison.OrdinalIgnoreCase))
            {
                return (expires, SipRefresher.Uac);
            }
            if (string.Equals(parameter.Value, "uas", StringComparison.OrdinalIgnoreCase))
            {
                return (expires, SipRefresher.Uas);
            }
        }

        return (expires, null);
    }
}

/// <summary>
/// Что предлагать и что считать допустимым.
///
/// Значения совпадают с боевыми (<c>Session Expires: 1800</c>,
/// <c>Min-SE: 90</c>) не ради подражания, а потому что несовпадение стоило бы
/// лишнего круга: предложи мы меньше боевого Min-SE, сервер ответил бы 422, и
/// звонок начинался бы с заведомо отклонённого запроса.
/// </summary>
public sealed class SipSessionTimerPolicy
{
    /// <summary>
    /// Роль, которую мы предпочитаем отдать собеседнику.
    ///
    /// Обновлять просим сервер, а сами по возможности остаёмся стороной,
    /// которая только следит. Причина в цене ошибки: если ошибётся наш таймер
    /// обновления, мы уроним живой разговор своими руками. Если не обновит
    /// сервер — разговор и правда мёртв, и класть трубку правильно. Боевой
    /// Asterisk в роли UAS обновляет сам, так что на исходящих звонках это
    /// совпадает с его собственным выбором.
    /// </summary>
    public const SipRefresher PreferredPeerRefresher = SipRefresher.Uas;

    /// <summary>Предлагаемый срок сессии в секундах.</summary>
    public int Expires { get; set; } = 1800;

    /// <summary>Наименьший срок, который мы согласны принять.</summary>
    public int MinimumExpires { get; set; } = 90;

    /// <summary>
    /// Включено ли вообще.
    ///
    /// Выключение оставлено осознанно: таймер сессии — единственный механизм в
    /// клиенте, который сам кладёт трубку. Если на чужом сервере он поведёт себя
    /// не так, это должно чиниться настройкой, а не пересборкой.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Договорённость по ответу сервера на наш INVITE.
    ///
    /// <see langword="null"/> означает «таймера нет»: сервер либо промолчал про
    /// Session-Expires, либо прислал негодное значение. Молчание — законный
    /// ответ (RFC 4028 §7.2), и обращаться с ним надо именно как с отсутствием
    /// договорённости, а не как с подразумеваемым согласием: иначе мы завели бы
    /// таймер, о котором вторая сторона не знает, и положили бы трубку посреди
    /// разговора.
    /// </summary>
    public SipSessionTimer? NegotiatedFromResponse(SipHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!IsEnabled
            || headers.First(SipSessionTimerHeader.SessionExpires) is not string raw
            || SipSessionTimer.Parse(raw) is not { } parsed)
        {
            return null;
        }

        // Роль по умолчанию — uas: так предписывает RFC 4028 §7.2 для ответа без
        // параметра, и так же удобнее нам (обновляет сервер).
        return new SipSessionTimer(parsed.Expires, parsed.Refresher ?? SipRefresher.Uas);
    }

    /// <summary>
    /// Договорённость для ответа на чужой INVITE.
    ///
    /// Возвращает <see langword="null"/>, когда звонящий про таймер не
    /// заговаривал: навязывать его тому, кто о нём не просил, нельзя — он не
    /// станет ни обновлять, ни следить, а трубку в срок положим мы.
    /// </summary>
    public SipSessionTimer? NegotiatedForIncoming(SipHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!IsEnabled
            || headers.First(SipSessionTimerHeader.SessionExpires) is not string raw
            || SipSessionTimer.Parse(raw) is not { } parsed)
        {
            return null;
        }

        // Срок берём предложенный, но не короче того, на что согласны сами.
        // Звонящий обязан был учесть наш Min-SE только если знал его, а на первом
        // INVITE он его не знал.
        //
        // Обновляющим назначаем звонящего. Выбор за нами (RFC 4028 §8.2: UAS
        // решает), и он тот же, что на исходящих: следить дешевле, чем обновлять,
        // а цена нашей ошибки — брошенный живой разговор.
        return new SipSessionTimer(Math.Max(parsed.Expires, MinimumExpires), SipRefresher.Uac);
    }
}

/// <summary>
/// Имена заголовков RFC 4028.
///
/// Отдельным типом, а не в <see cref="SipHeaderName"/>: там перечислены
/// заголовки ядра RFC 3261, и дописывание в тот список расширений по одному
/// быстро превращает его в свалку. Каноническое написание обоих имён в
/// <see cref="SipHeaderName"/> уже есть.
/// </summary>
public static class SipSessionTimerHeader
{
    public const string SessionExpires = "Session-Expires";

    public const string MinSE = "Min-SE";

    /// <summary>Значение для <c>Supported</c>, которым объявляется поддержка.</summary>
    public const string OptionTag = "timer";
}
