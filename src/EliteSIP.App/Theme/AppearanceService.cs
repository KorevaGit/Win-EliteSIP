using System.Windows.Media;
using System.Windows;
using Microsoft.Win32;

namespace EliteSIP.App.Theme;

/// <summary>Оформление: светлое, тёмное или как в системе.</summary>
///
/// <remarks>
/// Три значения, а не тумблер «тёмная тема»: «как в системе» — не середина
/// между двумя другими, а отказ выбирать, и он же значение по умолчанию.
/// </remarks>
public enum Appearance
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Держит палитру приложения в согласии с выбором оператора и с системой.
/// </summary>
///
/// <remarks>
/// На macOS этого класса не было вовсе: там хватало <c>NSApp.appearance</c>, а
/// цвета брались системные и перекрашивались сами. Здесь цвета свои, палитр
/// две, и переключать их приходится руками — заодно и следить за системой.
///
/// Словарь подменяется целиком, а не по кисти: половина перекрашенного окна
/// хуже неперекрашенного, потому что читается как поломка, а не как настройка.
/// </remarks>
public sealed class AppearanceService : IDisposable
{
    private static readonly Uri LightPalette = new("Theme/Palette.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPalette = new("Theme/Palette.Dark.xaml", UriKind.Relative);

    private readonly Application _application;
    private ResourceDictionary? _current;
    private Appearance _appearance = Appearance.System;
    private bool _disposed;

    public AppearanceService(Application application)
    {
        _application = application;

        // Системная тема меняется не только руками в «Параметрах»: Windows
        // переключает её по расписанию «светлая днём, тёмная вечером». Софтфон
        // висит поверх CRM весь день и переживёт такое переключение открытым.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Выбор оператора. Задаётся настройками менеджера.</summary>
    public Appearance Appearance
    {
        get => _appearance;
        set
        {
            _appearance = value;
            Apply();
        }
    }

    /// <summary>Тёмная ли палитра сейчас — с учётом системы.</summary>
    public bool IsDark => _appearance switch
    {
        Appearance.Light => false,
        Appearance.Dark => true,
        _ => SystemPrefersDark(),
    };

    /// <summary>Ставит палитру в ресурсы приложения.</summary>
    public void Apply()
    {
        var palette = new ResourceDictionary { Source = IsDark ? DarkPalette : LightPalette };
        Tint(palette, IsDark);

        // Сначала добавить, потом убрать прежний: наоборот — это кадр, в
        // котором ни одна кисть не находится, и WPF на нём рисует окно
        // системными цветами, то есть мигает.
        _application.Resources.MergedDictionaries.Add(palette);

        if (_current is not null)
        {
            _application.Resources.MergedDictionaries.Remove(_current);
        }

        _current = palette;

        // Полоса заголовка живёт вне палитры: её рисует диспетчер окон, и
        // словарь ресурсов до неё не достаёт. Переставляется она здесь же,
        // чтобы окно не осталось тёмным под белой полосой.
        SystemCaption.ApplyToOpenWindows(_application, IsDark);
    }

    /// <summary>Переводит палитру на акцентный цвет системы.</summary>
    ///
    /// <remarks>
    /// Переписываются ровно те ключи, что несут «цвет приложения»: сам акцент,
    /// выделение, точка разговора — и фоны окна и панелей, подкрашенные им же
    /// на несколько процентов. Всё остальное — текст, состояния, кнопки приёма
    /// и сброса — остаётся своим: зелёный «Ответить» у человека с красным
    /// акцентом должен остаться зелёным.
    ///
    /// Ключа в реестре может не быть (акцент не выбирали ни разу), и это не
    /// беда: палитра тогда остаётся ровно такой, какой пришла из файла.
    /// </remarks>
    private static void Tint(ResourceDictionary palette, bool dark)
    {
        if (SystemAccent.Read(dark) is not { } accent)
        {
            return;
        }

        palette["AccentBrush"] = new SolidColorBrush(accent);
        palette["SelectionBrush"] = new SolidColorBrush(
            Color.FromArgb(0x4C, accent.R, accent.G, accent.B));
        palette["StatusInCallBrush"] = new SolidColorBrush(accent);

        // Заливка — базовой ступенью, а не осветлённой: так в Параметрах
        // Windows 10. Под заливкой белый бегунок переключателя, и светлая
        // ступень тёмной темы делала бы его невидимым.
        if (SystemAccent.Read(dark: false) is { } fill)
        {
            palette["AccentFillBrush"] = new SolidColorBrush(fill);
        }

        // Фон акцентом больше не подкрашивается. Так было в 0.1.44–0.1.45, и
        // на живой машине вышло не «как в системе», а своё третье оформление:
        // Windows 10 держит фон нейтральным, чёрным или белым, а акцент кладёт
        // только на то, что нажимается, — переключатели, ползунки, отметку
        // выбранного раздела.
    }

    private static bool SystemPrefersDark()
    {
        // Реестр, а не `SystemParameters`: у WPF нет свойства «тёмная ли тема»
        // — его добавляли в WinUI, а не в WPF. Ключ читается заново на каждый
        // вопрос, потому что менять его может кто угодно и когда угодно.
        //
        // AppsUseLightTheme — тема приложений; SystemUsesLightTheme отвечает за
        // панель задач, и брать надо первый: софтфон — приложение.
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

        // Ключа может не быть — на Windows 10 до 1809 его нет, а нижняя планка
        // проекта 22H2 только по договорённости, а не по проверке. Нет ключа —
        // светлая: это то, что показывает система, у которой выбора нет.
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs change)
    {
        if (change.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
        {
            return;
        }

        // Смена акцента касается любой палитры, а не только «как в системе»:
        // выбранная руками тёмная тема тоже красится акцентом.
        if (_appearance is not Appearance.System
            && change.Category is not UserPreferenceCategory.Color)
        {
            return;
        }

        // Событие приходит не в потоке интерфейса, а ресурсы приложения — его
        // собственность.
        _application.Dispatcher.BeginInvoke(Apply);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Подписка на `SystemEvents` статическая и живёт дольше приложения:
        // неотписанный обработчик держит объект до конца процесса и на выходе
        // стреляет по уже закрытому окну.
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _disposed = true;
    }
}
