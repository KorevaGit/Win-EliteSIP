namespace EliteSIP.Audio;

// Изменение устройства — общий язык движков и фильтра DeviceChangeRelevance.
// До 20 октября 2026 лежало рядом с наблюдателем MMDevice API.

/// <summary>Что именно изменилось в звуковом хозяйстве.</summary>
public enum AudioDeviceChangeKind
{
    /// <summary>Устройство появилось.</summary>
    DeviceAdded,

    /// <summary>Устройство исчезло.</summary>
    DeviceRemoved,

    /// <summary>
    /// Устройство сменило состояние: воткнули, выдернули, выключили в
    /// параметрах звука.
    /// </summary>
    DeviceStateChanged,

    /// <summary>Сменилось системное устройство (на Windows — по роли из <c>AudioDeviceCatalog.DefaultRole</c>).</summary>
    DefaultChanged,
}

/// <summary>Одно изменение.</summary>
/// <param name="Kind">Что произошло.</param>
/// <param name="DeviceId">
/// С кем произошло. Для смены умолчания может быть пустым: система так
/// сообщает, что устройства этого направления не осталось вовсе.
/// </param>
/// <param name="Direction">
/// Направление, которого касается изменение. <c>null</c> — неизвестно:
/// уведомления о появлении и исчезновении устройства направления не несут.
/// </param>
/// <param name="Availability">
/// Новое состояние устройства. Осмысленно только при
/// <see cref="AudioDeviceChangeKind.DeviceStateChanged"/>.
/// </param>
public readonly record struct AudioDeviceChange(
    AudioDeviceChangeKind Kind,
    string DeviceId,
    AudioDeviceDirection? Direction,
    AudioDeviceAvailability? Availability);
