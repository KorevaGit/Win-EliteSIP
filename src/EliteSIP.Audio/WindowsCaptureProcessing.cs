namespace EliteSIP.Audio;

/// <summary>
/// Обработка Windows на микрофоне: эффекты режима связи драйвера и ОС поверх
/// нашей цепочки — или сырой поток (RAW).
/// </summary>
///
/// <remarks>
/// <para>
/// Категория потока остаётся «связь» в любом положении: по ней Windows
/// приглушает остальные звуки на время звонка. Переключается только флаг RAW,
/// и только у микрофона. На выводе RAW снимал бы защиту динамиков и коррекцию
/// ноутбука, а жалоб на вывод нет.
/// </para>
/// <para>
/// <b>Почему по умолчанию «авто», а не «вкл».</b> Режим связи — это то, что
/// заложил производитель: эхоподавитель, шумодав, АРУ, формирование луча, на
/// Windows 11 ещё Voice Clarity. Поверх работают наши AEC3, шумодав и блок
/// голосов вокруг. На гарнитуре системная обработка ничего не добавляет, а
/// отнимает предсказуемость. Замер W0: с RAW уровень микрофона держался в
/// пределах −41,5…−43,1 дБ на девяти прогонах, с обработкой системы гулял на
/// 26 дБ — это АРУ драйвера, и в паузах она вытягивает как раз голоса соседей.
/// MicroSIP, на котором у тестовой группы жалоб на соседей нет, режима связи
/// не просит вовсе (аудит 17 октября 2026). На встроенном микрофоне ноутбука,
/// наоборот, обработка полезна: формирование луча сужает то, что слышит
/// «набор микрофонов», и без него соседи громче.
/// </para>
/// </remarks>
public enum WindowsCaptureProcessing
{
    /// <summary>На гарнитуре — RAW, на остальном — обработка Windows.</summary>
    Auto,

    /// <summary>Всегда обработка Windows: как до 0.1.76.</summary>
    On,

    /// <summary>Всегда RAW, если устройство его умеет.</summary>
    Off,
}

/// <summary>Решение о RAW — чистой функцией, чтобы проверять тестом.</summary>
public static class WindowsCaptureProcessingPolicy
{
    /// <summary>Нужен ли сырой поток.</summary>
    /// <param name="mode">Положение переключателя.</param>
    /// <param name="captureTransport">Как подключён микрофон.</param>
    /// <param name="isHeadsetPair">Микрофон и выход — одна USB- или Bluetooth-гарнитура.</param>
    /// <param name="outputIsEchoFree">Выход — наушники или гарнитура по форм-фактору Windows.</param>
    public static bool WantsRaw(
        WindowsCaptureProcessing mode,
        AudioTransport captureTransport,
        bool isHeadsetPair,
        bool outputIsEchoFree) => mode switch
        {
            WindowsCaptureProcessing.On => false,
            WindowsCaptureProcessing.Off => true,

            // Гарнитура — по общей карте или по наушникам на выходе. Но
            // встроенный микрофон при наушниках — это всё ещё «набор
            // микрофонов» ноутбука, и луч ему нужен.
            _ => isHeadsetPair
                || (outputIsEchoFree && captureTransport is not AudioTransport.BuiltIn),
        };
}
