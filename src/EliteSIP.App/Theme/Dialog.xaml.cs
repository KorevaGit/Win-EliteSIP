using System.Windows;
using System.Windows.Controls;
using EliteSIP.App.Resources;

namespace EliteSIP.App.Theme;

/// <summary>Чем ответил человек на вопрос приложения.</summary>
///
/// <remarks>
/// Три значения, а не <c>bool?</c>: у вопроса «сохранить перед закрытием?»
/// ответов ровно три, и третий — «не закрывай вовсе» — не середина между
/// первыми двумя.
/// </remarks>
public enum DialogAnswer
{
    /// <summary>Подтвердил: сохранить, сменить, снять.</summary>
    Confirm,

    /// <summary>Отказался от правок, но действие продолжает.</summary>
    Discard,

    /// <summary>Передумал вовсе. Даёт и крестик окна, и Escape.</summary>
    Cancel,
}

/// <summary>Набор кнопок под вопросом.</summary>
public enum DialogButtons
{
    /// <summary>Подтвердить или отменить.</summary>
    ConfirmCancel,

    /// <summary>Сохранить, не сохранять или вернуться.</summary>
    SaveDiscardCancel,
}

/// <summary>Вопрос приложения, заданный окном приложения.</summary>
///
/// <remarks>
/// Зачем своё окно вместо <c>MessageBox</c> — сказано в разметке.
///
/// Кнопки собираются кодом, а не разметкой: наборов два, и в разметке они
/// стали бы двумя стопками кнопок, из которых одна всегда спрятана. Спрятанная
/// половина разметки — то место, где рано или поздно расходятся отступы.
/// </remarks>
public partial class Dialog : Window
{
    private DialogAnswer _answer = DialogAnswer.Cancel;

    private Dialog()
    {
        InitializeComponent();
    }

    /// <summary>Задаёт вопрос и ждёт ответа.</summary>
    ///
    /// <param name="owner">Окно, поверх которого встаёт вопрос.</param>
    /// <param name="title">Сам вопрос. Коротко и вопросительно.</param>
    /// <param name="body">Что случится после ответа.</param>
    /// <param name="buttons">Какой набор кнопок нужен.</param>
    public static DialogAnswer Ask(
        Window? owner,
        string title,
        string body,
        DialogButtons buttons = DialogButtons.ConfirmCancel,
        string? confirmTitle = null)
    {
        var dialog = new Dialog
        {
            // Заголовок окна — тот же вопрос: полоса заголовка теперь
            // системная, и в панели задач окно подписано именно им.
            Title = title,
        };

        dialog.TitleText.Text = title;
        dialog.BodyText.Text = body;

        // Владелец бывает и закрытым — например, вопрос задаёт окно, которое
        // само закрывается. Ставить закрытое окно владельцем WPF не даёт.
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (buttons is DialogButtons.SaveDiscardCancel)
        {
            dialog.AddButton(Strings.Get("DialogSave"), DialogAnswer.Confirm, primary: true);
            dialog.AddButton(Strings.Get("DialogDiscard"), DialogAnswer.Discard, primary: false);
        }
        else
        {
            dialog.AddButton(confirmTitle ?? Strings.Get("DialogConfirm"), DialogAnswer.Confirm, primary: true);
        }

        dialog.AddButton(Strings.Get("DialogCancel"), DialogAnswer.Cancel, primary: false);

        dialog.ShowDialog();
        return dialog._answer;
    }

    private void AddButton(string title, DialogAnswer answer, bool primary)
    {
        var button = new Button
        {
            Content = title,
            Height = 32,
            MinWidth = 96,
            Margin = new Thickness(8, 0, 0, 0),

            // Подтверждающая кнопка залита цветом действия, прочие — обычная
            // поверхность. Цветом залита ровно одна: две залитые означали бы,
            // что выбор из них равнозначен, а он не равнозначен никогда.
            Style = (Style)FindResource(primary ? "AccentButtonStyle" : "SurfaceButtonStyle"),
            Padding = new Thickness(12, 0, 12, 0),

            // Enter отвечает подтверждением, Escape — отменой: так отвечают на
            // вопрос, не трогая мышь, и так же ведёт себя системное окно.
            IsDefault = primary,
            IsCancel = answer is DialogAnswer.Cancel,
        };

        button.Click += (_, _) =>
        {
            _answer = answer;
            Close();
        };

        Buttons.Children.Add(button);
    }
}
