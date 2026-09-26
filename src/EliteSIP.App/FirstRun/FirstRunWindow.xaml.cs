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

    public FirstRunWindow(FirstRunViewModel model, AppearanceService appearance)
    {
        InitializeComponent();
        Model = model;
        _appearance = appearance;
        DataContext = model;
    }

    public FirstRunViewModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCaption.Apply(this, _appearance.IsDark);
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
        base.OnClosed(e);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Model.Back();

    private void OnNewCodeClick(object sender, RoutedEventArgs e) => Model.RequestNewCode();

    private void OnManualSetupClick(object sender, RoutedEventArgs e) => Model.UseManualSetup();

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
