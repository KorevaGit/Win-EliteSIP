using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace EliteSIP.Audio;

/// <summary>
/// Какую обработку Windows и драйвер накладывают на наш поток захвата — до
/// нашей собственной.
/// </summary>
///
/// <remarks>
/// <para>
/// Зачем: тракт открывает микрофон в категории «связь» без <c>RAW</c>, и в этом
/// режиме система вправе обрабатывать голос сама — эффектами драйвера (Realtek,
/// Waves, Dolby, Intel Smart Sound) или, на Windows 11, своим нейросетевым
/// шумодавом Voice Clarity. Поверх этого работают наши AEC3 и шумодав. Двойной
/// шумодав — классический источник «подводного» голоса, а что именно включено
/// на машине, до 0.1.76 не было видно нигде (аудит 17 октября 2026).
/// </para>
/// <para>
/// Спрашивается <c>IAudioEffectsManager</c> (Windows 11, сборка 22000 и новее).
/// На Windows 10 его нет — тогда так и пишется. В NAudio 3 этого интерфейса
/// нет, а сам <c>IAudioClient</c> спрятан в закрытом поле; достаём его
/// отражением. Ошибка здесь стоит одной строки журнала «не узнать», а не
/// звонка; имя поля сторожит тест, чтобы обновление NAudio не сломало это
/// молча.
/// </para>
/// </remarks>
internal static class CaptureEffects
{
    /// <summary>Закрытое поле NAudio с интерфейсом клиента. Сторожит тест.</summary>
    internal const string ClientField = "audioClientInterface";

    private static readonly Guid EffectsManagerId = new("4460b3ae-4b44-4527-8676-7548a8acd260");

    private static readonly Guid AudioClientId = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");

    private const int ENoInterface = unchecked((int)0x80004002);

    /// <summary>Эффект на потоке: что это, включён ли и можно ли его переключить.</summary>
    internal readonly record struct Effect(Guid Id, bool CanSetState, bool IsOn);

    // ksmedia.h, AUDIO_EFFECT_TYPE_*: шестнадцать идентификаторов отличаются
    // только первым словом.
    private static Guid Type(uint first) => new(first, 0x8211, 0x11e2, 0x8c, 0x70, 0x2c, 0x27, 0xd7, 0xf0, 0x01, 0xfa);

    internal static readonly Guid EchoCancellation = Type(0x6f64adbe);
    internal static readonly Guid NoiseSuppression = Type(0x6f64adbf);
    internal static readonly Guid GainControl = Type(0x6f64adc0);
    internal static readonly Guid DeepNoiseSuppression = Type(0x6f64add0);

    private static readonly Dictionary<Guid, string> Names = new()
    {
        [EchoCancellation] = "эхоподавление",
        [NoiseSuppression] = "шумодав",
        [GainControl] = "АРУ",
        [Type(0x6f64adc1)] = "формирование луча",
        [Type(0x6f64adc2)] = "удаление тона",
        [Type(0x6f64adc3)] = "эквалайзер",
        [Type(0x6f64adc4)] = "выравнивание громкости",
        [Type(0x6f64adce)] = "компрессор",
        [DeepNoiseSuppression] = "шумодав ИИ (Voice Clarity)",
    };

    /// <summary>
    /// Строка для журнала про уже открытый (после <c>Initialize</c>) поток.
    /// </summary>
    /// <param name="client">Клиент захвата.</param>
    /// <param name="ownEcho">Работает ли наш AEC3.</param>
    /// <param name="ownNoise">Работает ли наш шумодав.</param>
    internal static string Describe(AudioClient client, bool ownEcho, bool ownNoise)
    {
        IReadOnlyList<Effect>? effects;
        string? failure;
        try
        {
            (effects, failure) = Read(client);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or MemberAccessException or TargetException)
        {
            effects = null;
            failure = e.Message;
        }

        return Summarize(effects, failure, ownEcho, ownNoise);
    }

