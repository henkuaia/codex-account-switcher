using System.Windows.Input;

namespace CodexAccountSwitcher.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<object?, CancellationToken, Task> _executeAsync;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Func<Exception, Task>? _handleErrorAsync;
    private int _executionGate;

    public AsyncCommand(
        IUiDispatcher dispatcher,
        Func<object?, CancellationToken, Task> executeAsync,
        Func<object?, bool>? canExecute = null,
        Func<Exception, Task>? handleErrorAsync = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
        _handleErrorAsync = handleErrorAsync;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _executionGate) == 0 && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter)
    {
        _ = ExecuteAsync(parameter);
    }

    public async Task ExecuteAsync(
        object? parameter = null,
        CancellationToken cancellationToken = default)
    {
        if (!(_canExecute?.Invoke(parameter) ?? true) ||
            Interlocked.CompareExchange(ref _executionGate, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(NotifyCanExecuteChanged, cancellationToken);
            await _executeAsync(parameter, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await HandleErrorAsync(exception);
        }
        finally
        {
            try
            {
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        Volatile.Write(ref _executionGate, 0);
                        NotifyCanExecuteChanged();
                    },
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _executionGate, 0);
                await HandleErrorAsync(exception);
            }
        }
    }

    internal void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task HandleErrorAsync(Exception exception)
    {
        if (_handleErrorAsync is null)
        {
            return;
        }

        try
        {
            await _handleErrorAsync(exception);
        }
        catch
        {
        }
    }
}
