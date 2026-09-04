using NAudio.CoreAudioApi;

namespace AudioProbe;

/// <summary>
/// Выбор устройства по номеру из вывода <c>devices</c>.
///
/// Номер, а не имя: имена содержат скобки, кириллицу и пробелы, и передать их
/// в командную строку без ошибок не удаётся с первого раза никому. Матрицу
/// устройств прогоняют десятками запусков подряд, и цена опечатки — потерянный
/// прогон.
/// </summary>
internal static class Devices
{
    public static IReadOnlyList<MMDevice> All(MMDeviceEnumerator enumerator, DataFlow flow) =>
        [.. enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)];

    /// <summary>
    /// Устройство по номеру или по куску имени. Без того и другого — то, что
    /// система выбрала для связи.
    ///
    /// **Номер оказался ненадёжен, и это находка стенда, а не его недоделка.**
    /// Между двумя запусками подряд, без единого касания к железу, порядок
    /// перечисления поменялся: гарнитура переехала со второго места на первое.
    /// Значит и в приложении привязываться к порядку нельзя — устройство
    /// опознаётся своим идентификатором, а имя годится только для человека.
    /// </summary>
    public static MMDevice Pick(MMDeviceEnumerator enumerator, DataFlow flow, int? index, string? name = null)
    {
        IReadOnlyList<MMDevice> all = All(enumerator, flow);

        if (name is not null)
        {
            int found = -1;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                foreach (MMDevice device in all)
                {
                    device.Dispose();
                }

                throw new ArgumentOutOfRangeException(
                    nameof(name),
                    $"Устройства с «{name}» в имени нет. Список — AudioProbe devices.");
            }

            index = found;
        }

        if (index is null)
        {
            // Роль Communications, а не Multimedia: софтфон обязан ехать на том
            // устройстве, которое человек назначил для связи.
            MMDevice chosen = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
            foreach (MMDevice other in all)
            {
                if (other.ID != chosen.ID)
                {
                    other.Dispose();
                }
            }

            return chosen;
        }

        if (index.Value < 0 || index.Value >= all.Count)
        {
            foreach (MMDevice device in all)
            {
                device.Dispose();
            }

            throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Устройства с номером {index.Value} нет. Список — AudioProbe devices.");
        }

        for (int i = 0; i < all.Count; i++)
        {
            if (i != index.Value)
            {
                all[i].Dispose();
            }
        }

        return all[index.Value];
    }
}
