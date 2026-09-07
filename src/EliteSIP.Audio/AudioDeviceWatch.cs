using NAudio.CoreAudioApi;

namespace EliteSIP.Audio;

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

    /// <summary>Сменилось устройство по умолчанию для связи.</summary>
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

/// <summary>
/// Слежение за составом устройств и за устройством по умолчанию.
///
/// Заменяет <c>AudioObjectAddPropertyListener</c> из оригинала. Устройство
/// исчезает и возвращается посреди разговора — это обычный случай, а не
/// авария, и тракт обязан узнать об этом от системы, а не по тишине в трубке.
///
/// <b>Отписка в <see cref="Dispose"/>, а не отдельным методом</b> — решение
/// перенесено из оригинала вместе с причиной: забытый слушатель переживает
/// объект, который его поставил, и стреляет по мёртвой ссылке. Здесь это не
/// падение по освобождённой памяти, как было в CoreAudio, а утечка обратного
/// вызова, который держит уже ненужный тракт живым, — тише и потому хуже.
///
/// <b>Четыре отличия от macOS, которые стоит знать.</b>
///
/// <list type="number">
/// <item>Уведомление о смене частоты устройства ставить некуда и незачем. На
/// macOS слушатель вешался на каждое устройство персонально, потому что смена
/// частоты и была переходом Bluetooth-гарнитуры в режим связи. На Windows
/// режим связи — отдельная конечная точка, и переход виден как обычное
/// появление и исчезновение устройств.</item>
/// <item>Уведомления приходят на рабочем потоке звуковой службы, и он держит
/// внутренний замок, пока их разносит. Обработчик обязан возвращаться
/// немедленно: тяжёлая работа на нём подвешивает звук во всей системе, а не
/// только у нас. Поэтому здесь только пересказ события — решения принимает
/// тот, кто подписался.</item>
/// <item>Событий <b>не</b> переносим на поток интерфейса, хотя NAudio это
/// умеет и предлагает по умолчанию. Тракт живёт без интерфейса, и ставить
/// его осведомлённость о пропаже устройства в зависимость от того, крутится
/// ли цикл сообщений, — значит однажды не узнать о пропаже вовсе. Кому нужен
/// поток интерфейса, тот переложит сам.</item>
/// <item>Роль «связь», а не «мультимедиа»: смена устройства для музыки нас не
/// касается и дёргать тракт посреди разговора не должна.</item>
/// </list>
/// </summary>
public sealed class AudioDeviceWatch : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient _client;
    private readonly Action<AudioDeviceChange> _onChange;
    private bool _disposed;

    /// <summary>Создаёт подписку. Живёт до <see cref="Dispose"/>.</summary>
    /// <param name="onChange">
    /// Вызывается на рабочем потоке звуковой службы. Должен возвращаться
    /// немедленно и не обращаться к звуковой подсистеме.
    /// </param>
    public AudioDeviceWatch(Action<AudioDeviceChange> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        _onChange = onChange;

        _enumerator = new MMDeviceEnumerator();
        _client = _enumerator.CreateNotificationClient(useSynchronizationContext: false);

        _client.DeviceAdded += OnDeviceAdded;
        _client.DeviceRemoved += OnDeviceRemoved;
        _client.DeviceStateChanged += OnDeviceStateChanged;
        _client.DefaultDeviceChanged += OnDefaultDeviceChanged;

        // PropertyValueChanged не подписывается намеренно. Свойства меняются
        // потоком — громкость, состояние разъёма, формат, — и будить тракт на
        // каждое значило бы пересобирать его десятки раз за разговор. Всё, что
        // действительно требует пересборки, приходит отдельным событием.
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _client.DeviceAdded -= OnDeviceAdded;
        _client.DeviceRemoved -= OnDeviceRemoved;
        _client.DeviceStateChanged -= OnDeviceStateChanged;
        _client.DefaultDeviceChanged -= OnDefaultDeviceChanged;

        try
        {
            _client.Dispose();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Звуковая служба перезапустилась и забыла про нашу подписку.
            // Отписываться больше не от чего, а бросать отсюда нельзя: это
            // путь освобождения, и исключение здесь потеряет настоящую ошибку,
            // если она уже в полёте.
        }

        _enumerator.Dispose();
    }

    private void OnDeviceAdded(object? sender, DeviceNotificationEventArgs e) =>
        Report(AudioDeviceChangeKind.DeviceAdded, e.DeviceId, null, null);

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e) =>
        Report(AudioDeviceChangeKind.DeviceRemoved, e.DeviceId, null, null);

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e) =>
        Report(AudioDeviceChangeKind.DeviceStateChanged, e.DeviceId, null, Translate(e.NewState));

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        // Роли «консоль» и «мультимедиа» нас не касаются: человек может увести
        // музыку на телевизор, не трогая гарнитуру для разговоров.
        // Пересобирать из-за этого живой тракт значило бы рвать разговор на
        // ровном месте.
        if (e.Role != Role.Communications)
        {
            return;
        }

        AudioDeviceDirection? direction = e.Flow switch
        {
            DataFlow.Capture => AudioDeviceDirection.Capture,
            DataFlow.Render => AudioDeviceDirection.Render,
            _ => null,
        };

        Report(AudioDeviceChangeKind.DefaultChanged, e.DeviceId, direction, null);
    }

    internal static AudioDeviceAvailability Translate(DeviceState state) => state switch
    {
        DeviceState.Active => AudioDeviceAvailability.Active,
        DeviceState.Unplugged => AudioDeviceAvailability.Unplugged,
        DeviceState.Disabled => AudioDeviceAvailability.Disabled,
        _ => AudioDeviceAvailability.Absent,
    };

    private void Report(
        AudioDeviceChangeKind kind,
        string? deviceId,
        AudioDeviceDirection? direction,
        AudioDeviceAvailability? availability)
    {
        try
        {
            _onChange(new AudioDeviceChange(kind, deviceId ?? string.Empty, direction, availability));
        }
#pragma warning disable CA1031 // намеренно: см. комментарий
        catch
        {
            // Исключение отсюда уходит в COM на рабочем потоке звуковой службы,
            // который держит замок: диагностировать его будет нечем и некому, а
            // последствие — подвисший звук во всей системе. Подписчик обязан
            // разбираться со своими ошибками сам; здесь остаётся только не дать
            // им уйти за границу.
        }
#pragma warning restore CA1031
    }
}
