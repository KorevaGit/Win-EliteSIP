using System.Reflection;
using System.Windows;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

/// <summary>
/// Приложение WPF для проверок разметки: ресурсы и палитра как у настоящего.
/// </summary>
///
/// <remarks>
/// Окна меряются без показа на экране — содержимое снимается с окна и
/// раскладывается в заданном размере клиентской области. Показ окна в
/// проверках зависел бы от монитора машины, на которой они идут.
///
/// Всё — в одном потоке STA на весь прогон: WPF держит приложение одно на
/// процесс и привязано оно к потоку, который его создал.
/// </remarks>
internal static class WpfHost
{
    private static readonly string[] Dictionaries = ["Theme/Icons.xaml", "Theme/Controls.xaml", "Theme/SettingsKit.xaml", "History/History.xaml"];

    private static readonly Lazy<(Thread Thread, System.Windows.Threading.Dispatcher Dispatcher)> Host = new(Start);

    public static void Run(Action action)
    {
        Exception? failure = null;
        Host.Value.Dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public static void UseTheme(Appearance appearance) => Palette.Appearance = appearance;

    private static AppearanceService Palette { get; set; } = null!;

    private static (Thread, System.Windows.Threading.Dispatcher) Start()
    {
        System.Windows.Threading.Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            // Относительные адреса ресурсов (`Theme/Controls.xaml`) WPF ищет в
            // сборке приложения, а приложение здесь — хост проверок.
            // Свойство ставится один раз, и хост проверок успевает поставить
            // его сам, — поэтому через поле.
            typeof(Application)
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(Assembly))
                .SetValue(null, typeof(App).Assembly);

            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in Dictionaries)
            {
                application.Resources.MergedDictionaries.Add(
                    new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
            }

            Palette = new AppearanceService(application);
            Palette.Apply();

            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return (thread, dispatcher!);
    }

    /// <summary>Раскладывает содержимое окна в клиентской области заданного размера.</summary>
    public static FrameworkElement Layout(Window window, double width, double height)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;

        // Ресурсы и шрифт окна содержимое наследовало от него — без окна
        // берутся те же, что окно задавало.
        if (window.Resources.Count > 0 || window.Resources.MergedDictionaries.Count > 0)
        {
            root.Resources.MergedDictionaries.Add(window.Resources);
        }
        foreach (var property in new[] { Window.FontFamilyProperty, Window.FontSizeProperty, Window.ForegroundProperty })
        {
            root.SetValue(property, window.GetValue(property));
        }

        foreach (var attached in window.GetLocalValueEnumerator().ToEnumerable())
        {
            if (attached.Property.OwnerType == typeof(SettingsLayout))
            {
                root.SetValue(attached.Property, attached.Value);
            }
        }

        root.DataContext = window.DataContext;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, double.IsInfinity(height) ? root.DesiredSize.Height : height));
        root.UpdateLayout();
        return root;
    }

    public static void SetPrivate(object target, string property, object value)
        => target.GetType()
                 .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                 .SetValue(target, value);

    private static IEnumerable<LocalValueEntry> ToEnumerable(this LocalValueEnumerator enumerator)
    {
        while (enumerator.MoveNext())
        {
            yield return enumerator.Current;
        }
    }
}
