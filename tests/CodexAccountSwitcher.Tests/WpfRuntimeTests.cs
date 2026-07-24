using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.ViewModels;
using CodexAccountSwitcher.Views;

namespace CodexAccountSwitcher.Tests;

public sealed class WpfRuntimeTests
{
    [Fact]
    public async Task Sta_timeout_shuts_down_and_joins_dispatcher_thread()
    {
        Thread? staThread = null;
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => RunOnStaThreadAsync(
            () => neverCompletes.Task,
            TimeSpan.FromMilliseconds(250),
            thread => staThread = thread));

        Assert.NotNull(staThread);
        Assert.False(staThread.IsAlive);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public Task Concrete_windows_render_bind_and_enforce_accessible_close_contracts() =>
        RunOnStaThreadAsync(async () =>
        {
            var app = new App();
            app.InitializeComponent();
            MainWindow? mainWindow = null;
            OperationWindow? operationWindow = null;
            OperationWindow? preflightWindow = null;
            OperationWindow? localizedWindow = null;
            OperationWindow? dialogLoginWindow = null;
            OperationWindow? canceledLoginWindow = null;
            OperationWindow? dialogRemoveWindow = null;
            SwitchConfirmationWindow? confirmationWindow = null;
            try
            {
                var expectedColors = new Dictionary<string, Color>
                {
                    ["WindowBackgroundColor"] = Color.FromRgb(0xF4, 0xF6, 0xF7),
                    ["SurfaceColor"] = Color.FromRgb(0xFB, 0xFC, 0xFC),
                    ["BorderColor"] = Color.FromRgb(0xD9, 0xDE, 0xE2),
                    ["PrimaryColor"] = Color.FromRgb(0x2D, 0x66, 0x78),
                    ["ActiveBackgroundColor"] = Color.FromRgb(0xED, 0xF5, 0xF1),
                    ["ActiveBorderColor"] = Color.FromRgb(0xC9, 0xDE, 0xD3),
                    ["ActiveTextColor"] = Color.FromRgb(0x31, 0x5C, 0x48),
                    ["WarningColor"] = Color.FromRgb(0xCF, 0x9D, 0x39),
                };
                foreach (var (key, color) in expectedColors)
                {
                    Assert.Equal(color, Assert.IsType<Color>(app.Resources[key]));
                }

                var first = Accounts.Record("first", "same@example.com", accountId: "account-1");
                var second = Accounts.Record("second", "same@example.com", accountId: "account-2");
                var registry = new AccountRegistry(3, first.AccountKey, [first, second]);
                var viewModel = CreateViewModel(registry);
                await viewModel.LoadAsync();
                mainWindow = new MainWindow(viewModel);
                Assert.True(mainWindow.ShowInTaskbar);
                mainWindow.Show();
                await mainWindow.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);

                Assert.NotNull(mainWindow.Icon);
                Assert.Same(viewModel, mainWindow.DataContext);
                Assert.Equal(440, mainWindow.Width);
                Assert.Equal(480, mainWindow.MinHeight);
                Assert.Equal(720, mainWindow.MaxHeight);
                Assert.Equal(8, Assert.IsType<Border>(mainWindow.Content).CornerRadius.TopLeft);

                var accountItems = Assert.IsType<ItemsControl>(mainWindow.FindName("AccountItems"));
                Assert.Same(
                    accountItems,
                    Assert.Single(FindVisualChildren<ItemsControl>(
                        Assert.Single(FindVisualChildren<ScrollViewer>(mainWindow)))));

                foreach (var name in new[] { "RefreshButton", "AddButton", "RemoveButton", "WindowCloseButton" })
                {
                    var button = Assert.IsType<Button>(mainWindow.FindName(name));
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                }
                var refreshButton = Assert.IsType<Button>(mainWindow.FindName("RefreshButton"));
                Assert.Contains("unofficial endpoint", refreshButton.ToolTip as string, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(
                    "unofficial endpoint",
                    AutomationProperties.GetHelpText(refreshButton),
                    StringComparison.OrdinalIgnoreCase);
                var retryLaunchButton = Assert.IsType<Button>(mainWindow.FindName("RetryLaunchButton"));
                Assert.Equal(Visibility.Collapsed, retryLaunchButton.Visibility);
                var refreshChrome = FindVisualChildren<Border>(
                        refreshButton)
                    .Single(border => border.Name == "Chrome");
                Assert.Equal(6, refreshChrome.CornerRadius.TopLeft);

                var switchButton = FindVisualChildren<Button>(mainWindow)
                    .Single(button =>
                        button.Content as string == "Switch" &&
                        !button.IsEnabled &&
                        button.Visibility == Visibility.Visible);
                Assert.True(ToolTipService.GetShowOnDisabled(switchButton));

                var activeSeparator = FindVisualChildren<Border>(mainWindow)
                    .Single(border =>
                        border.Name == "RowSeparator" &&
                        border.DataContext is AccountRowViewModel { IsActive: true });
                Assert.Equal(Visibility.Collapsed, activeSeparator.Visibility);
                var activeRow = FindVisualChildren<Border>(mainWindow)
                    .Single(border =>
                        border.Name == "RowBorder" &&
                        border.DataContext is AccountRowViewModel { IsActive: true });
                Assert.Equal(7, activeRow.CornerRadius.TopLeft);
                var details = FindVisualChildren<Expander>(mainWindow)
                    .Single(expander =>
                        expander.Name == "QuotaDetailsExpander" &&
                        expander.DataContext is AccountRowViewModel { IsActive: true });
                Assert.False(details.IsExpanded);
                Assert.Equal(Visibility.Visible, details.Visibility);
                var unqueriedPercent = FindVisualChildren<TextBlock>(mainWindow)
                    .Single(textBlock =>
                        textBlock.Name == "RemainingPercentText" &&
                        textBlock.DataContext is AccountRowViewModel row &&
                        row.Account.AccountKey == second.AccountKey);
                Assert.Equal(Visibility.Collapsed, unqueriedPercent.Visibility);

                var quotaStatus = FindVisualChildren<TextBlock>(mainWindow)
                    .Single(textBlock =>
                        textBlock.Name == "QuotaStatusTextBlock" &&
                        textBlock.DataContext is AccountRowViewModel row &&
                        row.Account.AccountKey == second.AccountKey);
                Assert.Equal(Visibility.Collapsed, quotaStatus.Visibility);
                var quotaError = "quota failed (HTTP 403).";
                viewModel.Accounts.Single(row => row.Account.AccountKey == second.AccountKey)
                    .ApplyQuota(new QuotaUpdate(second.AccountKey, null, quotaError));
                await mainWindow.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);
                Assert.Equal(Visibility.Visible, quotaStatus.Visibility);
                Assert.Equal(quotaError, quotaStatus.Text);
                Assert.Equal(quotaError, quotaStatus.ToolTip);

                var status = Assert.IsType<TextBlock>(mainWindow.FindName("StatusTextBlock"));
                Assert.Equal(TextWrapping.NoWrap, status.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, status.TextTrimming);
                Assert.NotNull(status.ToolTip);

                mainWindow.Close();
                Assert.False(mainWindow.IsVisible);

                var confirmationRow = new AccountRowViewModel(
                    second,
                    isActive: false,
                    canSwitch: true,
                    switchUnavailableReason: null);
                confirmationWindow = new SwitchConfirmationWindow(confirmationRow);
                confirmationWindow.Show();
                await confirmationWindow.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);
                var confirmationClose = FindVisualChildren<Button>(confirmationWindow)
                    .Single(button => button.ToolTip as string == "Close");
                Assert.Equal("Close", AutomationProperties.GetName(confirmationClose));
                confirmationWindow.Close();

                localizedWindow = new OperationWindow(OperationWindowText.AddAccount);
                Assert.False(localizedWindow.ShowInTaskbar);
                var localizedFirstRender = localizedWindow.ShowAndWaitForFirstRenderAsync(CancellationToken.None);
                Assert.False(localizedFirstRender.IsCompleted);
                await localizedFirstRender.WaitAsync(TimeSpan.FromSeconds(5));

                var localizedHeading = Assert.IsType<TextBlock>(localizedWindow.FindName("HeadingText"));
                var localizedPhase = Assert.IsType<TextBlock>(localizedWindow.FindName("PhaseText"));
                var localizedClose = Assert.IsType<Button>(localizedWindow.FindName("CloseButton"));
                var localizedHeaderClose = Assert.IsType<Button>(localizedWindow.FindName("HeaderCloseButton"));
                Assert.Equal("添加账号", localizedHeading.Text);
                Assert.Equal("等待浏览器登录", localizedPhase.Text);
                Assert.Equal("关闭", localizedClose.Content);
                Assert.Equal("关闭", localizedHeaderClose.ToolTip);
                Assert.Equal("关闭", AutomationProperties.GetName(localizedHeaderClose));
                Assert.Equal(Visibility.Collapsed, localizedClose.Visibility);
                Assert.Equal(Visibility.Collapsed, localizedHeaderClose.Visibility);
                localizedWindow.Close();
                Assert.True(localizedWindow.IsVisible);

                localizedWindow.Complete(new CommandResult(0, string.Empty, string.Empty));
                Assert.Equal("已完成", localizedPhase.Text);
                Assert.Equal(Visibility.Visible, localizedClose.Visibility);
                Assert.Equal(Visibility.Visible, localizedHeaderClose.Visibility);
                localizedWindow.Close();
                Assert.False(localizedWindow.IsVisible);

                localizedWindow = new OperationWindow(OperationWindowText.AddAccount);
                localizedWindow.Complete(new CommandResult(7, string.Empty, string.Empty));
                Assert.Equal("失败（退出代码 7）", Assert.IsType<TextBlock>(localizedWindow.FindName("PhaseText")).Text);

                localizedWindow = new OperationWindow(OperationWindowText.AddAccount);
                localizedWindow.Fail();
                Assert.Equal("操作失败", Assert.IsType<TextBlock>(localizedWindow.FindName("PhaseText")).Text);

                operationWindow = new OperationWindow("Add account", "Waiting for device login");
                var firstRender = operationWindow.ShowAndWaitForFirstRenderAsync(CancellationToken.None);
                Assert.False(firstRender.IsCompleted);
                await firstRender.WaitAsync(TimeSpan.FromSeconds(5));

                var operationClose = Assert.IsType<Button>(operationWindow.FindName("CloseButton"));
                var operationHeaderClose = Assert.IsType<Button>(operationWindow.FindName("HeaderCloseButton"));
                Assert.Equal("Add account", Assert.IsType<TextBlock>(operationWindow.FindName("HeadingText")).Text);
                Assert.Equal("Waiting for device login", Assert.IsType<TextBlock>(operationWindow.FindName("PhaseText")).Text);
                Assert.Equal("Close", operationClose.Content);
                Assert.Equal("Close", operationHeaderClose.ToolTip);
                Assert.Equal("Close", AutomationProperties.GetName(operationHeaderClose));
                Assert.Equal(Visibility.Collapsed, operationClose.Visibility);
                Assert.Equal(Visibility.Collapsed, operationHeaderClose.Visibility);
                operationWindow.Close();
                Assert.True(operationWindow.IsVisible);

                operationWindow.AppendLine(new ProcessOutputLine(
                    ProcessOutputStream.StandardError,
                    "login failed"));
                operationWindow.Complete(new CommandResult(
                    7,
                    string.Empty,
                    "login failed"));
                Assert.Equal("Failed (exit code 7)", Assert.IsType<TextBlock>(operationWindow.FindName("PhaseText")).Text);
                var streamedOutput = Assert.IsType<TextBox>(
                    operationWindow.FindName("OutputTextBox")).Text;
                Assert.Equal(1, CountOccurrences(streamedOutput, "login failed"));
                Assert.Equal(Visibility.Visible, operationClose.Visibility);
                operationWindow.Close();
                Assert.False(operationWindow.IsVisible);

                preflightWindow = new OperationWindow("Add account", "Preparing login");
                preflightWindow.Complete(new CommandResult(
                    1,
                    string.Empty,
                    "Authorization: Bearer secret-value"));
                Assert.Equal("Failed (exit code 1)", Assert.IsType<TextBlock>(preflightWindow.FindName("PhaseText")).Text);
                var output = Assert.IsType<TextBox>(preflightWindow.FindName("OutputTextBox")).Text;
                Assert.Contains("[REDACTED]", output, StringComparison.Ordinal);
                Assert.DoesNotContain("secret-value", output, StringComparison.Ordinal);

                preflightWindow = new OperationWindow("Remove account", "Waiting for account picker");
                preflightWindow.Complete(new CommandResult(0, string.Empty, string.Empty));
                Assert.Equal("Completed", Assert.IsType<TextBlock>(preflightWindow.FindName("PhaseText")).Text);
                preflightWindow.Fail();
                Assert.Equal("Operation failed", Assert.IsType<TextBlock>(preflightWindow.FindName("PhaseText")).Text);

                var cancelConfirmations = 0;
                var dialog = new AccountDialogService(
                    () => null,
                    new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
                    _ =>
                    {
                        cancelConfirmations++;
                        return true;
                    });
                const string url = "https://auth.openai.com/codex/device";
                const string deviceCode = "ABCD-EFGH";
                var windowsBeforeLogin = Application.Current.Windows.OfType<OperationWindow>().ToHashSet();
                var loginResult = await dialog.RunLoginAsync(
                    async (outputHandler, cancellationToken) =>
                    {
                        dialogLoginWindow = Assert.Single(
                            Application.Current.Windows.OfType<OperationWindow>(),
                            window => !windowsBeforeLogin.Contains(window));
                        await outputHandler(
                            new ProcessOutputLine(
                                ProcessOutputStream.StandardOutput,
                                "\u001b[90mWelcome to Codex [v0.145.0-alpha.30]\u001b[0m"),
                            cancellationToken);
                        await outputHandler(
                            new ProcessOutputLine(
                                ProcessOutputStream.StandardOutput,
                                $"  \u001b[94m{url}\u001b[0m"),
                            cancellationToken);
                        await outputHandler(
                            new ProcessOutputLine(
                                ProcessOutputStream.StandardOutput,
                                $"    \u001b[94m{deviceCode}\u001b[0m"),
                            cancellationToken);
                        return new CommandResult(0, string.Empty, string.Empty);
                    },
                    CancellationToken.None);

                Assert.True(loginResult.Succeeded);
                Assert.NotNull(dialogLoginWindow);
                Assert.Equal("添加账号", dialogLoginWindow.Title);
                Assert.Equal("添加账号", Assert.IsType<TextBlock>(dialogLoginWindow.FindName("HeadingText")).Text);
                Assert.Equal("已完成", Assert.IsType<TextBlock>(dialogLoginWindow.FindName("PhaseText")).Text);
                var loginClose = Assert.IsType<Button>(dialogLoginWindow.FindName("CloseButton"));
                var loginHeaderClose = Assert.IsType<Button>(dialogLoginWindow.FindName("HeaderCloseButton"));
                Assert.Equal("关闭", loginClose.Content);
                Assert.Equal("关闭", loginHeaderClose.ToolTip);
                Assert.Equal("关闭", AutomationProperties.GetName(loginHeaderClose));
                var loginOutput = Assert.IsType<TextBox>(dialogLoginWindow.FindName("OutputTextBox")).Text;
                Assert.Contains("欢迎使用 Codex [v0.145.0-alpha.30]", loginOutput, StringComparison.Ordinal);
                Assert.DoesNotContain('\u001b', loginOutput);
                Assert.DoesNotContain("90m", loginOutput, StringComparison.Ordinal);
                Assert.DoesNotContain("94m", loginOutput, StringComparison.Ordinal);
                Assert.DoesNotContain("0m", loginOutput, StringComparison.Ordinal);
                Assert.Contains($"  {url}", loginOutput, StringComparison.Ordinal);
                Assert.Contains($"    {deviceCode}", loginOutput, StringComparison.Ordinal);
                dialogLoginWindow.Close();
                Assert.DoesNotContain(
                    Application.Current.Windows.OfType<OperationWindow>(),
                    window => !windowsBeforeLogin.Contains(window));

                var windowsBeforeCancel = Application.Current.Windows.OfType<OperationWindow>().ToHashSet();
                var operationStarted = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var operationCanceled = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var canceledDialogTask = dialog.RunLoginAsync(
                    async (_, cancellationToken) =>
                    {
                        canceledLoginWindow = Assert.Single(
                            Application.Current.Windows.OfType<OperationWindow>(),
                            window => !windowsBeforeCancel.Contains(window));
                        operationStarted.TrySetResult();
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            operationCanceled.TrySetResult();
                        }

                        return CommandResult.Failed("Canceled.");
                    },
                    CancellationToken.None);

