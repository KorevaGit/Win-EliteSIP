using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EliteSIP.App.Resources;
using EliteSIP.App.Theme;
using Microsoft.Win32;

namespace EliteSIP.App.Settings;

/// <summary>Настройки менеджера: то, что человек правит себе сам.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// Проигрыватель прослушивания рингтона. Один на окно: два нажатия подряд
    /// не должны давать два голоса поверх друг друга.
    /// </summary>
    private readonly MediaPlayer _preview = new();

    private readonly AppearanceService _appearance;

    public SettingsWindow(SettingsViewModel model, AppearanceService appearance)
    {
        InitializeComponent();
        Model = model;
        _appearance = appearance;
        DataContext = model;

        // Тема применяется сразу и на живых окнах: палитра — это словарь
        // ресурсов, и подменить его можно под открытой панелью. В оригинале
        // соседний с ней переключатель корпуса требовал перезапуска, потому что
        // стекло выбиралось при сборке окон, — здесь выбирать нечего.
        model.Settings.Appearance.PropertyChanged += OnAppearanceChanged;
    }

    public SettingsViewModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Полоса заголовка у этого окна системная, и по своей воле она красится
        // по теме Windows, а не по нашей.
        WindowCaption.Apply(this, _appearance.IsDark);
    }

    protected override void OnClosed(EventArgs e)
    {
        Model.Settings.Appearance.PropertyChanged -= OnAppearanceChanged;

        // Иначе рингтон продолжает звонить после того, как окно закрыли, и
        // остановить его нечем.
        _preview.Stop();
        _preview.Close();

        base.OnClosed(e);
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(AppearanceSettings.Theme))
        {
            _appearance.Appearance = Model.Settings.Appearance.Theme;
            WindowCaption.Apply(this, _appearance.IsDark);
        }
    }

    /// <summary>Выбор файла рингтона.</summary>
    private void OnChooseRingtoneClick(object sender, RoutedEventArgs e)
    {
        _preview.Stop();

        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("SectionRingtone"),
            Multiselect = false,

            // Тот же набор, что и в оригинале, минус форматы Apple: CAF и AIFF
            // на Windows не читает ни один системный проигрыватель, и предлагать
            // их значило бы обещать то, чего не будет.
            Filter = "WAV, MP3, WMA, M4A|*.wav;*.mp3;*.wma;*.m4a",
        };

        if (dialog.ShowDialog(this) is not true)
        {
            return;
        }

        Model.Settings.Ringtone.CustomSoundPath = dialog.FileName;
        Model.NotifyRingtoneName();
        ShowRingtoneProblem(null);
    }

    private void OnDefaultRingtoneClick(object sender, RoutedEventArgs e)
    {
        _preview.Stop();
        Model.Settings.Ringtone.CustomSoundPath = null;
        Model.NotifyRingtoneName();
        ShowRingtoneProblem(null);
    }

    /// <summary>Прослушивание — тем же файлом и той же громкостью, что и звонок.</summary>
    private void OnPreviewRingtoneClick(object sender, RoutedEventArgs e)
    {
        _preview.Stop();

        var path = Model.Settings.Ringtone.CustomSoundPath;
        if (string.IsNullOrEmpty(path))
        {
            // Стандартный звук приедет вместе со слоем приложения — он же
            // играет входящий. Своей копии у окна настроек быть не должно:
            // прослушивание обязано звучать ровно так же, как звонок.
            ShowRingtoneProblem(Strings.Get("RingtoneDefault"));
            return;
        }

        if (!File.Exists(path))
        {
            ShowRingtoneProblem(Model.RingtoneName);
            return;
        }

        _preview.Volume = Model.Settings.Ringtone.Volume;
        _preview.Open(new Uri(path));
        _preview.Play();
        ShowRingtoneProblem(null);
    }

    private void ShowRingtoneProblem(string? text)
    {
        RingtoneProblem.Text = text ?? string.Empty;
        RingtoneProblem.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Спрашивает про перезапуск и только потом меняет язык.
    /// </summary>
    ///
    /// <remarks>
    /// Вопрос задаётся до правки, а не после: согласие здесь дают не на язык, а
    /// на перезапуск со снятием регистрации, и узнавать о нём постфактум
    /// оператор не должен.
    ///
    /// Привязка у сегментов односторонняя именно поэтому: выбор в свойство
    /// кладёт не она, а этот обработчик — и только после согласия. Отказ
    /// возвращает сегменты к тому, что записано в настройках.
    /// </remarks>
    private void OnLanguageChosen(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { Tag: string name })
        {
            return;
        }

        var chosen = Enum.Parse<LanguageSetting>(name);
        if (chosen == Model.Settings.Appearance.Language)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            Strings.Get("AppearanceLanguageQuestionBody"),
            Strings.Get("AppearanceLanguageQuestion"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer is not MessageBoxResult.OK)
        {
            // Вернуть сегменты к записанному: отказ не должен оставлять
            // выбранным то, чего не произошло.
            Model.NotifyLanguage();
            return;
        }

        // Порядок важен: перезапуск без записи вернул бы приложение к прежнему
        // языку — и без правки, ради которой его затевали. Запись идёт тем же
        // движением, что и правка (`AutoSave`), то есть до перезапуска.
        Model.Settings.Appearance.Language = chosen;
        Restart();
    }

    /// <summary>Кто открывает «Управление». Ставит приложение.</summary>
    ///
    /// <remarks>
    /// Событием, а не вызовом отсюда: за дверью пароль и другое окно, и решать,
    /// пускать ли, — не дело окна настроек.
    /// </remarks>
    public event Action? AdministrationRequested;

    private void OnAdministrationClick(object sender, RoutedEventArgs e)
        => AdministrationRequested?.Invoke();

    /// <summary>Перезапуск: новый процесс поднимается, этот закрывается.</summary>
    ///
    /// <remarks>
    /// Порядок обратный тому, что кажется естественным, и он же был в
    /// оригинале: новый процесс должен застать настройки уже записанными, а
    /// закрывать текущий до его запуска нельзя — тогда закрывать будет некому.
    /// </remarks>
    private static void Restart()
    {
        var executable = Environment.ProcessPath;
        if (executable is not null)
        {
            System.Diagnostics.Process.Start(executable);
        }

        Application.Current.Shutdown();
    }
}
