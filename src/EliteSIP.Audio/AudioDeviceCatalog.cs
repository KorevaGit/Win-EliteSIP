using NAudio.CoreAudioApi;

namespace EliteSIP.Audio;

/// <summary>
/// Список устройств и опознание их по идентификатору.
///
/// Тонкая обёртка над MMDevice API — тем, что в оригинале делал HAL. Роль та
/// же, что у <c>AudioDeviceCatalog</c> из <c>MediaCore</c>, и так же без
/// состояния: перечислитель создаётся на вызов и тут же освобождается. Долго
/// живёт только наблюдатель (<see cref="AudioDeviceWatch"/>) — ему нужен свой,
/// потому что подписка привязана к экземпляру.
/// </summary>
public static class AudioDeviceCatalog
{
    /// <summary>
    /// Имя перечислителя устройства — <c>DEVPKEY_Device_EnumeratorName</c>.
    ///
    /// Собирается руками: в наборе ключей NAudio его нет, а свойство
    /// <c>MMDevice.InstanceId</c>, которое напрашивается вместо него, на этой
    /// машине возвращает строку «Unknown» для всех устройств без исключения.
    /// Проверено прогоном по всем конечным точкам — см. историю W4.
    /// </summary>
    private static readonly PropertyKey EnumeratorNameKey =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    /// <summary>
    /// Форм-фактор конечной точки. Годится ровно на одно: отличить звук через
    /// видеовыход. Различить USB-гарнитуру и Bluetooth-гарнитуру он не может —
    /// у обеих <c>Headset</c>.
    /// </summary>
    private const int FormFactorDigitalAudioDisplayDevice = 9;

