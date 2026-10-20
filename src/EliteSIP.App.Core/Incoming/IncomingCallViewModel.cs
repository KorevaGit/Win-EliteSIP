using System.Windows.Input;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.CallGuard;

namespace EliteSIP.App.Incoming;

/// <summary>Содержимое окна входящего вызова.</summary>
///
/// <remarks>
/// Два состояния из макета, и переключает их одна настройка. Обычное: зелёная
/// «Ответить» и красная «Отклонить» рядом. С подтверждением цифрой: ряд
/// нейтральных цифровых целей, а «Отклонить» уезжает под них отдельной строкой.
///
/// Цели именно нейтральные, а не зелёные, и это часть защиты, а не вкусовщина:
/// кликер по шаблону изображения ищет на экране цветное пятно кнопки. Когда все
/// цели выглядят одинаково, искать нечего.
///
/// Порядок «ответить / отклонить» при этом не перемешивается: у этих действий
/// разные последствия, и провоцировать оператора на случайный отказ от лида
/// недопустимо.
/// </remarks>
public sealed class IncomingCallViewModel : Observable
{
    private string? _refusal;

    public IncomingCallViewModel(
        IncomingCallSubject subject,
        CallGuardChallenge challenge,
        bool isGuarded,
        Action<char> attempt,
        Action decline)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(challenge);

        Subject = subject;
        Targets = [.. challenge.Targets.Select(target => target.ToString())];
        Answer = challenge.Answer.ToString();
        HasChoice = challenge.HasChoice;
        IsGuarded = isGuarded;

        AttemptCommand = new RelayCommand(parameter =>
        {
            if (parameter is string digit && digit.Length == 1)
            {
                attempt(digit[0]);
            }
        });

        DeclineCommand = new RelayCommand(_ => decline());
    }

    public IncomingCallSubject Subject { get; }

    /// <summary>Что стоит на главном месте окна.</summary>
    public string Headline => Subject.Headline;

    /// <summary>Номер мелкой строкой; <c>null</c> — строки нет вовсе.</summary>
    public string? SecondaryNumber => Subject.SecondaryNumber;

    /// <summary>Показывать ли подсказку про звонок по сделке.</summary>
    ///
    /// <remarks>
    /// Только в этом случае, и ровно поэтому она работает: строка, которая стоит
    /// над кнопками всегда, к третьей сотне вызовов за смену не читается вовсе.
    /// </remarks>
    public bool IsSelfCall => Subject.Kind is IncomingCallKind.SelfCall;

    /// <summary>Цифровые цели. Одна — значит выбора нет.</summary>
    public IReadOnlyList<string> Targets { get; }

    /// <summary>Та единственная, которая принимает вызов.</summary>
    public string Answer { get; }

    public bool HasChoice { get; }

    /// <summary>
    /// Щит в шапке — честный индикатор того, что защита работает. Когда её
    /// выключили, значка нет, и это видно на снимке экрана.
    /// </summary>
    public bool IsGuarded { get; }

    /// <summary>Что сказать оператору вместо подписи «Входящий вызов».</summary>
    ///
    /// <remarks>
    /// Одно место на оба сообщения и в обоих состояниях окна: отдельная строка
    /// под отказ дёргала бы высоту окна прямо под рукой оператора, а подпись в
    /// этот момент всё равно ничего не сообщает — что вызов входящий, оператор
    /// уже понял.
    /// </remarks>
    public string? Refusal
    {
        get => _refusal;
        set
        {
            Set(ref _refusal, value);
            NotifyChanged(nameof(Caption));
            NotifyChanged(nameof(IsRefused));
        }
    }

    public string Caption => _refusal ?? Strings.Get("IncomingCaption");

    public bool IsRefused => _refusal is not null;

    public ICommand AttemptCommand { get; }

    public ICommand DeclineCommand { get; }
}