                await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(canceledLoginWindow);
                var cancelButton = Assert.IsType<Button>(canceledLoginWindow.FindName("CloseButton"));
                var cancelHeader = Assert.IsType<Button>(canceledLoginWindow.FindName("HeaderCloseButton"));
                var cancelPhase = Assert.IsType<TextBlock>(canceledLoginWindow.FindName("PhaseText"));
                Assert.Equal(Visibility.Visible, cancelButton.Visibility);
                Assert.Equal(Visibility.Visible, cancelHeader.Visibility);
                Assert.Equal("取消登录", cancelButton.Content);

                cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                cancelHeader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                await operationCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await canceledDialogTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, cancelConfirmations);
                Assert.Equal("登录已取消", cancelPhase.Text);
                Assert.Equal("关闭", cancelButton.Content);
                canceledLoginWindow.Close();
                Assert.False(canceledLoginWindow.IsVisible);

                var windowsBeforeRemoval = Application.Current.Windows.OfType<OperationWindow>().ToHashSet();
                var removeResult = await dialog.RunRemoveAsync(
                    _ =>
                    {
                        dialogRemoveWindow = Assert.Single(
                            Application.Current.Windows.OfType<OperationWindow>(),
                            window => !windowsBeforeRemoval.Contains(window));
                        return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
                    },
                    CancellationToken.None);

                Assert.True(removeResult.Succeeded);
                Assert.NotNull(dialogRemoveWindow);
                Assert.Equal("Remove account", dialogRemoveWindow.Title);
                Assert.Equal("Remove account", Assert.IsType<TextBlock>(dialogRemoveWindow.FindName("HeadingText")).Text);
                Assert.Equal("Completed", Assert.IsType<TextBlock>(dialogRemoveWindow.FindName("PhaseText")).Text);
                var removeClose = Assert.IsType<Button>(dialogRemoveWindow.FindName("CloseButton"));
                var removeHeaderClose = Assert.IsType<Button>(dialogRemoveWindow.FindName("HeaderCloseButton"));
                Assert.Equal("Close", removeClose.Content);
                Assert.Equal("Close", removeHeaderClose.ToolTip);
                Assert.Equal("Close", AutomationProperties.GetName(removeHeaderClose));
                dialogRemoveWindow.Close();
                Assert.DoesNotContain(
                    Application.Current.Windows.OfType<OperationWindow>(),
                    window => !windowsBeforeRemoval.Contains(window));
            }
            finally
            {
                confirmationWindow?.Close();
                if (localizedWindow?.IsVisible == true)
                {
                    localizedWindow.Fail();
                    localizedWindow.Close();
                }

                if (preflightWindow?.IsVisible == true)
                {
                    preflightWindow.Close();
                }

                if (operationWindow?.IsVisible == true)
                {
                    operationWindow.Complete(new CommandResult(1, string.Empty, string.Empty));
                    operationWindow.Close();
                }

                if (dialogLoginWindow?.IsVisible == true)
                {
                    dialogLoginWindow.Complete(new CommandResult(1, string.Empty, string.Empty));
                    dialogLoginWindow.Close();
                }

                if (canceledLoginWindow?.IsVisible == true)
                {
                    canceledLoginWindow.Fail();
                    canceledLoginWindow.Close();
                }

                if (dialogRemoveWindow?.IsVisible == true)
                {
                    dialogRemoveWindow.Complete(new CommandResult(1, string.Empty, string.Empty));
                    dialogRemoveWindow.Close();
                }

                if (mainWindow is not null)
                {
                    mainWindow.AllowClose();
                    mainWindow.Close();
                }
            }
        });

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static MainWindowViewModel CreateViewModel(AccountRegistry registry) => new(
        _ => Task.FromResult(registry),
        (_, _, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new CommandResult(0, string.Empty, string.Empty)),
        _ => Task.FromResult(new CommandResult(0, string.Empty, string.Empty)),
        (_, _, _) => Task.FromResult(new SwitchResult(true, "switched", true)),
        _ => Task.FromResult(true),
        new NoOpDialogService(),
        new InlineDispatcher(),
        new ActiveOperationTracker());

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static Task RunOnStaThreadAsync(Func<Task> action) => RunOnStaThreadAsync(
        action,
        TimeSpan.FromSeconds(15),
        threadStarted: null);

    private static async Task RunOnStaThreadAsync(
        Func<Task> action,
        TimeSpan timeout,
        Action<Thread>? threadStarted)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcherReady.TrySetResult(dispatcher);
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    if (!dispatcher.HasShutdownStarted)
                    {
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }
            }));
            Dispatcher.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        threadStarted?.Invoke(thread);
        thread.Start();

        try
        {
            await dispatcherReady.Task.WaitAsync(timeout);
            await completion.Task.WaitAsync(timeout);
        }
        finally
        {
            if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("STA dispatcher thread did not terminate after shutdown.");
            }
        }
    }

    private sealed class NoOpDialogService : IAccountDialogService
    {
        public Task<AccountMetadata?> EditMetadataAsync(
            AccountRowViewModel target,
            CancellationToken cancellationToken) =>
            Task.FromResult<AccountMetadata?>(null);

        public Task<AccountRowViewModel?> SelectRemovalTargetAsync(
            IReadOnlyList<AccountRowViewModel> accounts,
            CancellationToken cancellationToken) =>
            Task.FromResult<AccountRowViewModel?>(null);

        public Task<bool> ConfirmSwitchAsync(
            AccountRowViewModel target,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<CommandResult> RunLoginAsync(
            Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> operation,
            CancellationToken cancellationToken) =>
            operation(static (_, _) => ValueTask.CompletedTask, cancellationToken);

        public Task<CommandResult> RunRemoveAsync(
            Func<CancellationToken, Task<CommandResult>> operation,
            CancellationToken cancellationToken) => operation(cancellationToken);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
