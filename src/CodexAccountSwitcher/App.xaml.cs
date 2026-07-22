using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.Tray;
using CodexAccountSwitcher.ViewModels;
using CodexAccountSwitcher.Views;

namespace CodexAccountSwitcher;

public partial class App : System.Windows.Application
{
    private HttpClient? _httpClient;
    private MainWindow? _mainWindow;
    private TrayIconHost? _trayIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
            var processRunner = new ProcessRunner();
            var package = await new CodexPackageService(processRunner)
                .DiscoverAsync(CancellationToken.None);
            var authService = new CodexAuthService(
                ResolveHelperPath(),
                package.CliDirectory,
                processRunner);
            var registryService = new AccountRegistryService();
            _httpClient = new HttpClient();
            var quotaService = new QuotaService(_httpClient);
            var processController = new CodexProcessController();
            var switchCoordinator = new SafeSwitchCoordinator(
                package,
                codexHome,
                processController,
                authService,
                registryService);
            var uiDispatcher = new WpfUiDispatcher(Dispatcher);
            var dialogService = new AccountDialogService(() => _mainWindow, uiDispatcher);
            var viewModel = new MainWindowViewModel(
                codexHome,
                registryService,
                quotaService,
                authService,
                switchCoordinator,
                dialogService,
                uiDispatcher);

            _mainWindow = new MainWindow(viewModel);
            MainWindow = _mainWindow;
            _trayIcon = new TrayIconHost(OpenMainWindow, ExitApplication);
            _trayIcon.Show();
            await _mainWindow.ShowAndReloadAsync();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Codex account switcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApplication();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _httpClient?.Dispose();
        base.OnExit(e);
    }

    private static string ResolveHelperPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "tools", "codex-auth.exe");
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        return Path.Combine(
            Environment.CurrentDirectory,
            "vendor",
            "codex-auth",
            "codex-auth.exe");
    }

    private async void OpenMainWindow()
    {
        try
        {
            var reloadTask = await Dispatcher.InvokeAsync(() =>
                _mainWindow?.ShowAndReloadAsync() ?? Task.CompletedTask);
            await reloadTask;
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Codex account switcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ExitApplication()
    {
        _trayIcon?.Dispose();
        _mainWindow?.AllowClose();
        _mainWindow?.Close();
        Shutdown();
    }
}

internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher
        ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }
}

internal sealed class AccountDialogService(
    Func<Window?> ownerProvider,
    IUiDispatcher dispatcher) : IAccountDialogService
{
    private readonly Func<Window?> _ownerProvider = ownerProvider
        ?? throw new ArgumentNullException(nameof(ownerProvider));
    private readonly IUiDispatcher _dispatcher = dispatcher
        ?? throw new ArgumentNullException(nameof(dispatcher));

    public async Task<bool> ConfirmSwitchAsync(
        AccountRowViewModel target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var confirmed = false;
        await _dispatcher.InvokeAsync(
            () =>
            {
                var window = new SwitchConfirmationWindow(target)
                {
                    Owner = _ownerProvider(),
                };
                confirmed = window.ShowDialog() == true;
            },
            cancellationToken);
        return confirmed;
    }

    public Task<CommandResult> RunLoginAsync(
        Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        OperationWindow? window = null;
        return DialogOperationRunner.RunLoginAsync(
            showAsync: () => _dispatcher.InvokeAsync(
                () =>
                {
                    window = CreateOperationWindow("Add account", "Waiting for device login");
                    window.Show();
                },
                cancellationToken),
            appendAsync: (line, token) => new ValueTask(_dispatcher.InvokeAsync(
                () => window!.AppendLine(line),
                token)),
            completeAsync: result => _dispatcher.InvokeAsync(
                () => window!.Complete(result),
                CancellationToken.None),
            failAsync: _ => _dispatcher.InvokeAsync(
                () => window!.Fail(),
                CancellationToken.None),
            operation,
            cancellationToken);
    }

    public Task<CommandResult> RunRemoveAsync(
        Func<CancellationToken, Task<CommandResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        OperationWindow? window = null;
        return DialogOperationRunner.RunRemoveAsync(
            showAsync: () => _dispatcher.InvokeAsync(
                () =>
                {
                    window = CreateOperationWindow("Remove account", "Waiting for account picker");
                    window.Show();
                },
                cancellationToken),
            completeAsync: result => _dispatcher.InvokeAsync(
                () => window!.Complete(result),
                CancellationToken.None),
            failAsync: _ => _dispatcher.InvokeAsync(
                () => window!.Fail(),
                CancellationToken.None),
            operation,
            cancellationToken);
    }

    private OperationWindow CreateOperationWindow(string heading, string phase) => new(heading, phase)
    {
        Owner = _ownerProvider(),
    };
}
