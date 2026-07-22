using System.Collections.ObjectModel;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.ViewModels;

public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
}

public interface IOperationActivityTracker
{
    bool IsActive { get; }

    IDisposable Begin();
}

public interface IAccountDialogService
{
    Task<bool> ConfirmAddAsync(CancellationToken cancellationToken);

    Task<AccountRowViewModel?> SelectRemovalTargetAsync(
        IReadOnlyList<AccountRowViewModel> accounts,
        CancellationToken cancellationToken);

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
    private readonly Func<ProcessOutputHandler, CancellationToken, Task<LoginResult>> _loginAsync;
    private readonly Func<
        AccountRecord,
        AccountRegistry,
        CancellationToken,
        Task<RemovalResult>> _removeAsync;
    private readonly Func<
        AccountRecord,
        AccountRegistry,
        CancellationToken,
        Task<SwitchResult>> _switchAsync;
    private readonly Func<CancellationToken, Task<bool>> _retryLaunchAsync;
    private readonly Func<HelperAvailability> _checkHelperAvailability;
    private readonly IAccountDialogService _dialogService;
    private readonly IUiDispatcher _dispatcher;
    private readonly IOperationActivityTracker _activityTracker;
    private AccountRegistry _registry = AccountRegistry.Empty;
    private int _operationGate;
    private bool _isBusy;
    private bool _canRetryLaunch;
    private bool _isHelperAvailable;
    private string _helperAvailabilityError = string.Empty;
    private string _statusText = string.Empty;

