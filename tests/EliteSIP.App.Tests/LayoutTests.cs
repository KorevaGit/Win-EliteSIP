using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EliteSIP.AdminAccess;
using EliteSIP.App.FirstRun;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

/// <summary>
/// Разметка окон: влезает ли содержимое в размер, который ему дан.
/// </summary>
///
/// <remarks>
/// Заведено после 0.1.58–0.1.59: срезанные шаги мастера и клавиши панели,
/// уводившие окно за край экрана, находил только живой прогон. `Grid` молча
/// отдаёт содержимому меньше, чем оно просит, и по разметке этого не видно.
/// </remarks>
public sealed class LayoutTests
{
    // Клиентская область мастера на Windows 10 при 100 %: окно 460 × 440 без
    // рамок по 8 точек и заголовка в 31.
    private const double WizardClientWidth = 460 - 16;
    private const double WizardClientHeight = 440 - 8 - 31;

    public static TheoryData<FirstRunStep, Appearance> Steps()
    {
        var data = new TheoryData<FirstRunStep, Appearance>();
        foreach (var step in Enum.GetValues<FirstRunStep>())
        {
            data.Add(step, Appearance.Light);
            data.Add(step, Appearance.Dark);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public void Шаг_мастера_помещается_без_прокрутки(FirstRunStep step, Appearance theme) => WpfHost.Run(() =>
    {
        WpfHost.UseTheme(theme);
        var model = new FirstRunViewModel(new AppSettings(), new AdminAccessState());
        WpfHost.SetPrivate(model, nameof(FirstRunViewModel.Step), step);

        var window = new FirstRunWindow(model, null!);
        var root = WpfHost.Layout(window, WizardClientWidth, WizardClientHeight);
        var scroll = (ScrollViewer)window.FindName("StepScroll");

        var shownStep = ((Grid)scroll.Content).Children.OfType<StackPanel>().First(panel => panel.Visibility == Visibility.Visible);
        var parts = string.Join(", ", shownStep.Children.OfType<FrameworkElement>().Select(child => $"{child.GetType().Name}:{child.ActualHeight:0}"));
        Assert.True(scroll.ScrollableHeight <= 0.5,
            $"шаг {step} просит на {scroll.ScrollableHeight:0} точек больше, чем есть ({scroll.ViewportHeight:0}): {parts}");
    });

    [Theory]
    [InlineData(FirstRunStep.Welcome)]
    [InlineData(FirstRunStep.Appearance)]
    [InlineData(FirstRunStep.Finale)]
    public void Короткий_шаг_стоит_по_центру_а_не_у_верха(FirstRunStep step) => WpfHost.Run(() =>
    {
        var model = new FirstRunViewModel(new AppSettings(), new AdminAccessState());
        WpfHost.SetPrivate(model, nameof(FirstRunViewModel.Step), step);

        var window = new FirstRunWindow(model, null!);
        var root = WpfHost.Layout(window, WizardClientWidth, WizardClientHeight);
        var scroll = (ScrollViewer)window.FindName("StepScroll");

        var shown = ((Grid)scroll.Content).Children.OfType<StackPanel>()
            .First(panel => panel.Visibility == Visibility.Visible);
        var top = shown.TranslatePoint(default, scroll).Y;
        var bottom = scroll.ViewportHeight - top - shown.ActualHeight;

        Assert.InRange(Math.Abs(top - bottom), 0, 2);
    });

    [Fact]
    public void Предел_клавиш_не_трогается_когда_всё_влезает()
        => Assert.Null(PanelWindow.FitMacroHeight(content: 700, room: 900, rows: 6, zoom: 1, macroHeight: 60));

    [Fact]
    public void Двенадцать_клавиш_в_два_ряда_ужимаются_до_экрана()
    {
        // 0.1.58: шесть рядов по 60 при масштабе 1.2 — содержимое 1100 на
        // экране, где ему доступно 980.
        var limit = PanelWindow.FitMacroHeight(content: 1100, room: 980, rows: 6, zoom: 1.2, macroHeight: 60);

        Assert.NotNull(limit);

        // Шесть рядов отдают по (1100 - 980) / 1.2 / 6 ≈ 16,7 точки.
        Assert.Equal(43, limit.Value);
    }

    [Fact]
    public void Клавиша_не_становится_ниже_предела()
    {
        var limit = PanelWindow.FitMacroHeight(content: 3000, room: 600, rows: 10, zoom: 1, macroHeight: 60);

        Assert.Equal(PanelWindow.MinimumMacroHeight, limit);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(44)]
    [InlineData(34)]
    public void Подпись_клавиши_не_вылезает_за_клавишу(double limit) => WpfHost.Run(() =>
    {
        var model = new PanelViewModel { MacroColumns = 2, MacroHeight = 60, MacroHeightLimit = limit };
        foreach (var title in Titles)
        {
            model.Macros.Add(new MacroViewModel(title, "1"));
        }

        var window = new PanelWindow(model);
        var root = WpfHost.Layout(window, 254, double.PositiveInfinity);

        var boxes = Descendants(root).OfType<ShrinkBox>().Where(box => box.ActualHeight > 0).ToList();
        Assert.NotEmpty(boxes);

        foreach (var box in boxes)
        {
            var key = Ancestor<Button>(box);
            var text = (TextBlock)box.Child;
            Assert.True(text.DesiredSize.Height <= key.ActualHeight,
                $"«{text.Text}»: подпись {text.DesiredSize.Height:0} при клавише {key.ActualHeight:0}");
            Assert.True(key.ActualHeight <= limit + 0.5,
                $"«{text.Text}»: клавиша {key.ActualHeight:0} выше предела {limit}");
        }
    });

    private static (double Key, double Bottom, double Controls, double Font) MeasurePanel(double height, bool withMacros)
    {
        var model = new PanelViewModel { MacroColumns = 2, MacroHeight = 44 };
        if (withMacros)
        {
            foreach (var title in Titles)
            {
                model.Macros.Add(new MacroViewModel(title, "1"));
            }
        }

        var window = new PanelWindow(model);
        var root = (Grid)((Border)WpfHost.Layout(window, 300, height)).Child;
        var middle = root.Children.OfType<Grid>().Single(grid => Grid.GetRow(grid) == 2);
        var controls = middle.RowDefinitions[2].ActualHeight;
        var bottom = root.RowDefinitions[4].ActualHeight;

        var box = Descendants(root).OfType<ShrinkBox>().FirstOrDefault(item => item.ActualHeight > 0);
        if (box is null)
        {
            return (0, bottom, controls, 0);
        }

        var key = Ancestor<Button>(box);
        var text = (TextBlock)box.Child;
        Assert.True(text.DesiredSize.Height <= key.ActualHeight, $"подпись {text.DesiredSize.Height:0} при клавише {key.ActualHeight:0}");
        Assert.True(key.ActualHeight >= 44 - 0.5, $"клавиша срезана до {key.ActualHeight:0}");
        return (key.ActualHeight, bottom, controls, text.FontSize);
    }

    [Fact]
    public void Растянутое_окно_с_клавишами_растит_управление_на_половину_остальное_клавишам() => WpfHost.Run(() =>
    {
        var natural = MeasurePanel(double.PositiveInfinity, withMacros: true);
        var tall = MeasurePanel(1000, withMacros: true);

        Assert.Equal(68, natural.Controls, 0.5);
        Assert.Equal(102, tall.Controls, 0.5);
        Assert.True(tall.Key > natural.Key + 20, $"клавиша {natural.Key:0} → {tall.Key:0}");
        Assert.True(tall.Font > natural.Font + 4, $"кегль {natural.Font:0.#} → {tall.Font:0.#}");
        Assert.Equal(natural.Bottom, tall.Bottom, 0.5);
    });

    [Fact]
    public void Окно_минимальной_высоты_не_срезает_клавиши() => WpfHost.Run(() =>
    {
        // Высота ровно по содержимому, но заданная числом — как у окна,
        // которое растянули и сжали обратно до упора.
        var model = new PanelViewModel { MacroColumns = 2, MacroHeight = 44 };
        foreach (var title in Titles)
        {
            model.Macros.Add(new MacroViewModel(title, "1"));
        }

        var frame = (Border)WpfHost.Layout(new PanelWindow(model), 300, double.PositiveInfinity);
        var natural = frame.DesiredSize.Height;

        var tight = MeasurePanel(natural, withMacros: true);
        Assert.Equal(68, tight.Controls, 0.5);
    });

    [Fact]
    public void Без_клавиш_растягивание_целиком_уходит_кнопкам_управления() => WpfHost.Run(() =>
    {
        var natural = MeasurePanel(double.PositiveInfinity, withMacros: false);
        var tall = MeasurePanel(700, withMacros: false);

        Assert.True(tall.Controls > natural.Controls + 200, $"управление {natural.Controls:0} → {tall.Controls:0}");
        Assert.Equal(natural.Bottom, tall.Bottom, 0.5);
    });
    private static readonly string[] Titles =
    [
        "Квартиры 8+", "Квартиры 8+ ТОП", "Квартиры 15 +", "Квартиры 15 + ТОП",
        "Кв и дома 25 +", "Квартиры и дома 45+", "Участки СБ остальные",
        "Квартиры и дома 100+", "Вернуть клиента", "Кнопка для промаха",
    ];

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static T Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return (T)current;
    }
}
