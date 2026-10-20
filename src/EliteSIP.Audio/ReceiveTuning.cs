namespace EliteSIP.Audio;

/// <summary>
/// Числа приёмной цепочки, которые обязаны совпадать у всех движков.
/// </summary>
///
/// <remarks>
/// До 20 октября 2026 жили в <c>WasapiVoiceAudioEngine</c>. Вынесены, потому что
/// это не свойства WASAPI, а настройка разговора: Linux-движок, у которого
/// цель регулятора темпа или пределы выравнивания разойдутся с Windows,
/// звучал бы иначе на той же линии, и замеры 1–2 октября 2026 к нему уже не
/// относились бы.
/// </remarks>
internal static class ReceiveTuning
{
    /// <summary>
    /// Куда выравнивать голос собеседника, дБ RMS. На пару децибел громче, чем
    /// микрофон: приём слушают в одно ухо поверх шума кабинета.
    /// </summary>
    internal const double ReceiveTargetDb = -20;

    /// <summary>
    /// Подъём тихой линии не больше +10 дБ: вместе с голосом поднимается шум
    /// линии, и в паузах он стоит на той же прибавке.
    /// </summary>
    internal const double ReceiveMaximumBoostDb = 10;

    /// <summary>Громкую линию убавлять не больше чем на 6 дБ.</summary>
    internal const double ReceiveMaximumCutDb = 6;

    /// <summary>Цель регулятора темпа в отсчётах вывода. Отдельно — ради модели в тестах.</summary>
    internal static int BacklogTarget(PlaybackBacklog backlog, int frameSamples) =>
        Math.Max(1, (backlog.TargetFrames * frameSamples) - (frameSamples / 2));
}
