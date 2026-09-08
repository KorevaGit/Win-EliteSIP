namespace EliteSIP.Lines;

/// <summary>Что происходит с командой конференции на линии.</summary>
public enum ConferenceState
{
    /// <summary>Код не отправлялся.</summary>
    Idle,

    /// <summary>Код уходит прямо сейчас — повторное нажатие заблокировано.</summary>
    Sending,

    /// <summary>Код целиком вышел в поток. Повтор заблокирован до конца звонка.</summary>
    Sent,
}

/// <summary>Линия, какой её видит интерфейс.</summary>
/// <param name="CallId">Ключ линии. Другого нет: он приходит в каждом входящем запросе.</param>
/// <param name="Peer">Номер собеседника — для полосы линий.</param>
/// <param name="IsActive">Линия, у которой сейчас звук.</param>
/// <param name="IsHeldByOperator">Удержание по нашей воле.</param>
/// <param name="IsHeldByServer">Удержание с той стороны: <c>a=sendonly</c> или <c>c=0.0.0.0</c>.</param>
/// <param name="Conference">Состояние команды конференции.</param>
public readonly record struct LineView(
    string CallId,
    string Peer,
    bool IsActive,
    bool IsHeldByOperator,
    bool IsHeldByServer,
    ConferenceState Conference)
{
    /// <summary>Слышит ли оператор эту линию.</summary>
    public bool IsAudible => IsActive && !IsHeldByOperator && !IsHeldByServer;
}

/// <summary>
/// Одна линия: диалог, его медиа и всё, из-за чего она может молчать.
///
/// Причин молчать четыре, и они складываются: линия не активна, своё удержание,
/// серверное удержание и кнопка микрофона. Сводит их одно место —
/// <see cref="ApplyAudioState"/>. Раскладывать это по месту каждого нажатия —
/// верный способ получить разговор, в котором микрофон остался выключенным
/// после возврата с удержания.
/// </summary>
internal sealed class CallLine(string callId, ILineMedia media, string peer)
{
    public string CallId { get; } = callId;

    public ILineMedia Media { get; } = media;

    public string Peer { get; } = peer;

    public bool IsActive { get; set; }

    public bool IsHeldByOperator { get; set; }

    public bool IsHeldByServer { get; set; }

    public ConferenceState Conference { get; set; } = ConferenceState.Idle;

    /// <summary>Идёт ли по линии повторный INVITE прямо сейчас.</summary>
    public bool IsRenegotiating { get; set; }

    public LineView View => new(CallId, Peer, IsActive, IsHeldByOperator, IsHeldByServer, Conference);

    /// <summary>
    /// Сводит все причины молчать в одно состояние тракта.
    ///
    /// Приём глушится отдельно от передачи и по своей причине: Asterisk на
    /// удержании не замолкает и продолжает слать поток на полной скорости. Не
    /// заглушить его здесь значит дать оператору услышать всех собеседников
    /// сразу.
    /// </summary>
    public void ApplyAudioState(bool microphoneMuted)
    {
        bool audible = IsActive && !IsHeldByOperator && !IsHeldByServer;
        Media.SetMicrophoneMuted(!audible || microphoneMuted);
        Media.SetReceivingAudio(audible);
    }
}
