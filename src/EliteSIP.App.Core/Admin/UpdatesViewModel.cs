using EliteSIP.App.Panel;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Resources;

namespace EliteSIP.App.Admin;

/// <summary>
/// Обновления в «Диагностике»: версия, проверка по кнопке и установка.
/// </summary>
///
/// <remarks>
/// Кнопка «Проверить сейчас» существует по той же причине, что и у
/// предустановок: такт двухчасовой, а разбирают неисправность здесь и сейчас.
/// Без ответа она молчала бы, и нажавший не знал бы, случилось ли что-нибудь
/// вообще.
///
/// Действует сразу, а не по «Сохранить», — как и раздел «Поддержка»: это не
/// правка машины, а обращение к каналу.
/// </remarks>
public sealed class UpdatesViewModel : Observable
{
    private readonly UpdateService _updates;

    // Конструктор внутренний, а класс открытый: наружу этот тип нужен только
    // привязке разметки, которая внутренних свойств не видит, а создаёт его
    // одно приложение.
    internal UpdatesViewModel(UpdateService updates)
    {
        _updates = updates;

        CheckNow = new RelayCommand(async _ => await CheckAsync(), _ => !_updates.IsChecking);
        Install = new RelayCommand(_ => _updates.Offer(), _ => _updates.ReadyVersion is not null);

        // Ход проверки — по мере того как он идёт: «проверяем», «скачиваем
        // 23 из 54 МБ», итог. В том числе у проверки, начатой не кнопкой, а
        // тактом: окно, открытое посреди фоновой закачки, видит её сразу.
        _updates.StatusChanged += () =>
        {
            Status = _updates.LastResult;
            Refresh();
        };

        Status = _updates.LastResult;
    }

    public RelayCommand CheckNow { get; }

    public RelayCommand Install { get; }

    /// <summary>Установленная версия — та, из которой это окно и открыли.</summary>
    public string Installed => _updates.Installed.ToString(3);

    /// <summary>Чем кончилась последняя проверка. <c>null</c> — ещё не проверяли.</summary>
    public string? Status { get; private set; }

    public bool HasStatus => Status is not null;

    /// <summary>Есть ли что ставить прямо сейчас.</summary>
    public bool IsReady => _updates.ReadyVersion is not null;

    private async Task CheckAsync()
    {
        await _updates.CheckNowAsync().ConfigureAwait(true);

        Status = _updates.LastResult;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var name in new[] { nameof(Status), nameof(HasStatus), nameof(IsReady) })
        {
            NotifyChanged(name);
        }

        CheckNow.RaiseCanExecuteChanged();
        Install.RaiseCanExecuteChanged();
    }
}
