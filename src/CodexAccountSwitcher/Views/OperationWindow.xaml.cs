using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using CodexAccountSwitcher.Security;
using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.ViewModels;

namespace CodexAccountSwitcher.Views;

public partial class OperationWindow : Window
{
    private readonly OperationWindowText _text;
    private bool _canClose;
    private bool _hasStreamedOutput;
    private readonly TaskCompletionSource _firstRender =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OperationWindow(string heading, string phase)
        : this(OperationWindowText.English(heading, phase))
    {
    }

    internal OperationWindow(OperationWindowText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        InitializeComponent();
        Title = text.Heading;
        HeadingText.Text = text.Heading;
        PhaseText.Text = text.Phase;
        CloseButton.Content = text.Close;
        HeaderCloseButton.ToolTip = text.Close;
        AutomationProperties.SetName(HeaderCloseButton, text.Close);
        ContentRendered += OnContentRendered;
    }

    public async Task ShowAndWaitForFirstRenderAsync(CancellationToken cancellationToken)
    {
        if (!IsVisible)
        {
            Show();
        }

        await _firstRender.Task.WaitAsync(cancellationToken);
    }

    public void AppendLine(ProcessOutputLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        OutputTextBox.AppendText(line.Text);
        OutputTextBox.AppendText(Environment.NewLine);
        OutputTextBox.ScrollToEnd();
        _hasStreamedOutput = true;
    }

    public void Complete(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        PhaseText.Text = result.Succeeded
            ? _text.Completed
            : string.Format(CultureInfo.InvariantCulture, _text.Failed, result.ExitCode);
        if (!result.Succeeded && !_hasStreamedOutput)
        {
            AppendSanitizedFailure(result);
        }

        EnableClose();
    }

    public void Fail()
    {
        PhaseText.Text = _text.OperationFailed;
        EnableClose();
    }

    private void EnableClose()
    {
        _canClose = true;
        HeaderCloseButton.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Visible;
    }

    private void AppendSanitizedFailure(CommandResult result)
    {
        var failureText = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        if (string.IsNullOrWhiteSpace(failureText))
        {
            return;
        }

        var sanitized = SensitiveTextRedactor.Redact(failureText, Array.Empty<string>());
        OutputTextBox.AppendText(sanitized.TrimEnd());
        OutputTextBox.AppendText(Environment.NewLine);
        OutputTextBox.ScrollToEnd();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        _firstRender.TrySetResult();
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

internal sealed record OperationWindowText(
    string Heading,
    string Phase,
    string Completed,
    string Failed,
    string OperationFailed,
    string Close)
{
    public static OperationWindowText AddAccount { get; } = new(
        "添加账号",
        "等待设备登录",
        "已完成",
        "失败（退出代码 {0}）",
        "操作失败",
        "关闭");

    public static OperationWindowText English(string heading, string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        return new OperationWindowText(
            heading,
            phase,
            "Completed",
            "Failed (exit code {0})",
            "Operation failed",
            "Close");
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

        try
        {
            await showAsync();
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

        try
        {
            await showAsync();
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

internal sealed class ActiveOperationTracker : IOperationActivityTracker
{
    private int _activeCount;

    public bool IsActive => Volatile.Read(ref _activeCount) != 0;

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _activeCount);
        return new Activity(this);
    }

    private void End() => Interlocked.Decrement(ref _activeCount);

    private sealed class Activity(ActiveOperationTracker owner) : IDisposable
    {
        private ActiveOperationTracker? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.End();
        }
    }
}
