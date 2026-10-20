namespace EliteSIP.App.Platform;

/// <summary>
/// Окно входящего вызова — то, что от него нужно телефону.
/// </summary>
///
/// <remarks>
/// Само окно платформенное до последней строки: поверх всех окон без захвата
/// фокуса, позиция в пикселях экрана, слежение за мышью низкоуровневым хуком
/// на Windows и XInput2 на Linux. Телефону же нужно три вещи: показать вызов,
/// убрать его и узнать, на экране ли он.
/// </remarks>
public interface IIncomingCallView
{
    public bool IsVisible { get; }

    /// <summary>Показать вызов и взять его под защиту от автокликера.</summary>
    public void Show(
        Incoming.IncomingCallSubject subject,
        CallGuard.CallGuardPolicy policy,
        Action onAnswer,
        Action onDecline);

    public void Hide();
}

/// <summary>
/// Передача скачанного выпуска обновляльщику.
/// </summary>
///
/// <remarks>
/// На Windows — отметка в <c>%ProgramData%\EliteSIP</c> и задача планировщика
/// от SYSTEM (<c>W12-UPDATES.md</c>); на Linux — тот же каталог обмена в
/// <c>/var/lib/elitesip</c> и служба systemd от root. Проверка подписи,
/// закачка, отсрочки и поведение линии — общие и живут в <c>UpdateService</c>.
/// </remarks>
public interface IUpdateHandoff
{
    /// <summary>Каталог обмена: сюда приложение кладёт скачанный выпуск.</summary>
    public string UpdatesDirectory { get; }

    /// <summary>Оставить согласие на установку этой версии. <c>false</c> — не вышло.</summary>
    public bool Request(Version version, Action<string> log);

    /// <summary>Разбудить обновляльщика сейчас, не дожидаясь его такта. <c>false</c> — не вышло.</summary>
    public bool Trigger(Action<string> log);
}

/// <summary>Автозапуск при входе в систему — переключатель в «Обслуживании».</summary>
public interface IAutoStart
{
    public bool IsEnabled { get; }

    /// <summary>Включить или выключить. <c>false</c> — система не дала.</summary>
    public bool TrySet(bool enabled);
}
