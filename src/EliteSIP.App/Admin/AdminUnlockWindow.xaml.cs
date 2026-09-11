using System.Windows;
using System.Windows.Input;
using EliteSIP.AdminAccess;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Admin;

/// <summary>Вход в административный режим.</summary>
///
/// <remarks>
/// Порядок проверок здесь и есть решение о том, кого пускать. Незащищённая
/// машина — законное состояние: пароль снимают руками, и тогда окно только
/// предупреждает. Защищённая просит пароль, и отказ у неё один на все случаи:
/// различать «не тот пароль» и «пароль не заведён» вслух незачем — подбирающему
/// это подсказка, а администратору всё равно.
/// </remarks>
public partial class AdminUnlockWindow : Window
{
    private readonly AdminAccessState _access;
    private readonly AppearanceService _appearance;

    public AdminUnlockWindow(AppSettings settings, AdminAccessState access, AppearanceService appearance)
    {
        InitializeComponent();
        _access = access;
        _appearance = appearance;

        if (settings.Admin.IsProtected)
        {
            PasswordField.Focus();
            return;
        }

        // Пароля нет — пускать некого спрашивать. Предупреждение всё равно
        // показывается: открытые настройки не делают правку менее
        // последствийной.
        PasswordStep.Visibility = Visibility.Collapsed;
        WarningText.Visibility = Visibility.Visible;
        EnterButton.IsEnabled = true;
        EnterButton.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCaption.Apply(this, _appearance.IsDark);
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        EnterButton.IsEnabled = PasswordField.Password.Length > 0;

        // Отказ прячется, когда человек начал набирать заново, — но не когда
        // поле очистили мы сами после отказа. Прежде `Clear()` здесь же и
        // прятал только что показанное «Неверный пароль»: окно выглядело
        // молча перезапущенным.
        if (PasswordField.Password.Length > 0)
        {
            ProblemRow.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPasswordFocus(object sender, KeyboardFocusChangedEventArgs e) => ShowCapsLock();

    private void OnPasswordKeyUp(object sender, KeyEventArgs e) => ShowCapsLock();

    private void ShowCapsLock()
        => CapsRow.Visibility = Keyboard.IsKeyToggled(Key.CapsLock)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        // Пароль набирают с клавиатуры, и тянуться мышью после него
        // противоестественно. IsDefault у кнопки этого не даёт: поле пароля
        // забирает Enter себе.
        if (e.Key is Key.Enter && EnterButton.IsEnabled)
        {
            OnEnterClick(sender, e);
        }
    }

    private void OnEnterClick(object sender, RoutedEventArgs e)
    {
        if (_access.IsUnlocked || !_access.IsProtected)
        {
            // Незащищённая машина: замок открыт с самого начала, и подтверждать
            // тут нечего, кроме прочитанного предупреждения.
            _access.Unlock(string.Empty);
            DialogResult = true;
            return;
        }

        // Отказ приходит исключением, а не ложью в возвращаемом значении:
        // `Unlock` возвращает `true` либо не возвращается вовсе.
        try
        {
            _access.Unlock(PasswordField.Password);
        }
        catch (AdminAccessException)
        {
            // Один отказ на все случаи — испорченные данные и неудачный вывод
            // ключа выглядят снаружи так же, как неверный пароль.
            // Сначала очистить, потом показать: очистка сама прячет строку
            // отказа (см. `OnPasswordChanged`).
            PasswordField.Clear();
            ProblemText.Text = Strings.Get("AdminUnlockWrongPassword");
            ProblemRow.Visibility = Visibility.Visible;
            PasswordField.Focus();
            ShowCapsLock();
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
