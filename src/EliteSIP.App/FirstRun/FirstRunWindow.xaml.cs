using System.ComponentModel;
using System.Windows;
using EliteSIP.App.Resources;
using EliteSIP.App.Theme;

namespace EliteSIP.App.FirstRun;

/// <summary>Мастер первоначальной настройки.</summary>
///
/// <remarks>
/// Окно закрывается крестиком, и это не оплошность: человек вправе передумать
/// заводить машину. Но незавершённый мастер оставляет её ненастроенной, и
/// приложение в этом случае не поднимает ни панель, ни «Управление» — иначе
/// сброшенная машина осталась бы с пустыми настройками и открытым
/// «Управлением», ровно тем состоянием, ради лечения которого мастер и заведён.
/// </remarks>
public partial class FirstRunWindow : Window
{
    private readonly AppearanceService _appearance;
    private readonly Appearance _originalAppearance;

    public FirstRunWindow(FirstRunViewModel model, AppearanceService appearance)
    {
        InitializeComponent();
        Model = model;
        _appearance = appearance;
        _originalAppearance = appearance?.Appearance ?? Appearance.System;
        DataContext = model;
        CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => Close()));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) { Template = null; }
        else
        {
            WindowStyle = WindowStyle.None;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false,
            });
        }
        Loaded += (_, _) => Model.Begin();
        model.PropertyChanged += OnModelChanged;
    }

    public FirstRunViewModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_appearance is not null) { WindowCaption.Apply(this, _appearance.IsDark); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // Закрытие на полпути — это отказ, и спрашивается он один раз: мастер
        // короткий, и переспрашивать на каждом шаге значило бы держать человека
        // в окне, из которого он уже решил выйти.
        if (DialogResult is true || Model.Step is FirstRunStep.Welcome)
        {
            return;
        }

        var answer = Theme.Dialog.Ask(
            this,
            Strings.Get("FirstRunAbandonTitle"),
            Strings.Get("FirstRunAbandonBody"),
            confirmTitle: Strings.Get("FirstRunAbandonConfirm"));

        if (answer is not DialogAnswer.Confirm)
        {
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Длинный опрос Spark не должен пережить окно.
        Model.Dispose();
        Model.PropertyChanged -= OnModelChanged;
        if (DialogResult is not true && _appearance is not null) { _appearance.Appearance = _originalAppearance; }
        base.OnClosed(e);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Model.Back();

    private void OnNewCodeClick(object sender, RoutedEventArgs e) => Model.RequestNewCode();

    private void OnManualSetupClick(object sender, RoutedEventArgs e) => Model.UseManualSetup();

    private void OnSparkSetupClick(object sender, RoutedEventArgs e) => Model.UseSparkSetup();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FirstRunViewModel.Theme) && _appearance is not null)
        {
            _appearance.Appearance = Model.Theme;
            if (PresentationSource.FromVisual(this) is not null) { WindowCaption.Apply(this, _appearance.IsDark); }
        }
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (!Model.IsLastStep)
        {
            Model.Forward();
            return;
        }

        Model.Complete();
        DialogResult = true;
    }

    // Пароли не привязываются: привязка означала бы пароль в памяти дерева
    // элементов, а не только в поле.
    private void OnSipPasswordChanged(object sender, RoutedEventArgs e)
        => Model.SipPassword = SipPasswordField.Password;

    private void OnAdminPasswordChanged(object sender, RoutedEventArgs e)
        => Model.AdminPassword = AdminPasswordField.Password;

    private void OnRepeatedAdminPasswordChanged(object sender, RoutedEventArgs e)
        => Model.RepeatedAdminPassword = RepeatedAdminPasswordField.Password;
}