    public MainWindowViewModel(
        string codexHome,
        AccountRegistryService accountRegistryService,
        QuotaService quotaService,
        CodexAuthService codexAuthService,
        SafeLoginCoordinator safeLoginCoordinator,
        TargetedRemoveCoordinator targetedRemoveCoordinator,
        SafeSwitchCoordinator safeSwitchCoordinator,
        IAccountDialogService dialogService,
        IUiDispatcher dispatcher,
        IOperationActivityTracker activityTracker)
        : this(
            CreateLoadRegistryDelegate(codexHome, accountRegistryService),
            CreateRefreshQuotaDelegate(codexHome, quotaService),
            CreateLoginDelegate(safeLoginCoordinator),
            CreateRemoveDelegate(targetedRemoveCoordinator),
            CreateSwitchDelegate(safeSwitchCoordinator),
            safeSwitchCoordinator.RetryLaunchAsync,
            codexAuthService.CheckAvailability,
            dialogService,
            dispatcher,
            activityTracker)
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
        Func<CancellationToken, Task<bool>> retryLaunchAsync,
        IAccountDialogService dialogService,
        IUiDispatcher dispatcher,
        IOperationActivityTracker activityTracker)
        : this(
            loadRegistryAsync,
            refreshQuotaAsync,
            async (outputHandler, cancellationToken) =>
            {
                var result = await loginAsync(outputHandler, cancellationToken);
                return new LoginResult(
                    result.Succeeded,
                    result.Succeeded ? "Login completed." : "Login failed.",
                    true);
            },
            async (_, _, cancellationToken) =>
            {
                var result = await removeAsync(cancellationToken);
                return new RemovalResult(
                    result.Succeeded,
                    result.Succeeded ? "Removal completed." : "Removal failed.");
            },
            switchAsync,
            retryLaunchAsync,
            static () => new HelperAvailability(true, "codex-auth.exe", string.Empty),
            dialogService,
            dispatcher,
            activityTracker)
    {
    }

    internal MainWindowViewModel(
        Func<CancellationToken, Task<AccountRegistry>> loadRegistryAsync,
        Func<
            IReadOnlyList<AccountRecord>,
            IProgress<QuotaUpdate>,
            CancellationToken,
            Task> refreshQuotaAsync,
        Func<ProcessOutputHandler, CancellationToken, Task<LoginResult>> loginAsync,
        Func<
            AccountRecord,
            AccountRegistry,
            CancellationToken,
            Task<RemovalResult>> removeAsync,
        Func<
            AccountRecord,
            AccountRegistry,
            CancellationToken,
            Task<SwitchResult>> switchAsync,
        Func<CancellationToken, Task<bool>> retryLaunchAsync,
        Func<HelperAvailability> checkHelperAvailability,
        IAccountDialogService dialogService,
        IUiDispatcher dispatcher,
        IOperationActivityTracker activityTracker)
    {
        _loadRegistryAsync = loadRegistryAsync ?? throw new ArgumentNullException(nameof(loadRegistryAsync));
        _refreshQuotaAsync = refreshQuotaAsync ?? throw new ArgumentNullException(nameof(refreshQuotaAsync));
        _loginAsync = loginAsync ?? throw new ArgumentNullException(nameof(loginAsync));
        _removeAsync = removeAsync ?? throw new ArgumentNullException(nameof(removeAsync));
        _switchAsync = switchAsync ?? throw new ArgumentNullException(nameof(switchAsync));
        _retryLaunchAsync = retryLaunchAsync ?? throw new ArgumentNullException(nameof(retryLaunchAsync));
        _checkHelperAvailability = checkHelperAvailability
            ?? throw new ArgumentNullException(nameof(checkHelperAvailability));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _activityTracker = activityTracker ?? throw new ArgumentNullException(nameof(activityTracker));

        var availability = _checkHelperAvailability();
        _isHelperAvailable = availability.IsAvailable;
        _helperAvailabilityError = availability.Error;

        RefreshCommand = new AsyncCommand(
            _dispatcher,
            (_, cancellationToken) => RunBusyAsync(RefreshQuotaAsync, cancellationToken),
            _ => !IsBusy && IsHelperAvailable,
            HandleCommandErrorAsync);
        AddCommand = new AsyncCommand(
            _dispatcher,
            (_, cancellationToken) => RunBusyAsync(LoginAsync, cancellationToken),
            _ => !IsBusy && IsHelperAvailable,
            HandleCommandErrorAsync);
        RemoveCommand = new AsyncCommand(
            _dispatcher,
            (_, cancellationToken) => RunBusyAsync(RemoveAsync, cancellationToken),
            _ => !IsBusy && IsHelperAvailable,
            HandleCommandErrorAsync);
        SwitchCommand = new AsyncCommand(
            _dispatcher,
            SwitchAccountAsync,
            parameter => !IsBusy && IsHelperAvailable && parameter is AccountRowViewModel { CanSwitch: true },
            HandleCommandErrorAsync);
        RetryLaunchCommand = new AsyncCommand(
            _dispatcher,
            (_, cancellationToken) => RunBusyAsync(RetryLaunchAsync, cancellationToken),
            _ => !IsBusy && CanRetryLaunch,
            HandleRetryLaunchErrorAsync);
    }

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand AddCommand { get; }

    public AsyncCommand RemoveCommand { get; }

    public AsyncCommand SwitchCommand { get; }

    public AsyncCommand RetryLaunchCommand { get; }

    public bool CanRetryLaunch
    {
        get => _canRetryLaunch;
        private set
        {
            if (SetProperty(ref _canRetryLaunch, value))
            {
                RetryLaunchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsHelperAvailable
    {
        get => _isHelperAvailable;
        private set => SetProperty(ref _isHelperAvailable, value);
    }

    public string HelperAvailabilityError
    {
        get => _helperAvailabilityError;
        private set => SetProperty(ref _helperAvailabilityError, value);
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
        var availability = _checkHelperAvailability();
        var registry = await _loadRegistryAsync(cancellationToken);
        await _dispatcher.InvokeAsync(
            () =>
            {
                ApplyRegistry(registry);
                ApplyHelperAvailability(availability);
            },
            cancellationToken);
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
        if (!await _dialogService.ConfirmAddAsync(cancellationToken))
        {
            return;
        }

        LoginResult? loginResult = null;
        await _dialogService.RunLoginAsync(
            async (outputHandler, token) =>
            {
                loginResult = await _loginAsync(outputHandler, token);
                return new CommandResult(
                    loginResult.Succeeded ? 0 : 1,
                    string.Empty,
                    loginResult.Message);
            },
            cancellationToken);
        var result = loginResult
            ?? throw new InvalidOperationException("The account login did not return a result.");
        var registry = await _loadRegistryAsync(CancellationToken.None);
        await _dispatcher.InvokeAsync(
            () =>
            {
                ApplyRegistry(registry);
                StatusText = result.Message;
                CanRetryLaunch = result.CanRetryLaunch;
            },
            CancellationToken.None);
    }

    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        var target = await _dialogService.SelectRemovalTargetAsync(
            Accounts.ToArray(),
            cancellationToken);
        if (target is null)
        {
            return;
        }

        var result = await _removeAsync(target.Account, _registry, cancellationToken);
        var registry = await _loadRegistryAsync(CancellationToken.None);
        await _dispatcher.InvokeAsync(
            () =>
            {
                ApplyRegistry(registry);
                StatusText = result.Message;
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
                    CanRetryLaunch = result.CanRetryLaunch;
                },
                CancellationToken.None);
            return;
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                StatusText = result.Message;
                CanRetryLaunch = result.CanRetryLaunch;
            },
            CancellationToken.None);
    }

    private async Task RetryLaunchAsync(CancellationToken cancellationToken)
    {
        var succeeded = await _retryLaunchAsync(cancellationToken);
        await _dispatcher.InvokeAsync(
            () =>
            {
                StatusText = succeeded ? "Codex launched." : "Codex launch retry failed.";
                CanRetryLaunch = !succeeded;
            },
            CancellationToken.None);
    }

    private async Task RunBusyAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationGate, 1, 0) != 0)
        {
            return;
        }

        using var activity = _activityTracker.Begin();
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

    private Task HandleRetryLaunchErrorAsync(Exception exception) =>
        _dispatcher.InvokeAsync(
            () =>
            {
                StatusText = "Codex launch retry failed.";
                CanRetryLaunch = true;
            },
            CancellationToken.None);

    private void ApplyRegistry(AccountRegistry registry)
    {
        var priorRows = Accounts.ToDictionary(
            row => row.Account.AccountKey,
            StringComparer.Ordinal);
        _registry = registry;
        Accounts.Clear();
        foreach (var account in registry.Accounts)
        {
            var isActive = string.Equals(
                account.AccountKey,
                registry.ActiveAccountKey,
                StringComparison.Ordinal);
            var selector = AccountSelectorResolver.Resolve(account, registry.Accounts);
            var canSwitch = !isActive && selector.IsAvailable;
            var unavailableReason = isActive ? AlreadyActiveReason : selector.Error;
            if (priorRows.TryGetValue(account.AccountKey, out var priorRow))
            {
                priorRow.ApplyAccountState(account, isActive, canSwitch, unavailableReason);
                Accounts.Add(priorRow);
            }
            else
            {
                Accounts.Add(new AccountRowViewModel(
                    account,
                    isActive,
                    canSwitch,
                    unavailableReason));
            }
        }

        RaiseCommandCanExecuteChanged();
    }

    private void ApplyHelperAvailability(HelperAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        IsHelperAvailable = availability.IsAvailable;
        HelperAvailabilityError = availability.Error;
        if (!availability.IsAvailable)
        {
            StatusText = availability.Error;
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
        RetryLaunchCommand.NotifyCanExecuteChanged();
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

    private static Func<ProcessOutputHandler, CancellationToken, Task<LoginResult>> CreateLoginDelegate(
        SafeLoginCoordinator safeLoginCoordinator)
    {
        ArgumentNullException.ThrowIfNull(safeLoginCoordinator);
        return safeLoginCoordinator.LoginAsync;
    }

    private static Func<
        AccountRecord,
        AccountRegistry,
        CancellationToken,
        Task<RemovalResult>> CreateRemoveDelegate(
            TargetedRemoveCoordinator targetedRemoveCoordinator)
    {
        ArgumentNullException.ThrowIfNull(targetedRemoveCoordinator);
        return targetedRemoveCoordinator.RemoveAsync;
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
