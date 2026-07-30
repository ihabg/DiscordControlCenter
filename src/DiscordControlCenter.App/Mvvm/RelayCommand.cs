using System.Windows.Input;

namespace DiscordControlCenter.App.Mvvm;

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
