namespace EliteSIP.Audio;

/// <summary>
/// Правила выбора устройства, которые не зависят от звуковой подсистемы.
/// </summary>
///
/// <remarks>
/// До 20 октября 2026 жили в <c>AudioDeviceCatalog</c> рядом с MMDevice API.
/// Вынесены, потому что это не обёртка над Windows, а решения продукта —
/// какой микрофон брать взамен, что считать вебкамерой и гарнитурой, — и
/// Linux-версия должна принимать их так же. Имена устройств на Linux другие
/// (PulseAudio пишет описание карты, а не «Микрофон (карта)»), поэтому
/// <see cref="AdapterOf"/> там понадобится свой разбор; сами правила — эти.
/// </remarks>
internal static class AudioDeviceRules
{
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
    /// что и вывод, потом любая проводная или USB, затем Bluetooth в режиме
    /// гарнитуры (он переводит наушники в моно 8 кГц), последней — вебкамера.
    /// Виртуальные и HDMI не берутся вовсе.
    ///
    /// Камера — ниже всех, даже Bluetooth: она стоит на мониторе в полуметре
    /// и дальше, и в линию с неё идут комната и соседи (тестовая группа,
    /// 17 октября 2026). До 0.1.76 USB-камера шла вровень с микрофоном
    /// ноутбука, и ничью решал порядок перечисления.
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
                IsCamera(device.Name)
                    ? -1
                    : (renderAdapter is not null && AdapterOf(device.Name) == renderAdapter ? 4 : 0)
                        + (device.Transport is AudioTransport.Usb or AudioTransport.BuiltIn ? 2 : 0)
                        + (device.Transport is AudioTransport.BluetoothHandsFree ? 0 : 1)))
            .OrderByDescending(entry => entry.score)
            .ThenBy(entry => entry.order)
            .Select(entry => entry.device)
            .FirstOrDefault();
    }

    /// <summary>
    /// Микрофон вебкамеры — по имени конечной точки.
    /// </summary>
    ///
    /// <remarks>
    /// По имени, потому что другого признака у Windows нет: камера заявляет
    /// микрофон обычным USB-устройством с форм-фактором «микрофон», как и
    /// гарнитура. Имена — то, что пишут драйверы: «Микрофон (HD Pro Webcam
    /// C920)», «Microphone (Logitech BRIO)», «Микрофон (USB2.0 Camera)». Ошибка
    /// здесь дешёвая: камера, не узнанная по имени, просто идёт вровень с
    /// прочими USB, как раньше.
    /// </remarks>
    internal static bool IsCamera(string? name) =>
        !string.IsNullOrEmpty(name) && CameraName.IsMatch(name);

    private static readonly System.Text.RegularExpressions.Regex CameraName = new(
        @"web\s?cam|camera|камер|веб-?кам|lifecam|streamcam|facecam|\bbrio\b|\bkiyo\b|\bc(270|310|505|615|920|922|925|930|960|980)\w*\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.Compiled);

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
}
