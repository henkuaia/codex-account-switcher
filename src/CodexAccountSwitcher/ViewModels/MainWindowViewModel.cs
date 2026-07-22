using System.Collections.ObjectModel;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.ViewModels;

public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
}

public interface IAccountDialogService
{
    Task<bool> ConfirmSwitchAsync(
        AccountRowViewModel target,
        CancellationToken cancellationToken);

    Task<CommandResult> RunLoginAsync(
        Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> operation,
        CancellationToken cancellationToken);

    Task<CommandResult> RunRemoveAsync(
        Func<CancellationToken, Task<CommandResult>> operation,
        CancellationToken cancellationToken);
}

public sealed class MainWindowViewModel : ObservableObject
{
    private const string AlreadyActiveReason = "This account is already active.";

    private readonly Func<CancellationToken, Task<AccountRegistry>> _loadRegistryAsync;
    private readonly Func<
        IReadOnlyList<AccountRecord>,
        IProgress<QuotaUpdate>,
        CancellationToken,
        Task> _refreshQuotaAsync;
    private readonly Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> _loginAsync;
    private readonly Func<CancellationToken, Task<CommandResult>> _removeAsync;
    private readonly Func<
        AccountRecord,
        AccountRegistry,
        CancellationToken,
        Task<SwitchResult>> _switchAsync;
    private readonly IAccountDialogService _dialogService;
    private readonly IUiDispatcher _dispatcher;
    private AccountRegistry _registry = AccountRegistry.Empty;
    private int _operationGate;
    private bool _isBusy;
    private string _statusText = string.Empty;

    public MainWindowViewModel(
        string codexHome,
        AccountRegistryService accountRegistryService,
        QuotaService quotaService,
        CodexAuthService codexAuthService,
        SafeSwitchCoordinator safeSwitchCoordinator,
        IAccountDialogService dialogService,
        IUiDispatcher dispatcher)
        : this(
            CreateLoadRegistryDelegate(codexHome, accountRegistryService),
            CreateRefreshQuotaDelegate(codexHome, quotaService),
            CreateLoginDelegate(codexAuthService),
            CreateRemoveDelegate(codexAuthService),
            CreateSwitchDelegate(safeSwitchCoordinator),
            dialogService,
            dispatcher)
    {
    }

    internal MainWindowViewModel(
        Func<CancellationToken, Task<AccountRegistry>> loadRegistryAsync,
        Func<
            IReadOnlyList<AccountRecord>,
            IProgress<QuotaUpdate>,
            CancellationToken,
            Task> refreshQuotaAsync,
        Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> loginAsync,
        Func<CancellationToken, Task<CommandResult>> removeAsync,
        Func<
            AccountRecord,
            AccountRegistry,
            CancellationToken,
            Task<SwitchResult>> switchAsync,
        IAccountDialogService dialogService,
        IUiDispatcher dispatcher)
    {
        _loadRegistryAsync = loadRegistryAsync ?? throw new ArgumentNullException(nameof(loadRegistryAsync));
        _refreshQuotaAsync = refreshQuotaAsync ?? throw new ArgumentNullException(nameof(refreshQuotaAsync));
        _loginAsync = loginAsync ?? throw new ArgumentNullException(nameof(loginAsync));
        _removeAsync = removeAsync ?? throw new ArgumentNullException(nameof(removeAsync));
        _switchAsync = switchAsync ?? throw new ArgumentNullException(nameof(switchAsync));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        RefreshCommand = new AsyncCommand(
            _dispatcher,
            (_, cancellationToken) => RunBusyAsync(RefreshQuotaAsync, cancellationToken),
            _ => !IsBusy,
            HandleCommandErrorAsync);
        AddCommand = new AsyncCommand(
            _dispatcher,
            (_, cancellationToken) => RunBusyAsync(LoginAsync, cancellationToken),
            _ => !IsBusy,
            HandleCommandErrorAsync);
        RemoveCommand = new AsyncCommand(
            _dispatcher,
            (_, cancellationToken) => RunBusyAsync(RemoveAsync, cancellationToken),
            _ => !IsBusy,
            HandleCommandErrorAsync);
        SwitchCommand = new AsyncCommand(
            _dispatcher,
            SwitchAccountAsync,
            parameter => !IsBusy && parameter is AccountRowViewModel { CanSwitch: true },
            HandleCommandErrorAsync);
    }

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand AddCommand { get; }

    public AsyncCommand RemoveCommand { get; }

    public AsyncCommand SwitchCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(LoadRegistryAsync, cancellationToken);

    private async Task LoadRegistryAsync(CancellationToken cancellationToken)
    {
        var registry = await _loadRegistryAsync(cancellationToken);
        await _dispatcher.InvokeAsync(() => ApplyRegistry(registry), cancellationToken);
    }

