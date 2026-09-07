namespace EliteSIP.Audio;

/// <summary>
/// Чем тракт сейчас связан с устройствами: что просили в настройках и что
/// досталось на самом деле.
///
/// Разница между этими двумя вещами и есть весь смысл типа. Настройки могут
/// называть гарнитуру, которой сейчас нет; тракт в этом случае обязан взять
/// системное устройство для связи, а не отказаться от звонка. Но тогда он ждёт
/// возвращения названной гарнитуры — и вернуться она должна сама, без просьбы
/// оператора переключить что-нибудь руками.
/// </summary>
/// <param name="ConfiguredInputId">Микрофон из настроек. Пусто — следуем системному.</param>
/// <param name="ConfiguredOutputId">Выход из настроек. Пусто — следуем системному.</param>
/// <param name="ActiveInputId">Микрофон, который открыт сейчас.</param>
/// <param name="ActiveOutputId">Выход, который открыт сейчас.</param>
/// <remarks>
/// <b>Класс, а не структура, и причина в потоках.</b> Привязку пишет сборка
/// тракта, а читает рабочий поток звуковой службы, разносящий уведомления.
/// Структура из четырёх ссылок обновляется четырьмя записями, и читатель
/// застаёт её наполовину новой — с уже новым микрофоном и ещё старым выходом.
/// По такой смеси фильтр отвечает неверно, причём именно в тот момент, когда
/// устройства меняются, то есть ровно там, где он нужен. Ссылка подменяется
/// целиком или никак.
/// </remarks>
public sealed record AudioRouteBinding(
    string? ConfiguredInputId,
    string? ConfiguredOutputId,
    string? ActiveInputId,
    string? ActiveOutputId)
{
    /// <summary>Привязки ещё нет: тракт не собран.</summary>
    public static AudioRouteBinding None { get; } = new(null, null, null, null);

    /// <summary>Микрофон не назначен — идём за системным для связи.</summary>
    public bool FollowsDefaultInput => string.IsNullOrEmpty(ConfiguredInputId);

    /// <summary>Выход не назначен — идём за системным для связи.</summary>
    public bool FollowsDefaultOutput => string.IsNullOrEmpty(ConfiguredOutputId);

    /// <summary>Назначенный микрофон недоступен, работаем на подмене.</summary>
    public bool UsesFallbackInput =>
        !FollowsDefaultInput && !string.Equals(ConfiguredInputId, ActiveInputId, StringComparison.Ordinal);

    /// <summary>Назначенный выход недоступен, работаем на подмене.</summary>
    public bool UsesFallbackOutput =>
        !FollowsDefaultOutput && !string.Equals(ConfiguredOutputId, ActiveOutputId, StringComparison.Ordinal);
}

/// <summary>
/// Касается ли нас то, что произошло в звуковом хозяйстве.
///
/// <b>Зачем фильтр вообще.</b> На этой машине двадцать конечных точек, и
/// уведомления о них приходят пачками при каждом чихе: воткнули монитор с
/// динамиками, проснулся Bluetooth, служба переставила умолчание для
/// мультимедиа. Пересобирать тракт на каждое такое событие значит рвать
/// разговор по поводу, к разговору не относящемуся, — а пересборка это
/// заметная пауза, до восьми десятых секунды на открытии устройства.
///
/// <b>Зачем он отдельный и чистый.</b> Правила короткие, но ошибиться в них
/// легко в обе стороны, и обе ошибки дорогие: лишняя пересборка рвёт живой
/// разговор, пропущенная оставляет оператора говорить в выдернутую гарнитуру.
/// Проверить это на живом железе можно только выдёргиванием проводов в нужном
/// порядке; здесь оно проверяется тестом.
/// </summary>
public static class DeviceChangeRelevance
{
    /// <summary>Надо ли пересобирать тракт из-за этого изменения.</summary>
    public static bool AffectsRoute(AudioDeviceChange change, AudioRouteBinding binding) =>
        change.Kind switch
        {
            // Умолчание для связи сменилось — но касается это только той
            // стороны, которая за умолчанием и следует. Если микрофон назначен
            // явно, смена системного микрофона нас не трогает.
            AudioDeviceChangeKind.DefaultChanged => change.Direction switch
            {
                AudioDeviceDirection.Capture => binding.FollowsDefaultInput,
                AudioDeviceDirection.Render => binding.FollowsDefaultOutput,

                // Направление неизвестно — считаем, что касается. Пропустить
                // смену маршрута хуже, чем пересобрать лишний раз.
                _ => true,
            },

            // Устройство исчезло или сменило состояние. Наше — пересобираем.
            AudioDeviceChangeKind.DeviceRemoved => IsOurs(change.DeviceId, binding),
            AudioDeviceChangeKind.DeviceStateChanged => IsOurs(change.DeviceId, binding)
                || IsAwaitedReturn(change.DeviceId, binding),

            // Появилось устройство. Интересно ровно одним: это может быть та
            // самая гарнитура из настроек, на подмене которой мы сейчас
            // работаем. Ждать, пока оператор сам заметит и переключит, — значит
            // оставить его говорить в динамики ноутбука весь разговор.
            AudioDeviceChangeKind.DeviceAdded => IsAwaitedReturn(change.DeviceId, binding),

            _ => false,
        };

    /// <summary>Это одно из устройств, на которых тракт работает прямо сейчас.</summary>
    private static bool IsOurs(string deviceId, AudioRouteBinding binding) =>
        !string.IsNullOrEmpty(deviceId)
        && (string.Equals(deviceId, binding.ActiveInputId, StringComparison.Ordinal)
            || string.Equals(deviceId, binding.ActiveOutputId, StringComparison.Ordinal));

    /// <summary>Это назначенное устройство, которого нам сейчас не хватает.</summary>
    private static bool IsAwaitedReturn(string deviceId, AudioRouteBinding binding) =>
        !string.IsNullOrEmpty(deviceId)
        && ((binding.UsesFallbackInput
                && string.Equals(deviceId, binding.ConfiguredInputId, StringComparison.Ordinal))
            || (binding.UsesFallbackOutput
                && string.Equals(deviceId, binding.ConfiguredOutputId, StringComparison.Ordinal)));
}
