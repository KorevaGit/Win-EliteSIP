using System.Windows;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.App.Theme;

namespace EliteSIP.App;

// IDisposable у приложения — не церемония: `AppearanceService` подписан на
// статическое событие Windows о смене темы, и неотписанная подписка живёт
// дольше процесса, стреляя по закрытым окнам.
public partial class App : Application, IDisposable
{
    private AppearanceService? _appearance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Порядок важен: язык и палитра выбираются до первого окна. Иначе
        // панель успевает нарисоваться английской и светлой и перекрашивается
        // на глазах — именно то мигание, ради которого убран `StartupUri`.
        //
        // Выбора оператора пока нет: настройки приезжают со своим шагом этапа,
        // и до тех пор и язык, и оформление берутся у системы.
        Strings.Apply(chosen: null);

        _appearance = new AppearanceService(this);
        _appearance.Apply();

        // Слоя приложения ещё нет: панель поднимается со своим состоянием, а
        // звонить ей пока нечем. Настоящая модель подпишется на те же команды.
        var model = new PanelViewModel();
        PanelDemo.Apply(model, e.Args);

        new PanelWindow(model).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        _appearance?.Dispose();
        _appearance = null;
        GC.SuppressFinalize(this);
    }
}
