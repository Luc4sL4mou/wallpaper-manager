using System.Windows.Input;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Liga um botão (Command="{Binding ...}") a um método do ViewModel, sem código no code-behind.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    // O WPF reavalia CanExecute sozinho (cliques, foco, teclas) via CommandManager
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>
    /// Pede ao WPF para reavaliar CanExecute agora (útil ao fim de uma operação assíncrona).
    /// </summary>
    public static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
