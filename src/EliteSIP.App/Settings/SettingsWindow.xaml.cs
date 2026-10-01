using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EliteSIP.App.Resources;
using EliteSIP.App.Theme;
using EliteSIP.Audio;
using Microsoft.Win32;

namespace EliteSIP.App.Settings;

/// <summary>Настройки менеджера: то, что человек правит себе сам.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// Проигрыватель прослушивания — тот же, что звонит на входящем. Своего
    /// <c>MediaPlayer</c> у окна больше нет: он не умел ни стандартный звук, ни
    /// OGG и звучал не на том устройстве, что звонок.
    /// </summary>
    private readonly SignalSoundPlayer _sounds;

    private readonly AppearanceService _appearance;

    public SettingsWindow(SettingsViewModel model, AppearanceService appearance, SignalSoundPlayer sounds)
    {
        InitializeComponent();
        _sounds = sounds;
        Model = model;
        _appearance = appearance;
        DataContext = model;

        // Тема применяется сразу и на живых окнах: палитра — это словарь
        // ресурсов, и подменить его можно под открытой панелью. В оригинале
        // соседний с ней переключатель корпуса требовал перезапуска, потому что
        // стекло выбиралось при сборке окон, — здесь выбирать нечего.
        model.Settings.Appearance.PropertyChanged += OnAppearanceChanged;

        // Шкалы звука опрашиваются, только пока окно открыто и виден раздел
        // «Звук»; закрытие окна отпускает и проверку, если она шла.
        Loaded += (_, _) => model.UpdateMeterPolling(windowOpen: true);
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
        Model.UpdateMeterPolling(windowOpen: false);

        // Иначе рингтон продолжает звонить после того, как окно закрыли, и
        // остановить его нечем.
        _sounds.StopPreview();

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
        _sounds.StopPreview();

        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("SectionRingtone"),
            Multiselect = false,

            // Тот же набор, что и в оригинале, минус форматы Apple: CAF и AIFF
            // на Windows не читает ни один системный проигрыватель, и предлагать
            // их значило бы обещать то, чего не будет.
            Filter = "MP3, WAV, OGG|*.mp3;*.wav;*.ogg",
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
        _sounds.StopPreview();
        Model.Settings.Ringtone.CustomSoundPath = null;
        Model.NotifyRingtoneName();
        ShowRingtoneProblem(null);
    }

    /// <summary>Прослушивание — тем же файлом и той же громкостью, что и звонок.</summary>
    private void OnPreviewRingtoneClick(object sender, RoutedEventArgs e)
    {
        _sounds.StopPreview();

        // До 0.1.68 при стандартном рингтоне здесь ничего не играло: окно
        // показывало надпись «Стандартный» вместо звука. Теперь играет тот же
        // проигрыватель, что и на входящем: тот же файл, громкость и устройство.
        var path = Model.Settings.Ringtone.CustomSoundPath;
        if (!string.IsNullOrEmpty(path) && !File.Exists(path))
        {
            // Звонок в этом случае зазвонит стандартным — его и даём услышать,
            // но причину говорим.
            ShowRingtoneProblem(Model.RingtoneName);
        }
        else
        {
            ShowRingtoneProblem(null);
        }

        var ringtone = Model.Settings.Ringtone;
        var readable = _sounds.PreviewRingtone(
            path,
            ringtone.Volume,
            ringtone.Output is RingtoneOutput.CallDevice ? Model.Settings.Audio.OutputDeviceId : null);

        if (!readable)
        {
            ShowRingtoneProblem(Strings.Get("RingtoneUnreadable"));
        }
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

        var answer = Theme.Dialog.Ask(
            this,
            Strings.Get("AppearanceLanguageQuestion"),
            Strings.Get("AppearanceLanguageQuestionBody"),
            confirmTitle: Strings.Get("AppearanceLanguageConfirm"));

        if (answer is not DialogAnswer.Confirm)
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