    private async Task RefreshQuotaAsync(CancellationToken cancellationToken)
    {
        var updates = new List<QuotaUpdate>();
        await _refreshQuotaAsync(
            _registry.Accounts.ToArray(),
            new InlineProgress<QuotaUpdate>(updates.Add),
            cancellationToken);
        await _dispatcher.InvokeAsync(
            () =>
            {
                foreach (var update in updates)
                {
                    Accounts.FirstOrDefault(row => string.Equals(
                        row.Account.AccountKey,
                        update.AccountKey,
                        StringComparison.Ordinal))?.ApplyQuota(update);
                }

                StatusText = "Quota refresh completed.";
            },
            cancellationToken);
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        var result = await _dialogService.RunLoginAsync(_loginAsync, cancellationToken);
        var registry = await _loadRegistryAsync(CancellationToken.None);
        await _dispatcher.InvokeAsync(
            () =>
            {
                ApplyRegistry(registry);
                StatusText = result.Succeeded ? "Login completed." : "Login failed.";
            },
            CancellationToken.None);
    }

    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        var result = await _dialogService.RunRemoveAsync(_removeAsync, cancellationToken);
        var registry = await _loadRegistryAsync(CancellationToken.None);
        await _dispatcher.InvokeAsync(
            () =>
            {
                ApplyRegistry(registry);
                StatusText = result.Succeeded ? "Removal completed." : "Removal failed.";
            },
            CancellationToken.None);
    }

    private Task SwitchAccountAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (parameter is not AccountRowViewModel target)
        {
            return Task.CompletedTask;
        }

        return RunBusyAsync(token => SwitchAccountAsync(target, token), cancellationToken);
    }

    private async Task SwitchAccountAsync(
        AccountRowViewModel target,
        CancellationToken cancellationToken)
    {
        if (!await _dialogService.ConfirmSwitchAsync(target, cancellationToken))
        {
            return;
        }

        var result = await _switchAsync(target.Account, _registry, cancellationToken);
        if (result.Succeeded)
        {
            var registry = await _loadRegistryAsync(CancellationToken.None);
            await _dispatcher.InvokeAsync(
                () =>
                {
                    ApplyRegistry(registry);
                    StatusText = result.Message;
                },
                CancellationToken.None);
            return;
        }

        await _dispatcher.InvokeAsync(() => StatusText = result.Message, cancellationToken);
    }

    private async Task RunBusyAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationGate, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() => SetBusy(true), cancellationToken);
            await operation(cancellationToken);
        }
        finally
        {
            try
            {
                await _dispatcher.InvokeAsync(() => SetBusy(false), CancellationToken.None);
            }
            finally
            {
                Volatile.Write(ref _operationGate, 0);
            }
        }
    }

    private Task HandleCommandErrorAsync(Exception exception) =>
        _dispatcher.InvokeAsync(() => StatusText = exception.Message, CancellationToken.None);

    private void ApplyRegistry(AccountRegistry registry)
    {
        _registry = registry;
        Accounts.Clear();
        foreach (var account in registry.Accounts)
        {
            var isActive = string.Equals(
                account.AccountKey,
                registry.ActiveAccountKey,
                StringComparison.Ordinal);
            var selector = AccountSelectorResolver.Resolve(account, registry.Accounts);
            Accounts.Add(new AccountRowViewModel(
                account,
                isActive,
                !isActive && selector.IsAvailable,
                isActive ? AlreadyActiveReason : selector.Error));
        }

        RaiseCommandCanExecuteChanged();
    }

    private void SetBusy(bool value)
    {
        IsBusy = value;
        RaiseCommandCanExecuteChanged();
    }

    private void RaiseCommandCanExecuteChanged()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        AddCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        SwitchCommand.NotifyCanExecuteChanged();
    }

    private static Func<CancellationToken, Task<AccountRegistry>> CreateLoadRegistryDelegate(
        string codexHome,
        AccountRegistryService accountRegistryService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        ArgumentNullException.ThrowIfNull(accountRegistryService);
        return cancellationToken => accountRegistryService.LoadAsync(codexHome, cancellationToken);
    }

    private static Func<
        IReadOnlyList<AccountRecord>,
        IProgress<QuotaUpdate>,
        CancellationToken,
        Task> CreateRefreshQuotaDelegate(
            string codexHome,
            QuotaService quotaService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        ArgumentNullException.ThrowIfNull(quotaService);
        return (accounts, progress, cancellationToken) =>
            quotaService.RefreshAllAsync(accounts, codexHome, progress, cancellationToken);
    }

    private static Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> CreateLoginDelegate(
        CodexAuthService codexAuthService)
    {
        ArgumentNullException.ThrowIfNull(codexAuthService);
        return codexAuthService.LoginAsync;
    }

    private static Func<CancellationToken, Task<CommandResult>> CreateRemoveDelegate(
        CodexAuthService codexAuthService)
    {
        ArgumentNullException.ThrowIfNull(codexAuthService);
        return codexAuthService.RemoveAsync;
    }

    private static Func<
        AccountRecord,
        AccountRegistry,
        CancellationToken,
        Task<SwitchResult>> CreateSwitchDelegate(SafeSwitchCoordinator safeSwitchCoordinator)
    {
        ArgumentNullException.ThrowIfNull(safeSwitchCoordinator);
        return safeSwitchCoordinator.SwitchAsync;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
