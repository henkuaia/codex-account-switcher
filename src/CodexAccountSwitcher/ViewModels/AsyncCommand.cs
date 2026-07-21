using System.Windows.Input;

namespace CodexAccountSwitcher.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, CancellationToken, Task> _executeAsync;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Func<Exception, Task>? _handleErrorAsync;
    private bool _isExecuting;

    public AsyncCommand(
        Func<object?, CancellationToken, Task> executeAsync,
        Func<object?, bool>? canExecute = null,
        Func<Exception, Task>? handleErrorAsync = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
        _handleErrorAsync = handleErrorAsync;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter)
    {
        _ = ExecuteAsync(parameter);
    }

    public async Task ExecuteAsync(
        object? parameter = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _executeAsync(parameter, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (_handleErrorAsync is not null)
            {
                try
                {
                    await _handleErrorAsync(exception);
                }
                catch
                {
                }
            }
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