    /// <summary>Устройства заданного направления.</summary>
    /// <param name="direction">Захват или воспроизведение.</param>
    /// <param name="includeInactive">
    /// Включать ли невоткнутые, выключенные и отсутствующие. По умолчанию нет:
    /// для выбора устройства человеку нужны те, на которых можно говорить.
    /// Диагностике нужны все — она и просит.
    /// </param>
    public static IReadOnlyList<AudioDevice> Devices(
        AudioDeviceDirection direction,
        bool includeInactive = false)
    {
        try
        {
            using MMDeviceEnumerator enumerator = new();
            DeviceState mask = includeInactive ? DeviceState.All : DeviceState.Active;

            List<AudioDevice> result = [];
            foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(Flow(direction), mask))
            {
                using (endpoint)
                {
                    AudioDevice? device = Describe(endpoint, direction);
                    if (device is not null)
                    {
                        result.Add(device);
                    }
                }
            }

            return result;
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
            // Звуковой подсистемы в системе может не быть вовсе: машина без
            // звуковой карты, сервер сборки, сеанс без звукового устройства.
            // Отказ перечисления — не повод падать: приложение должно
            // сказать «устройств нет», а не закрыться.
            return [];
        }
    }

    /// <summary>
    /// Устройство, которое человек назначил для связи.
    ///
    /// Роль «связь», а не «мультимедиа»: софтфон обязан ехать на том
    /// устройстве, которое выбрано для разговоров, а не на том, где играет
    /// музыка. Это то же решение, что в стенде W0.
    /// </summary>
    public static AudioDevice? Default(AudioDeviceDirection direction)
    {
        try
        {
            using MMDeviceEnumerator enumerator = new();
            if (!enumerator.TryGetDefaultAudioEndpoint(Flow(direction), Role.Communications, out MMDevice? endpoint)
                || endpoint is null)
            {
                return null;
            }

            using (endpoint)
            {
                return Describe(endpoint, direction);
            }
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
            return null;
        }
    }

    /// <summary>
    /// Устройство по сохранённому идентификатору.
    ///
    /// Возвращает <c>null</c>, если устройства сейчас нет — например, гарнитура
    /// отключена. Вызывающий в этом случае обязан взять системное по умолчанию,
    /// а не отказываться от звонка: требование перенесено из оригинала
    /// дословно, потому что цена ошибки — несостоявшийся разговор.
    /// </summary>
    public static AudioDevice? Find(string id, AudioDeviceDirection direction)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        try
        {
            using MMDeviceEnumerator enumerator = new();
            using MMDevice endpoint = enumerator.GetDevice(id);
            return Describe(endpoint, direction);
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
            return null;
        }
    }

    /// <summary>
    /// Открывает конечную точку для работы. Отдаёт её вызывающему — освобождать
    /// ему.
    ///
    /// Нужно тракту: описание устройства для открытия потока не годится, работу
    /// ведёт сама конечная точка.
    /// </summary>
    public static MMDevice? Open(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        try
        {
            using MMDeviceEnumerator enumerator = new();
            return enumerator.GetDevice(id);
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
            return null;
        }
    }

    /// <summary>
    /// Описывает уже открытую конечную точку. Саму точку не освобождает.
    ///
    /// Открытое, а не внутреннее: описывать точку, которая уже в руках, нужно
    /// и тракту, и стенду. Второму — чтобы печатать ту же сводку, что и
    /// приложение, иначе отчёты перестанут сравниваться глазами.
    /// </summary>
    public static AudioDevice? Describe(MMDevice endpoint, AudioDeviceDirection direction)
    {
        string id;
        string name;
        try
        {
            id = endpoint.ID;
            name = endpoint.FriendlyName;
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
            // Устройство исчезло между перечислением и чтением. Обычное дело
            // при выдёргивании донгла — пропустить его тише, чем уронить весь
            // список.
            return null;
        }

        string transportName = ReadEnumeratorName(endpoint);
        int formFactor = ReadFormFactor(endpoint);
        (int channels, int sampleRate, bool live) = ReadFormat(endpoint);

        return new AudioDevice(
            Id: id,
            Name: name,
            Direction: direction,
            Transport: Classify(transportName, formFactor),
            TransportName: transportName,
            Availability: Availability(endpoint),
            Channels: channels,
            SampleRate: sampleRate,
            FormatIsLive: live);
    }

    /// <summary>
    /// Тип подключения по имени перечислителя и форм-фактору.
    ///
    /// Чистая функция и отдельно от чтения свойств намеренно: разбор имени —
    /// единственное здесь, что можно проверить тестом без звуковой карты, а
    /// ошибка в нём стоит неверно опознанной гарнитуры.
    /// </summary>
    internal static AudioTransport Classify(string enumeratorName, int formFactor)
    {
        // Видеовыход опознаётся форм-фактором: перечислитель у него тот же
        // HDAUDIO, что у встроенной карты, и по нему это встроенное устройство.
        if (formFactor == FormFactorDigitalAudioDisplayDevice)
        {
            return AudioTransport.Hdmi;
        }

        // Сравнение без учёта регистра: система пишет имена заглавными, но
        // полагаться на это незачем — цена проверки нулевая.
        return enumeratorName.ToUpperInvariant() switch
        {
            "BTHHFENUM" => AudioTransport.BluetoothHandsFree,
            "BTHENUM" or "BTHLEENUM" => AudioTransport.Bluetooth,
            "USB" => AudioTransport.Usb,
            "HDAUDIO" or "PCI" or "ACPI" => AudioTransport.BuiltIn,
            "SWD" or "ROOT" or "MMDEVAPI" => AudioTransport.Virtual,
            "" => AudioTransport.Other,
            _ => AudioTransport.Other,
        };
    }

    private static DataFlow Flow(AudioDeviceDirection direction) =>
        direction == AudioDeviceDirection.Capture ? DataFlow.Capture : DataFlow.Render;

    private static AudioDeviceAvailability Availability(MMDevice endpoint)
    {
        try
        {
            return endpoint.State switch
            {
                DeviceState.Active => AudioDeviceAvailability.Active,
                DeviceState.Unplugged => AudioDeviceAvailability.Unplugged,
                DeviceState.Disabled => AudioDeviceAvailability.Disabled,
                _ => AudioDeviceAvailability.Absent,
            };
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
            return AudioDeviceAvailability.Absent;
        }
    }

    private static string ReadEnumeratorName(MMDevice endpoint)
    {
        try
        {
            PropertyStore properties = endpoint.Properties;
            if (properties.Contains(EnumeratorNameKey) &&
                properties[EnumeratorNameKey].Value is string name)
            {
                return name;
            }
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
            // Хранилище свойств у исчезнувшего устройства не читается.
        }

        return string.Empty;
    }

    /// <summary>
    /// Физический микрофон взамен системного — когда тот виртуальный или молчит.
    /// </summary>
    /// <param name="excluding">Устройства, которые не годятся: текущее и уже замолчавшие.</param>
    /// <param name="renderName">Имя устройства вывода: микрофон той же карты — первый кандидат.</param>
    public static AudioDevice? SpareCapture(IReadOnlyCollection<string> excluding, string? renderName)
        => PickSpareCapture(Devices(AudioDeviceDirection.Capture), excluding, renderName);

    /// <summary>
    /// Выбор запасного микрофона — чистой функцией, чтобы проверять тестом.
    /// </summary>
    ///
    /// <remarks>
    /// Поймано 11 сентября 2026: на машине оператора системным микрофоном для
    /// связи стоял «Headset Microphone (Oculus Virtual Audio Device)», он отдаёт
    /// цифровой ноль, а гарнитура была воткнута в Realtek. Наушники при этом
    /// системными были «Наушники (Realtek(R) Audio)» — то есть подсказка, где
    /// настоящий микрофон, лежала рядом. Отсюда порядок: сначала та же карта,
    /// что и вывод, потом любая проводная или USB, последним — Bluetooth в
    /// режиме гарнитуры (он переводит наушники в моно 8 кГц). Виртуальные и
    /// HDMI не берутся вовсе.
    /// </remarks>
    internal static AudioDevice? PickSpareCapture(
        IReadOnlyList<AudioDevice> candidates,
        IReadOnlyCollection<string> excluding,
        string? renderName)
    {
        string? renderAdapter = AdapterOf(renderName);

        return candidates
            .Where(device => device.Availability == AudioDeviceAvailability.Active
                && !excluding.Contains(device.Id)
                && device.Transport is not (AudioTransport.Virtual or AudioTransport.Hdmi))
            .Select((device, order) => (device, order, score:
                (renderAdapter is not null && AdapterOf(device.Name) == renderAdapter ? 4 : 0)
                + (device.Transport is AudioTransport.Usb or AudioTransport.BuiltIn ? 2 : 0)
                + (device.Transport is AudioTransport.BluetoothHandsFree ? 0 : 1)))
            .OrderByDescending(entry => entry.score)
            .ThenBy(entry => entry.order)
            .Select(entry => entry.device)
            .FirstOrDefault();
    }

    /// <summary>
    /// Имя карты из имени конечной точки: то, что в последних скобках.
    /// «Наушники (Realtek(R) Audio)» → «Realtek(R) Audio». Скобки внутри имени
    /// карты учитываются — поэтому разбор с конца по глубине, а не по
    /// последней открывающей.
    /// </summary>
    internal static string? AdapterOf(string? endpointName)
    {
        if (string.IsNullOrEmpty(endpointName) || endpointName[^1] != ')')
        {
            return null;
        }

        int depth = 0;
        for (int i = endpointName.Length - 1; i >= 0; i--)
        {
            depth += endpointName[i] switch
            {
                ')' => 1,
                '(' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return endpointName[(i + 1)..^1];
            }
        }

        return null;
    }

    /// <summary>
    /// Устройство, у которого нет пути от динамика до микрофона: наушники,
    /// гарнитура, трубка. Эхоподавителю на нём вычитать нечего.
    /// </summary>
    ///
    /// <remarks>
    /// Форм-фактора мало: беспроводные гарнитуры с донглом заявляют выход как
    /// «Динамики». Так делает JBL Quantum350 — и на ней до 1 октября 2026
    /// работали эхоподавитель и сильный шумодав, то есть ровно то, что на
    /// гарнитуре приседает и «роботизирует» голос оператора (см.
    /// <see cref="VoiceProcessor"/>). Поэтому вторым признаком — микрофон и
    /// выход одной USB- или Bluetooth-гарнитуры.
    /// </remarks>
    internal static bool IsEchoFree(MMDevice render, MMDevice capture)
    {
        if (ReadFormFactor(render) is FormFactorHeadphones or FormFactorHeadset or FormFactorHandset)
        {
            return true;
        }

        try
        {
            return IsHeadsetPair(TransportOf(capture), capture.FriendlyName, TransportOf(render), render.FriendlyName);
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
            return false;
        }
    }

    /// <summary>Тип подключения точки — без открытия потока, в отличие от <see cref="Describe"/>.</summary>
    internal static AudioTransport TransportOf(MMDevice endpoint) =>
        Classify(ReadEnumeratorName(endpoint), ReadFormFactor(endpoint));

    /// <summary>
    /// Микрофон и выход — одна гарнитура: USB или Bluetooth, одна и та же
    /// карта по имени.
    /// </summary>
    ///
    /// <remarks>
    /// Встроенная карта сюда не попадает намеренно: «Микрофон (Realtek)» и
    /// «Динамики (Realtek)» — это микрофон ноутбука и его же динамики, между
    /// которыми эхо как раз есть. USB-спикерфон (Jabra Speak и подобные)
    /// признак пройдёт, но у них эхоподавитель свой, аппаратный.
    /// </remarks>
    internal static bool IsHeadsetPair(
        AudioTransport captureTransport,
        string? captureName,
        AudioTransport renderTransport,
        string? renderName) =>
        captureTransport is AudioTransport.Usb or AudioTransport.BluetoothHandsFree
        && captureTransport == renderTransport
        && AdapterOf(captureName) is string adapter
        && string.Equals(adapter, AdapterOf(renderName), StringComparison.OrdinalIgnoreCase);

    // Форм-факторы из `EndpointFormFactor` (mmdeviceapi.h).
    private const int FormFactorHeadphones = 3;
    private const int FormFactorHeadset = 5;
    private const int FormFactorHandset = 6;

    private static int ReadFormFactor(MMDevice endpoint)
    {
        try
        {
            PropertyStore properties = endpoint.Properties;
            if (properties.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor))
            {
                object? value = properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value;
                if (value is not null)
                {
                    return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        catch (Exception e) when (IsAudioFailure(e) || e is FormatException or InvalidCastException or OverflowException)
        {
        }

        return -1;
    }

    /// <summary>
    /// Формат устройства: сколько каналов, на какой частоте и откуда это
    /// известно.
    ///
    /// Источников два, и порядок между ними существенный. <b>Микшер</b> — это
    /// то, что WASAPI действительно отдаст в общем режиме, но прочитать его
    /// можно только у работающего устройства. <b>Хранилище свойств</b>
    /// отвечает всегда, в том числе про невоткнутую гарнитуру, но описывает
    /// прошлое подключение.
    ///
    /// Второй источник добавлен не для полноты: без него в списке устройств
    /// у всего, что сейчас не подключено, стояло бы «частота неизвестна» — а
    /// именно там она и интересна. Замер W0 сверен с ним напрямую: у AirPods
    /// Pro точка режима связи заявляет 8000 Гц моно, стереоточка — 44 100 Гц,
    /// ровно как в таблице устройств.
    /// </summary>
    private static (int Channels, int SampleRate, bool Live) ReadFormat(MMDevice endpoint)
    {
        try
        {
            using AudioClient client = endpoint.CreateAudioClient();
            var mix = client.MixFormat;
            return (mix.Channels, mix.SampleRate, true);
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
            // Устройство не открывается: не подключено, занято монопольно,
            // сломан драйвер. Это не ошибка — переходим ко второму источнику.
        }

        try
        {
            PropertyStore properties = endpoint.Properties;
            if (properties.Contains(PropertyKeys.PKEY_AudioEngine_DeviceFormat) &&
                properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat].Value is byte[] blob)
            {
                // WAVEFORMATEX: каналы со смещения 2, частота — с 4.
                // Разбирается вручную, потому что свойство отдаётся сырым
                // блоком, а не разобранным форматом.
                if (blob.Length >= 8)
                {
                    int channels = BitConverter.ToUInt16(blob, 2);
                    long rate = BitConverter.ToUInt32(blob, 4);
                    if (channels > 0 && rate is > 0 and <= int.MaxValue)
                    {
                        return (channels, (int)rate, false);
                    }
                }
            }
        }
        catch (Exception e) when (IsAudioFailure(e))
        {
        }

        return (0, 0, false);
    }

    /// <summary>
    /// Отказы звуковой подсистемы, которые нормальны и не должны ронять
    /// приложение.
    ///
    /// Список узкий намеренно: ловить всё подряд здесь значило бы прятать свои
    /// же ошибки — обращение к освобождённому объекту, неверный аргумент — под
    /// видом «устройство отвалилось».
    ///
    /// <c>COMException</c> покрывает и собственные исключения NAudio: все они,
    /// включая <c>AudioDeviceDisconnectedException</c> на исчезнувшем
    /// устройстве, наследуются прямо от него.
    /// </summary>
    private static bool IsAudioFailure(Exception e) =>
        e is System.Runtime.InteropServices.COMException
            or System.Runtime.InteropServices.InvalidComObjectException
            or PlatformNotSupportedException;
}
