using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    /// <param name="footnote">
    /// Приписка мелким под вопросом. <c>null</c> — её нет.
    /// </param>
    /// <param name="countdownSeconds">
    /// Сколько секунд подтверждающая кнопка погашена. Ноль — не гасить.
    ///
    /// Заведено ради сброса машины и только ради него: отсчёт стоит там, где
    /// цена нажатия — стёртое рабочее место, и разница между «прочитал» и
    /// «промахнулся мышью» должна успеть проявиться.
    /// </param>
    /// <param name="destructive">
    /// Красит подтверждающую кнопку цветом отказа. Заливкой она при этом не
    /// становится: залитая красная кнопка читается как то, чего от человека
    /// ждут, а от него не ждут стирания машины.
    /// </param>
    public static DialogAnswer Ask(
        Window? owner,
        string title,
        string body,
        DialogButtons buttons = DialogButtons.ConfirmCancel,
        string? confirmTitle = null,
        string? footnote = null,
        int countdownSeconds = 0,
        bool destructive = false)
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

        if (footnote is { Length: > 0 })
        {
            dialog.FootnoteText.Text = footnote;
            dialog.FootnoteText.Visibility = Visibility.Visible;
        }

        Button confirm;
        if (buttons is DialogButtons.SaveDiscardCancel)
        {
            confirm = dialog.AddButton(Strings.Get("DialogSave"), DialogAnswer.Confirm, primary: true);
            dialog.AddButton(Strings.Get("DialogDiscard"), DialogAnswer.Discard, primary: false);
        }
        else
        {
            confirm = dialog.AddButton(
                confirmTitle ?? Strings.Get("DialogConfirm"), DialogAnswer.Confirm, primary: true, destructive);
        }

        dialog.AddButton(Strings.Get("DialogCancel"), DialogAnswer.Cancel, primary: false);

        if (countdownSeconds > 0)
        {
            dialog.CountDown(confirm, confirmTitle ?? Strings.Get("DialogConfirm"), countdownSeconds);
        }

        dialog.ShowDialog();
        return dialog._answer;
    }

    /// <summary>Гасит кнопку и отпускает её через <paramref name="seconds"/>.</summary>
    ///
    /// <remarks>
    /// Оставшееся написано на самой кнопке, а не рядом: человек в этот момент
    /// смотрит на неё, и счётчик в стороне он прочтёт уже после того, как
    /// поймёт, что кнопка не нажимается.
    ///
    /// Кнопка перестаёт быть кнопкой по умолчанию на время отсчёта: иначе Enter
    /// подтвердил бы вопрос, которого человек ещё не дочитал, — то есть ровно
    /// то, ради чего отсчёт и заведён.
    /// </remarks>
    private void CountDown(Button confirm, string title, int seconds)
    {
        var remaining = seconds;
        confirm.IsEnabled = false;
        confirm.IsDefault = false;
        confirm.Content = $"{title} ({remaining})";

        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                confirm.Content = $"{title} ({remaining})";
                return;
            }

            timer.Stop();
            confirm.Content = title;
            confirm.IsEnabled = true;
        };

        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private Button AddButton(string title, DialogAnswer answer, bool primary, bool destructive = false)
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
            Style = (Style)FindResource(primary && !destructive ? "AccentButtonStyle" : "SurfaceButtonStyle"),
            Padding = new Thickness(12, 0, 12, 0),

            // Enter отвечает подтверждением, Escape — отменой: так отвечают на
            // вопрос, не трогая мышь, и так же ведёт себя системное окно.
            IsDefault = primary,
            IsCancel = answer is DialogAnswer.Cancel,
        };

        if (destructive)
        {
            button.Foreground = (System.Windows.Media.Brush)FindResource("StatusFailureBrush");
        }

        button.Click += (_, _) =>
        {
            _answer = answer;
            Close();
        };

        Buttons.Children.Add(button);

        return button;
    }
}
