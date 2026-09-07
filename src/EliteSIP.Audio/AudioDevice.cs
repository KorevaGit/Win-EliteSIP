namespace EliteSIP.Audio;

/// <summary>
/// Куда устройство ведёт звук. На Windows конечная точка всегда односторонняя.
///
/// В оригинале направления не было: на macOS одно устройство HAL несёт и вход,
/// и выход, и признаки <c>isInput</c>/<c>isOutput</c> считались по числу
/// каналов в каждой области. Здесь захват и воспроизведение — две разные
/// конечные точки с разными идентификаторами, даже когда железо одно.
/// </summary>
public enum AudioDeviceDirection
{
    Capture,
    Render,
}

/// <summary>
/// Чем устройство подключено.
///
/// <b>Определяется по перечислителю устройства</b> (<c>DEVPKEY_Device_EnumeratorName</c>),
/// а не по форм-фактору конечной точки. Форм-фактор напрашивается первым и не
/// годится: у проводной USB-гарнитуры LifeChat и у AirPods он одинаковый —
/// <c>Headset</c>, — то есть он не различает ровно тот случай, ради которого
/// тип и нужен.
/// </summary>
public enum AudioTransport
{
    /// <summary>Встроенное звуковое устройство (<c>HDAUDIO</c>).</summary>
    BuiltIn,

    /// <summary>USB — провод или донгл 2,4 ГГц (<c>USB</c>).</summary>
    Usb,

    /// <summary>Bluetooth в режиме воспроизведения, A2DP (<c>BTHENUM</c>).</summary>
    Bluetooth,

    /// <summary>
    /// Bluetooth в режиме двусторонней связи, HFP (<c>BTHHFENUM</c>).
    ///
    /// <b>Главное отличие от macOS.</b> Там режим гарнитуры был состоянием
    /// одного и того же устройства: при поднятии микрофона у устройства вывода
    /// появлялись входные каналы и падала частота, и признак приходилось
    /// нащупывать замерами. На Windows режим связи — это <b>отдельная
    /// конечная точка со своим перечислителем</b>, и опознаётся он прямо, без
    /// догадок. Замер W0 это подтвердил: у AirPods Pro точка
    /// <c>BTHHFENUM</c> заявляет 8000 Гц моно, а точка <c>BTHENUM</c> —
    /// 44 100 Гц стерео.
    /// </summary>
    BluetoothHandsFree,

    /// <summary>Звук через видеовыход.</summary>
    Hdmi,

    /// <summary>Программное устройство: виртуальный кабель, удалённый рабочий стол.</summary>
    Virtual,

    /// <summary>Что-то ещё. Само имя перечислителя лежит в <see cref="AudioDevice.TransportName"/>.</summary>
    Other,
}

/// <summary>
/// В каком состоянии устройство числится у системы.
///
/// <b>Читать это как «работает или нет» нельзя</b>, и это записано в
/// <c>docs/W0-AUDIO.md</c> ценой двух испорченных прогонов: донгл JBL остаётся
/// в системе, его конечные точки числятся действующими, поток открывается без
/// ошибки и отдаёт мусор — при том что самой гарнитуры рядом нет. Состояние
/// отвечает только на вопрос «есть ли устройство у системы»; на вопрос
/// «идёт ли через него звук» отвечают счётчики тракта.
/// </summary>
public enum AudioDeviceAvailability
{
    /// <summary>Действует. Открыть можно — что из него польётся, вопрос отдельный.</summary>
    Active,

    /// <summary>Разъём свободен: устройство знакомо системе, но не воткнуто.</summary>
    Unplugged,

    /// <summary>Выключено человеком в параметрах звука.</summary>
    Disabled,

    /// <summary>Драйвер есть, устройства нет.</summary>
    Absent,
}

