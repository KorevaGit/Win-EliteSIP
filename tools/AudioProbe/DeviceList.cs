using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioProbe;

/// <summary>
/// Что видит приложение вместо <c>AudioDevice.all()</c> из оригинала.
///
/// Печатает не только имена, но и три числа, от которых зависит вся
/// архитектура тракта: формат микшера, период устройства по умолчанию и
/// минимальный период. В macOS-версии соответствующее решение принимала
/// система, и знать их было незачем; здесь на них считается размер кадра и
/// выясняется, достижимы ли вообще 20 мс без режима исключительного доступа.
/// </summary>
internal static class DeviceList
{
    public static int Run()
    {
        using var enumerator = new MMDeviceEnumerator();

        Print(enumerator, DataFlow.Capture, "ЗАХВАТ");
        Console.WriteLine();
        Print(enumerator, DataFlow.Render, "ВОСПРОИЗВЕДЕНИЕ");

        return 0;
    }

    private static void Print(MMDeviceEnumerator enumerator, DataFlow flow, string title)
    {
        Console.WriteLine($"=== {title} ===");

        string? defaultId = null;
        try
        {
            using MMDevice def = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
            defaultId = def.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Устройств этого направления в системе нет вовсе. Это не ошибка
            // стенда: у машины без микрофона так и будет.
        }

        IReadOnlyList<MMDevice> all = Devices.All(enumerator, flow);
        for (int i = 0; i < all.Count; i++)
        {
            MMDevice device = all[i];
            using (device)
            {
                string mark = device.ID == defaultId ? "  ← по умолчанию для связи" : string.Empty;
                Console.WriteLine($"[{i}] {device.FriendlyName}{mark}");

                try
                {
                    using AudioClient client = device.CreateAudioClient();
                    WaveFormat mix = client.MixFormat;

                    Console.WriteLine(
                        "    микшер:  {0} Гц, {1} кан., {2} бит",
                        mix.SampleRate.ToString(CultureInfo.InvariantCulture),
                        mix.Channels.ToString(CultureInfo.InvariantCulture),
                        mix.BitsPerSample.ToString(CultureInfo.InvariantCulture));

                    Console.WriteLine(
                        "    период:  {0:F2} мс по умолчанию, {1:F2} мс минимум",
                        client.DefaultDevicePeriod / 10000.0,
                        client.MinimumDevicePeriod / 10000.0);

                    // Громкость и текущий пик.
                    //
                    // Этого здесь не было, и зря: замер эхоподавления двое суток
                    // показывал «эхо не давится», а прямой замер пути эха дал
                    // корреляцию 0,035 — то есть микрофон почти не слышал того,
                    // что мы играли. Первое, что надо было исключить, — что
                    // динамики просто убавлены.
                    Console.WriteLine(
                        "    громкость: {0:P0}{1},  пик сейчас {2:P1}",
                        device.AudioEndpointVolume.MasterVolumeLevelScalar,
                        device.AudioEndpointVolume.Mute ? ", ВЫКЛЮЧЕН" : string.Empty,
                        device.AudioMeterInformation.MasterPeakValue);
                }
                catch (System.Runtime.InteropServices.COMException e)
                {
                    // Устройство есть в списке, но не открывается: занято
                    // монопольно, отвалилось между перечислением и открытием,
                    // сломан драйвер. Стенд должен продолжить перечисление, а
                    // не упасть на первом таком.
                    Console.WriteLine($"    не открылось: 0x{e.HResult:X8}");
                }
            }
        }
    }
}
