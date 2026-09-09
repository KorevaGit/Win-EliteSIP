using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Admin;

/// <summary>Окно «Управление»: настройки рабочего места за паролем.</summary>
public partial class AdministrationWindow : Window
{
    private readonly AppearanceService _appearance;

    public AdministrationWindow(AdministrationViewModel model, AppearanceService appearance)
    {
        InitializeComponent();
        Model = model;
        _appearance = appearance;
        DataContext = model;
    }

    public AdministrationViewModel Model { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCaption.Apply(this, _appearance.IsDark);
    }

    /// <summary>Закрытие с несохранённым спрашивает, а не выбирает за человека.</summary>
    ///
    /// <remarks>
    /// Молча применить — значит применить то, чего не подтверждали; молча
    /// выбросить — значит потерять работу, которую делали. Спрашивается ровно
    /// один раз и только когда есть о чём.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (!Model.IsDirty)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            Strings.Get("AdminCloseDirtyBody"),
            Strings.Get("AdminCloseDirtyTitle"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                Model.Save();
                break;

            case MessageBoxResult.No:
                Model.Revert();
                break;

            default:
                e.Cancel = true;
                break;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Model.Save();

    private void OnRevertClick(object sender, RoutedEventArgs e) => Model.Revert();

    private void OnMacroAddClick(object sender, RoutedEventArgs e) => Model.AddMacro();

    private void OnMacroRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Macro(sender) is { } macro)
        {
            Model.RemoveMacro(macro);
        }
    }

    private void OnMacroUpClick(object sender, RoutedEventArgs e)
    {
        if (Macro(sender) is { } macro)
        {
            Model.MoveMacro(macro, offset: -1);
        }
    }

    private void OnMacroDownClick(object sender, RoutedEventArgs e)
    {
        if (Macro(sender) is { } macro)
        {
            Model.MoveMacro(macro, offset: 1);
        }
    }

    private static MacroSetting? Macro(object sender)
        => (sender as FrameworkElement)?.DataContext as MacroSetting;

    private void OnSipPasswordChanged(object sender, RoutedEventArgs e)
        => Model.SipPassword = SipPasswordField.Password;

    private void OnQueueAddClick(object sender, RoutedEventArgs e) => Model.AddQueue();

    private void OnQueueRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueueSetting queue)
        {
            Model.RemoveQueue(queue);
        }
    }

    private void OnNewPasswordChanged(object sender, RoutedEventArgs e)
        => Model.NewPassword = NewPasswordField.Password;

    private void OnRepeatedPasswordChanged(object sender, RoutedEventArgs e)
        => Model.RepeatedPassword = RepeatedPasswordField.Password;

    private void OnSetPasswordClick(object sender, RoutedEventArgs e)
    {
        Model.SetPassword();

        // Поля очищаются и здесь: модель их обнулила, но `PasswordBox` не
        // привязывается — пароль в привязке был бы паролем в памяти дерева
        // элементов, а не только в поле.
        NewPasswordField.Clear();
        RepeatedPasswordField.Clear();
    }

    private void OnRemovePasswordClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            Strings.Get("AdminRemovePasswordBody"),
            Strings.Get("AdminRemovePasswordTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer is MessageBoxResult.OK)
        {
            Model.RemovePassword();
        }
    }

    /// <summary>Показывает папку с настройками и журналом.</summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(AdministrationViewModel.SettingsPath);
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        // Проводник, а не открытие файла: настроек в папке несколько, и
        // поддержке нужны они все вместе с журналом.
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }
}
