using System.Windows.Input;

namespace BilinguaFlow.App.ViewModels;

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private int _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => Volatile.Read(ref _running) == 0 && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter) || Interlocked.Exchange(ref _running, 1) == 1) return;
        RaiseCanExecuteChanged();
        try { await execute(); }
        finally { Interlocked.Exchange(ref _running, 0); RaiseCanExecuteChanged(); }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
