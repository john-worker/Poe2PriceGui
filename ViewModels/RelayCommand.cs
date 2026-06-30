using System.Windows.Input;

namespace Poe2PriceGui.ViewModels;

/// <summary>
/// 简单的命令实现，用于绑定按钮等交互控件。
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private readonly bool _isAsync;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _executeAsync = () =>
        {
            execute();
            return Task.CompletedTask;
        };
        _canExecute = canExecute;
        _isAsync = false;
    }

    public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
        _isAsync = true;
    }

    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (_isAsync)
        {
            await _executeAsync();
        }
        else
        {
            _executeAsync().Wait();
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}
