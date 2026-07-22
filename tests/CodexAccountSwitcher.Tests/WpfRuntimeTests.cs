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
    public Task Concrete_windows_render_bind_and_enforce_accessible_close_contracts() =>
        RunOnStaThreadAsync(async () =>
        {
            var app = new App();
            app.InitializeComponent();
            MainWindow? mainWindow = null;
            OperationWindow? operationWindow = null;
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
                mainWindow.Show();
                await mainWindow.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);

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
                var refreshChrome = FindVisualChildren<Border>(
                        Assert.IsType<Button>(mainWindow.FindName("RefreshButton")))
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

                operationWindow = new OperationWindow("Add account", "Waiting for device login");
                var firstRender = operationWindow.ShowAndWaitForFirstRenderAsync(CancellationToken.None);
                Assert.False(firstRender.IsCompleted);
                await firstRender.WaitAsync(TimeSpan.FromSeconds(5));

                var operationClose = Assert.IsType<Button>(operationWindow.FindName("CloseButton"));
                var operationHeaderClose = Assert.IsType<Button>(operationWindow.FindName("HeaderCloseButton"));
                Assert.Equal("Close", AutomationProperties.GetName(operationHeaderClose));
                Assert.Equal(Visibility.Collapsed, operationClose.Visibility);
                operationWindow.Close();
                Assert.True(operationWindow.IsVisible);

                operationWindow.Complete(new CommandResult(
                    1,
                    string.Empty,
                    "Authorization: Bearer secret-value"));
                var output = Assert.IsType<TextBox>(operationWindow.FindName("OutputTextBox")).Text;
                Assert.Contains("[REDACTED]", output, StringComparison.Ordinal);
                Assert.DoesNotContain("secret-value", output, StringComparison.Ordinal);
                Assert.Equal(Visibility.Visible, operationClose.Visibility);
                operationWindow.Close();
                Assert.False(operationWindow.IsVisible);
            }
            finally
            {
                confirmationWindow?.Close();
                if (operationWindow?.IsVisible == true)
                {
                    operationWindow.Complete(new CommandResult(1, string.Empty, string.Empty));
                    operationWindow.Close();
                }

                if (mainWindow is not null)
                {
                    mainWindow.AllowClose();
                    mainWindow.Close();
                }
            }
        });

    private static MainWindowViewModel CreateViewModel(AccountRegistry registry) => new(
        _ => Task.FromResult(registry),
        (_, _, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new CommandResult(0, string.Empty, string.Empty)),
        _ => Task.FromResult(new CommandResult(0, string.Empty, string.Empty)),
        (_, _, _) => Task.FromResult(new SwitchResult(true, "switched", true)),
        new NoOpDialogService(),
        new InlineDispatcher());

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

    private static Task RunOnStaThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
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
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class NoOpDialogService : IAccountDialogService
    {
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
