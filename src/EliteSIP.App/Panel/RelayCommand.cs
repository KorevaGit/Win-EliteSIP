using System.Windows.Input;

namespace EliteSIP.App.Panel;

/// <summary>Действие панели: что делать и когда это можно.</summary>
///
/// <remarks>
/// Своё, а не из библиотеки: команд в приложении десяток, и тянуть ради них
/// зависимость значило бы отдать чужому пакету место, где решается, нажимается
/// ли «Завершить».
/// </remarks>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    /// <summary>Пересобрать разрешение. Зовётся, когда меняется состояние линии.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