/// <summary>
/// Звуковое устройство в терминах Windows Core Audio.
///
/// Своя структура, а не голый <c>MMDevice</c>, по той же причине, что и в
/// оригинале: тип подключения нам не косметика, а поведение. Но одно различие
/// с macOS существенное и упрощающее.
///
/// <b>Идентификатор один, а не два.</b> В оригинале приходилось хранить и
/// <c>AudioDeviceID</c> (живёт до перезагрузки), и <c>uid</c> (постоянный, его
/// и клали в настройки). На Windows строка идентификатора конечной точки
/// постоянна: переживает и перезагрузку, и переподключение устройства. Значит
/// в настройки ложится она же, и разъезда двух идентификаторов не бывает.
///
/// Правило W0 остаётся в силе: <b>устройство опознаётся идентификатором,
/// порядок и имя — только для человека.</b> Между двумя запусками подряд, без
/// касания к железу, порядок перечисления менялся, и один прогон из-за этого
/// молча ушёл не на ту гарнитуру.
/// </summary>
/// <param name="Id">Постоянный идентификатор конечной точки. Он же хранится в настройках.</param>
/// <param name="Name">Имя для человека. Для опознания не годится.</param>
/// <param name="Direction">Захват или воспроизведение.</param>
/// <param name="Transport">Чем подключено.</param>
/// <param name="TransportName">Имя перечислителя как есть — для журнала и для разбора незнакомых случаев.</param>
/// <param name="Availability">Что о нём думает система.</param>
/// <param name="Channels">Число каналов в объявленном формате.</param>
/// <param name="SampleRate">Частота, на которой устройство работает. 0 — узнать не удалось.</param>
/// <param name="FormatIsLive">
/// Формат взят у работающего устройства (микшер WASAPI), а не из хранилища
/// свойств. У неподключённого устройства второе — единственный источник, и оно
/// говорит о прошлом подключении, а не о будущем.
/// </param>
public sealed record AudioDevice(
    string Id,
    string Name,
    AudioDeviceDirection Direction,
    AudioTransport Transport,
    string TransportName,
    AudioDeviceAvailability Availability,
    int Channels,
    int SampleRate,
    bool FormatIsLive)
{
    /// <summary>
    /// Открытие микрофона на такой гарнитуре испортит звук во всей системе.
    ///
    /// На macOS это было свойством устройства, которое ещё предстоит перевести
    /// в режим связи. Здесь режим связи — уже отдельная точка, поэтому признак
    /// читается прямо: если мы собираемся захватывать с <c>BTHHFENUM</c>,
    /// система уже в режиме связи или встанет в него при открытии.
    /// </summary>
    public bool SwitchesToHeadsetMode => Transport == AudioTransport.BluetoothHandsFree;

    /// <summary>
    /// Полосу режет гарнитура, а не кодек.
    ///
    /// Записано ради одного продуктового вывода из W0: Windows подключает
    /// AirPods по HFP в узкой полосе 8 кГц (CVSD, не mSBC), и на них
    /// <b>G.722 теряет смысл</b> — сервер отдаст 16 кГц, человек услышит
    /// телефонное качество. Согласовывать широкополосный кодек на таком
    /// устройстве значит платить полосой за то, чего никто не услышит.
    /// </summary>
    public bool IsNarrowband => SampleRate is > 0 and <= 8000;

    /// <summary>
    /// Строка для журнала. Формат один и тот же в приложении и в
    /// <c>AudioProbe</c>, чтобы отчёты можно было сравнивать глазами.
    /// </summary>
    public string Summary
    {
        get
        {
            string direction = Direction == AudioDeviceDirection.Capture ? "вход" : "выход";
            string transport = TransportTitle(Transport, TransportName);
            string rate = SampleRate > 0
                ? SampleRate + " Гц" + (FormatIsLive ? string.Empty : " (заявлено)")
                : "частота неизвестна";
            string state = Availability == AudioDeviceAvailability.Active
                ? string.Empty
                : ", " + AvailabilityTitle(Availability);

            return $"{Name} [{transport}], {direction} {Channels} кан., {rate}{state}";
        }
    }

    internal static string TransportTitle(AudioTransport transport, string rawName) => transport switch
    {
        AudioTransport.BuiltIn => "встроенное",
        AudioTransport.Usb => "USB",
        AudioTransport.Bluetooth => "Bluetooth",
        AudioTransport.BluetoothHandsFree => "Bluetooth, режим связи",
        AudioTransport.Hdmi => "HDMI",
        AudioTransport.Virtual => "виртуальное",
        _ => string.IsNullOrEmpty(rawName) ? "иное" : $"иное ({rawName})",
    };

    internal static string AvailabilityTitle(AudioDeviceAvailability availability) => availability switch
    {
        AudioDeviceAvailability.Active => "действует",
        AudioDeviceAvailability.Unplugged => "не воткнуто",
        AudioDeviceAvailability.Disabled => "выключено",
        _ => "отсутствует",
    };
}
