using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.Audio;

namespace EliteSIP.App.Settings;

/// <summary>Разделы менеджерских настроек.</summary>
///
/// <remarks>
/// Порядок — по тому, как часто сюда заходят. «Работа» первой: её открывают в
/// начале смены и по самому важному поводу — телефон не работает вовсе, пока
/// рабочее место выбрано неверно. «Звук» следом, потому что его правят при
/// каждой смене наушников; «Техподдержка» последней, потому что туда идут,
/// когда уже сломалось.
/// </remarks>
public enum SettingsSectionKind
{
    Work,
    Audio,
    Ringtone,
    Appearance,
    Support,
}

/// <summary>Пункт бокового списка.</summary>
public sealed record SettingsSectionItem(SettingsSectionKind Kind, string Title, string Glyph);

/// <summary>Устройство в выпадающем списке. <c>null</c> в поле — «системное».</summary>
///
/// <remarks>
/// Пункт «системное по умолчанию» — не отсутствие выбора, а выбор: он значит
/// «ходи за устройством к системе каждый раз», и от него зависит, переедет ли
/// звук вслед за воткнутой гарнитурой.
/// </remarks>
public sealed record AudioDeviceOption(string? Id, string Name);

/// <summary>Состояние окна настроек менеджера.</summary>
public sealed class SettingsViewModel : Observable
{
    private SettingsSectionKind _section = SettingsSectionKind.Work;

    public SettingsViewModel(AppSettings settings)
    {
        Settings = settings;

        Sections =
        [
            new(SettingsSectionKind.Work, Strings.Get("SectionWork"), "person.crop.circle"),
            new(SettingsSectionKind.Audio, Strings.Get("SectionAudio"), "mic.fill"),
            new(SettingsSectionKind.Ringtone, Strings.Get("SectionRingtone"), "bell"),
            // Своего значка у оформления в комплекте нет. `gearshape` — не
            // «тема», а «настройка вообще», и это признанная неточность: раздел
            // опознаётся подписью, а значок держит строку в колонке с прочими.
            new(SettingsSectionKind.Appearance, Strings.Get("SectionAppearance"), "gearshape"),
            new(SettingsSectionKind.Support, Strings.Get("SectionSupport"), "stethoscope"),
        ];

        ReloadDevices();
    }

    public AppSettings Settings { get; }

    public IReadOnlyList<SettingsSectionItem> Sections { get; }

    public SettingsSectionKind Section
    {
        get => _section;
        set
        {
            Set(ref _section, value);
            foreach (var name in new[]
            {
                nameof(ShowsWork), nameof(ShowsAudio), nameof(ShowsRingtone),
                nameof(ShowsAppearance), nameof(ShowsSupport),
            })
            {
                NotifyChanged(name);
            }
        }
    }

    public bool ShowsWork => _section is SettingsSectionKind.Work;

    public bool ShowsAudio => _section is SettingsSectionKind.Audio;

    public bool ShowsRingtone => _section is SettingsSectionKind.Ringtone;

    public bool ShowsAppearance => _section is SettingsSectionKind.Appearance;

    public bool ShowsSupport => _section is SettingsSectionKind.Support;

    public ObservableCollection<AudioDeviceOption> Inputs { get; } = [];

    public ObservableCollection<AudioDeviceOption> Outputs { get; } = [];

    /// <summary>
    /// Оба устройства заданы явно и разными — значит системного эхоподавления
    /// не будет.
    /// </summary>
    ///
    /// <remarks>
    /// Текст под этим признаком не обещает эха, и это правка после разбора
    /// макета: эха не будет ни в наушниках, ни в гарнитуре — там микрофон
    /// акустически развязан с динамиком. Эхо случается на колонках. Прежняя
    /// формулировка пугала им всегда, то есть чаще всего впустую.
    /// </remarks>
    public bool NeedsAggregate
        => Settings.Audio.InputDeviceId is not null && Settings.Audio.OutputDeviceId is not null;

    /// <summary>Версия и сборка одной строкой.</summary>
    ///
    /// <remarks>
    /// Первое, что просит поддержка по телефону, — «какая у вас версия». В
    /// оригинале эта строка сперва стояла только за административным паролем, и
    /// менеджеру было нечего ответить.
    /// </remarks>
    public static string Version
        => Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "—";

    /// <summary>Имя выбранного рингтона — или «Стандартный».</summary>
    public string RingtoneName
    {
        get
        {
            if (!Settings.Ringtone.HasCustomSound)
            {
                return Strings.Get("RingtoneDefault");
            }

            var name = Path.GetFileName(Settings.Ringtone.CustomSoundPath)!;

            // Пропавший файл называется прямо: рингтон в этом случае молча
            // вернётся к стандартному, и человек должен понимать почему, а не
            // слышать не то.
            return Settings.Ringtone.SoundIsMissing
                ? name + Strings.Get("RingtoneMissingSuffix")
                : name;
        }
    }

    /// <summary>Толкает подпись рингтона: она считается, а не хранится.</summary>
    public void NotifyRingtoneName() => NotifyChanged(nameof(RingtoneName));

    /// <summary>Возвращает сегменты языка к записанному в настройках.</summary>
    public void NotifyLanguage() => Settings.Appearance.NotifyLanguageChanged();

    /// <summary>Перечитывает устройства у системы.</summary>
    ///
    /// <remarks>
    /// Списки берутся готовыми при создании окна, а не спрашиваются у Core Audio
    /// при показе раздела: в оригинале именно такой опрос давал видимую
    /// задержку — он успевал не к первой отрисовке, а к следующей, и строки под
    /// выпадающими списками появлялись на глазах, сдвигая выключатели вниз.
    /// </remarks>
    public void ReloadDevices()
    {
        Fill(Inputs, AudioDeviceDirection.Capture, Strings.Get("AudioSystemDefaultInput"));
        Fill(Outputs, AudioDeviceDirection.Render, Strings.Get("AudioSystemDefaultOutput"));
        NotifyChanged(nameof(NeedsAggregate));
    }

    private static void Fill(
        ObservableCollection<AudioDeviceOption> target,
        AudioDeviceDirection direction,
        string systemTitle)
    {
        target.Clear();

        // «Системное» первым пунктом, а не отдельным выключателем рядом: это
        // одно и то же поле — какое устройство брать.
        var system = AudioDeviceCatalog.Default(direction);
        target.Add(new AudioDeviceOption(
            Id: null,
            Name: system is null ? systemTitle : $"{systemTitle} — {system.Name}"));

        foreach (var device in AudioDeviceCatalog.Devices(direction))
        {
            target.Add(new AudioDeviceOption(device.Id, device.Name));
        }
    }
}
