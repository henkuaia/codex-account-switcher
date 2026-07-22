using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Views;

public partial class OperationWindow : Window
{
    private bool _canClose;

    public OperationWindow(string heading, string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        InitializeComponent();
        HeadingText.Text = heading;
        PhaseText.Text = phase;
    }

    public void AppendLine(ProcessOutputLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        OutputTextBox.AppendText(line.Text);
        OutputTextBox.AppendText(Environment.NewLine);
        OutputTextBox.ScrollToEnd();
    }

    public void Complete(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        PhaseText.Text = result.Succeeded
            ? "Completed"
            : $"Failed (exit code {result.ExitCode})";
        EnableClose();
    }

    public void Fail()
    {
        PhaseText.Text = "Operation failed";
        EnableClose();
    }

    private void EnableClose()
    {
        _canClose = true;
        HeaderCloseButton.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Visible;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_canClose)
        {
            e.Cancel = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}

internal static class DialogOperationRunner
{
    public static async Task<CommandResult> RunLoginAsync(
        Func<Task> showAsync,
        ProcessOutputHandler appendAsync,
        Func<CommandResult, Task> completeAsync,
        Func<Exception, Task> failAsync,
        Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(showAsync);
        ArgumentNullException.ThrowIfNull(appendAsync);
        ArgumentNullException.ThrowIfNull(completeAsync);
        ArgumentNullException.ThrowIfNull(failAsync);
        ArgumentNullException.ThrowIfNull(operation);

        await showAsync();
        try
        {
            var result = await operation(appendAsync, cancellationToken);
            await completeAsync(result);
            return result;
        }
        catch (Exception exception)
        {
            await failAsync(exception);
            throw;
        }
    }

    public static async Task<CommandResult> RunRemoveAsync(
        Func<Task> showAsync,
        Func<CommandResult, Task> completeAsync,
        Func<Exception, Task> failAsync,
        Func<CancellationToken, Task<CommandResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(showAsync);
        ArgumentNullException.ThrowIfNull(completeAsync);
        ArgumentNullException.ThrowIfNull(failAsync);
        ArgumentNullException.ThrowIfNull(operation);

        await showAsync();
        try
        {
            var result = await operation(cancellationToken);
            await completeAsync(result);
            return result;
        }
        catch (Exception exception)
        {
            await failAsync(exception);
            throw;
        }
    }
}