    /// <summary>
    /// Строка журнала по списку эффектов — чистой функцией, чтобы проверять
    /// тестом.
    /// </summary>
    /// <param name="effects"><c>null</c> — список узнать не удалось.</param>
    /// <param name="failure">Почему не удалось; <c>null</c> — Windows 10, интерфейса нет.</param>
    internal static string Summarize(IReadOnlyList<Effect>? effects, string? failure, bool ownEcho, bool ownNoise)
    {
        const string Head = "обработка Windows на микрофоне: ";

        if (effects is null)
        {
            return Head + (failure is null
                ? "не узнать — список эффектов есть только в Windows 11"
                : $"не узнать ({failure})");
        }

        List<string> on = [];
        List<string> off = [];
        foreach (Effect effect in effects)
        {
            string name = Names.TryGetValue(effect.Id, out string? known)
                ? known
                : effect.Id.ToString("D", CultureInfo.InvariantCulture)[..8];
            string label = effect.CanSetState ? name + " (переключаемый)" : name;
            (effect.IsOn ? on : off).Add(label);
        }

        string text = Head + (on.Count == 0 ? "нет" : string.Join(", ", on));
        if (off.Count > 0)
        {
            text += "; выключены: " + string.Join(", ", off);
        }

        // Двойная обработка — то, ради чего строка заведена. Говорим прямо,
        // что поверх чего работает.
        List<string> doubled = [];
        if (ownEcho && effects.Any(e => e.IsOn && e.Id == EchoCancellation))
        {
            doubled.Add("эхоподавление");
        }

        if (ownNoise && effects.Any(e => e.IsOn && (e.Id == NoiseSuppression || e.Id == DeepNoiseSuppression)))
        {
            doubled.Add("шумодав");
        }

        if (doubled.Count > 0)
        {
            text += " — ДВОЙНАЯ обработка: наш " + string.Join(" и ", doubled) + " поверх системного";
        }

        return text;
    }

    /// <summary>Достаёт список эффектов. <c>(null, null)</c> — интерфейса нет (Windows 10).</summary>
    private static unsafe (IReadOnlyList<Effect>? Effects, string? Failure) Read(AudioClient client)
    {
        FieldInfo? field = typeof(AudioClient).GetField(ClientField, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(client) is not object wrapper)
        {
            return (null, "в NAudio нет поля " + ClientField);
        }

        // NAudio 3 держит интерфейс сгенерированной обёрткой ComWrappers, а
        // сам тип у него внутренний: берём из обёртки голый IUnknown и дальше
        // идём по таблице методов сами.
        if (!ComWrappers.TryGetComInstance(wrapper, out IntPtr unknown))
        {
            return (null, "клиент NAudio — не обёртка COM");
        }

        IntPtr audioClient;
        try
        {
            Guid clientId = AudioClientId;
            int queried = Marshal.QueryInterface(unknown, in clientId, out audioClient);
            if (queried < 0)
            {
                return (null, $"QueryInterface 0x{queried:X8}");
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }

        IntPtr manager;
        int hr;
        try
        {
            // IUnknown (0–2), Initialize … SetEventHandle (3–13), GetService (14).
            var getService = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)audioClient + 14));
            Guid iid = EffectsManagerId;
            IntPtr result;
            hr = getService(audioClient, &iid, &result);
            manager = result;
        }
        finally
        {
            Marshal.Release(audioClient);
        }

        if (hr == ENoInterface)
        {
            return (null, null);
        }

        if (hr < 0 || manager == IntPtr.Zero)
        {
            return (null, $"GetService 0x{hr:X8}");
        }

        try
        {
            // IUnknown (0–2), Register (3), Unregister (4), GetAudioEffects (5).
            var getAudioEffects = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, int>)(*(*(void***)manager + 5));

            IntPtr array;
            uint count;
            hr = getAudioEffects(manager, &array, &count);
            if (hr < 0)
            {
                return (null, $"GetAudioEffects 0x{hr:X8}");
            }

            try
            {
                var effects = new List<Effect>((int)count);
                for (int i = 0; i < count; i++)
                {
                    // AUDIO_EFFECT: GUID id; BOOL canSetState; AUDIO_EFFECT_STATE state.
                    NativeEffect native = Marshal.PtrToStructure<NativeEffect>(array + (i * sizeof(NativeEffect)));
                    effects.Add(new Effect(native.Id, native.CanSetState != 0, native.State != 0));
                }

                return (effects, null);
            }
            finally
            {
                Marshal.FreeCoTaskMem(array);
            }
        }
        finally
        {
            Marshal.Release(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEffect
    {
        public Guid Id;
        public int CanSetState;
        public int State;
    }
}
