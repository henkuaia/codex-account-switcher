using System.Windows.Input;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.ViewModels;
using CodexAccountSwitcher.Views;

namespace CodexAccountSwitcher.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task Initial_load_maps_active_account_and_disables_its_switch()
    {
        var fixture = new Fixture();

        await fixture.ViewModel.LoadAsync();

        Assert.Equal(2, fixture.ViewModel.Accounts.Count);
        var active = Assert.Single(fixture.ViewModel.Accounts, row => row.Account.AccountKey == fixture.First.AccountKey);
        var inactive = Assert.Single(fixture.ViewModel.Accounts, row => row.Account.AccountKey == fixture.Second.AccountKey);
        Assert.True(active.IsActive);
        Assert.False(active.CanSwitch);
        Assert.NotNull(active.SwitchUnavailableReason);
        Assert.False(inactive.IsActive);
        Assert.True(inactive.CanSwitch);
        Assert.Null(inactive.SwitchUnavailableReason);
        Assert.Equal("First", active.DisplayIdentity);
        Assert.Equal("Not queried", active.QuotaLabel);
    }

    [Fact]
    public async Task Initial_load_does_not_refresh_quota()
    {
        var fixture = new Fixture();

        await fixture.ViewModel.LoadAsync();

        Assert.Equal(0, fixture.QuotaRefreshCallCount);
    }

    [Fact]
    public async Task Refresh_is_manual_and_calls_quota_service_once()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.QuotaUpdates =
        [
            new QuotaUpdate(
                fixture.First.AccountKey,
                new QuotaDisplay(QuotaPeriod.Weekly, 73, null, TimeSpan.FromDays(7), "weekly"),
                null),
            new QuotaUpdate(
                fixture.Second.AccountKey,
                new QuotaDisplay(QuotaPeriod.Unknown, 42, null, TimeSpan.FromDays(12), "other"),
                null),
        ];

        await fixture.ViewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal(1, fixture.QuotaRefreshCallCount);
        Assert.Equal("Weekly", fixture.Row(fixture.First).QuotaLabel);
        Assert.Equal("Quota", fixture.Row(fixture.Second).QuotaLabel);
    }

    [Fact]
    public async Task Busy_operation_disables_all_mutation_commands()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        var releaseLogin = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.LoginOperation = (_, _) => releaseLogin.Task;

        var running = fixture.ViewModel.AddCommand.ExecuteAsync();
        await fixture.Dialog.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.AddCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.RemoveCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.SwitchCommand.CanExecute(fixture.Row(fixture.Second)));

        releaseLogin.SetResult(Succeeded());
        await running;
        Assert.False(fixture.ViewModel.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task View_model_gate_rejects_competing_work_before_delayed_busy_dispatch(
        bool competeWithLoad)
    {
        var dispatcher = new ControllableDispatcher();
        var fixture = new Fixture(dispatcher);
        await fixture.ViewModel.LoadAsync();
        var delayedBusy = dispatcher.DelayInvocation(2);

        var first = fixture.ViewModel.AddCommand.ExecuteAsync();
        await delayedBusy.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var second = competeWithLoad
            ? fixture.ViewModel.LoadAsync()
            : fixture.ViewModel.RemoveCommand.ExecuteAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        var loadCallsBeforeRelease = fixture.LoadCallCount;
        var removeCallsBeforeRelease = fixture.RemoveCallCount;

        delayedBusy.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, loadCallsBeforeRelease);
        Assert.Equal(0, removeCallsBeforeRelease);
    }

    [Fact]
    public async Task Exit_is_rejected_through_switch_and_noncancelable_busy_clear()
    {
        var dispatcher = new ControllableDispatcher();
        var tracker = new ActiveOperationTracker();
        var fixture = new Fixture(dispatcher, tracker);
        await fixture.ViewModel.LoadAsync();
        fixture.Dialog.ConfirmResult = true;
        fixture.Registries.Enqueue(fixture.Registry with
        {
            ActiveAccountKey = fixture.Second.AccountKey,
        });
        var switchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var switchResult = new TaskCompletionSource<SwitchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SwitchOperation = (_, _, _) =>
        {
            switchStarted.TrySetResult();
            return switchResult.Task;
        };
        var events = new List<string>();
        var exit = CreateExitCoordinator(tracker, events);

        var running = fixture.ViewModel.SwitchCommand.ExecuteAsync(fixture.Row(fixture.Second));
        await switchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(exit.TryExit());
        var busyClear = dispatcher.DelayInvocation(2);
        switchResult.SetResult(new SwitchResult(true, "Account switch verified.", true));
        await busyClear.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(exit.TryExit());

        busyClear.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(exit.TryExit());
        Assert.Equal(
            ["rejected", "rejected", "disposed", "closed", "shutdown"],
            events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exit_is_rejected_through_dialog_registry_reload_and_busy_clear(bool login)
    {
        var dispatcher = new ControllableDispatcher();
        var tracker = new ActiveOperationTracker();
        var fixture = new Fixture(dispatcher, tracker);
        await fixture.ViewModel.LoadAsync();
        var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.LoadRegistryOperation = async cancellationToken =>
        {
            reloadStarted.TrySetResult();
            await releaseReload.Task.WaitAsync(cancellationToken);
            return fixture.Registry;
        };
        var events = new List<string>();
        var exit = CreateExitCoordinator(tracker, events);

        var running = login
            ? fixture.ViewModel.AddCommand.ExecuteAsync()
            : fixture.ViewModel.RemoveCommand.ExecuteAsync();
        await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(exit.TryExit());
        var busyClear = dispatcher.DelayInvocation(2);
        releaseReload.SetResult();
        await busyClear.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(exit.TryExit());

        busyClear.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(exit.TryExit());
        Assert.Equal(
            ["rejected", "rejected", "disposed", "closed", "shutdown"],
            events);
    }

    [Fact]
    public async Task Initial_load_owns_shared_activity_until_completion()
    {
        var tracker = new ActiveOperationTracker();
        var fixture = new Fixture(activityTracker: tracker);
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.LoadRegistryOperation = async cancellationToken =>
        {
            loadStarted.TrySetResult();
            await releaseLoad.Task.WaitAsync(cancellationToken);
            return fixture.Registry;
        };

        var running = fixture.ViewModel.LoadAsync();
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(tracker.IsActive);

        releaseLoad.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public async Task Quota_refresh_owns_shared_activity_until_completion()
    {
        var tracker = new ActiveOperationTracker();
        var fixture = new Fixture(activityTracker: tracker);
        await fixture.ViewModel.LoadAsync();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.QuotaRefreshOperation = async (_, _, cancellationToken) =>
        {
            refreshStarted.TrySetResult();
            await releaseRefresh.Task.WaitAsync(cancellationToken);
        };

        var running = fixture.ViewModel.RefreshCommand.ExecuteAsync();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(tracker.IsActive);

        releaseRefresh.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public async Task Async_command_raises_execution_notifications_only_on_awaited_dispatcher()
    {
        var dispatcher = new ControllableDispatcher();
        var fixture = new Fixture(dispatcher);
        await fixture.ViewModel.LoadAsync();
        var notificationLocations = new List<bool>();
        fixture.ViewModel.AddCommand.CanExecuteChanged += (_, _) =>
            notificationLocations.Add(dispatcher.IsDispatching);
        var delayedNotification = dispatcher.DelayNextInvocation();

        var running = fixture.ViewModel.AddCommand.ExecuteAsync();
        await delayedNotification.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var overlap = fixture.ViewModel.AddCommand.ExecuteAsync();
        await overlap.WaitAsync(TimeSpan.FromSeconds(5));

        delayedNotification.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(notificationLocations);
        Assert.All(notificationLocations, Assert.True);
        Assert.Equal(1, fixture.LoginCallCount);
    }

    [Fact]
    public async Task Canceled_confirmation_never_calls_switch_coordinator()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Dialog.ConfirmResult = false;

        await fixture.ViewModel.SwitchCommand.ExecuteAsync(fixture.Row(fixture.Second));

        Assert.Equal(0, fixture.SwitchCallCount);
        Assert.Equal(1, fixture.LoadCallCount);
    }

    [Fact]
    public async Task Confirmed_successful_switch_reloads_before_updating_active_row()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Dialog.ConfirmResult = true;
        fixture.SwitchResult = new SwitchResult(true, "Account switch verified.", true);
        fixture.Registries.Enqueue(fixture.Registry with { ActiveAccountKey = fixture.Second.AccountKey });

        await fixture.ViewModel.SwitchCommand.ExecuteAsync(fixture.Row(fixture.Second));

        Assert.Equal(1, fixture.SwitchCallCount);
        Assert.Equal(2, fixture.LoadCallCount);
        Assert.True(fixture.Row(fixture.Second).IsActive);
        Assert.False(fixture.Row(fixture.Second).CanSwitch);
        Assert.False(fixture.Row(fixture.First).IsActive);
        Assert.Equal("Account switch verified.", fixture.ViewModel.StatusText);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 7)]
    public async Task Login_reloads_registry_after_any_normal_exit(bool succeeded, int exitCode)
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.LoginOperation = (_, _) => Task.FromResult(
            new CommandResult(exitCode, string.Empty, succeeded ? string.Empty : "login failed"));
        fixture.Registries.Enqueue(fixture.Registry with
        {
            Accounts = [fixture.First, fixture.Second, fixture.Third],
        });

        await fixture.ViewModel.AddCommand.ExecuteAsync();

        Assert.Equal(1, fixture.LoginCallCount);
        Assert.Equal(2, fixture.LoadCallCount);
        Assert.Equal(3, fixture.ViewModel.Accounts.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Removal_reloads_registry_after_any_normal_exit(int exitCode)
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.RemoveOperation = _ => Task.FromResult(
            new CommandResult(exitCode, string.Empty, exitCode == 0 ? string.Empty : "remove failed"));
        fixture.Registries.Enqueue(fixture.Registry with { Accounts = [fixture.First] });

        await fixture.ViewModel.RemoveCommand.ExecuteAsync();

        Assert.Equal(1, fixture.RemoveCallCount);
        Assert.Equal(2, fixture.LoadCallCount);
        Assert.Single(fixture.ViewModel.Accounts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Login_and_removal_complete_reload_after_late_caller_cancellation(bool login)
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        using var cancellationSource = new CancellationTokenSource();
        fixture.Registries.Enqueue(fixture.Registry with
        {
            Accounts = [fixture.First, fixture.Second, fixture.Third],
        });
        fixture.LoginOperation = (_, _) =>
        {
            cancellationSource.Cancel();
            return Task.FromResult(Succeeded());
        };
        fixture.RemoveOperation = _ =>
        {
            cancellationSource.Cancel();
            return Task.FromResult(Succeeded());
        };

        var command = login ? fixture.ViewModel.AddCommand : fixture.ViewModel.RemoveCommand;
        await command.ExecuteAsync(cancellationToken: cancellationSource.Token);

        Assert.Equal(2, fixture.LoadCallCount);
        Assert.Equal(3, fixture.ViewModel.Accounts.Count);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    [Fact]
    public async Task Verified_switch_completes_reload_after_late_caller_cancellation()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        using var cancellationSource = new CancellationTokenSource();
        fixture.Dialog.ConfirmResult = true;
        fixture.SwitchResult = new SwitchResult(true, "Account switch verified.", true);
        fixture.BeforeSwitchReturn = cancellationSource.Cancel;
        fixture.Registries.Enqueue(fixture.Registry with { ActiveAccountKey = fixture.Second.AccountKey });

        await fixture.ViewModel.SwitchCommand.ExecuteAsync(
            fixture.Row(fixture.Second),
            cancellationSource.Token);

        Assert.Equal(2, fixture.LoadCallCount);
        Assert.True(fixture.Row(fixture.Second).IsActive);
        Assert.Equal("Account switch verified.", fixture.ViewModel.StatusText);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    [Fact]
    public async Task Quota_error_updates_only_affected_row_and_leaves_valid_switch_enabled()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.QuotaUpdates =
        [
            new QuotaUpdate(fixture.Second.AccountKey, null, "quota failed"),
        ];

        await fixture.ViewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal("Not queried", fixture.Row(fixture.First).QuotaLabel);
        var affected = fixture.Row(fixture.Second);
        Assert.Equal("Unavailable", affected.QuotaLabel);
        Assert.Equal("quota failed", affected.QuotaError);
        Assert.True(affected.CanSwitch);
    }

    [Fact]
    public async Task Selector_ambiguity_disables_only_the_affected_switch()
    {
        var fixture = new Fixture();
        var ambiguous = fixture.Second with { Alias = string.Empty, Email = fixture.First.Email };
        fixture.Registries.Clear();
        fixture.Registry = new AccountRegistry(3, null, [fixture.First, ambiguous, fixture.Third]);
        fixture.Registries.Enqueue(fixture.Registry);

        await fixture.ViewModel.LoadAsync();

        Assert.False(fixture.Row(ambiguous).CanSwitch);
        Assert.NotNull(fixture.Row(ambiguous).SwitchUnavailableReason);
        Assert.True(fixture.Row(fixture.Third).CanSwitch);
    }

    [Fact]
    public async Task Non_cancellation_command_exception_updates_status_and_does_not_escape()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.RemoveOperation = _ => Task.FromException<CommandResult>(
            new InvalidOperationException("unexpected removal failure"));

        await fixture.ViewModel.RemoveCommand.ExecuteAsync();

        Assert.Equal("unexpected removal failure", fixture.ViewModel.StatusText);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    [Fact]
    public async Task ICommand_execute_contains_asynchronous_failure_until_status_dispatch_completes()
    {
        var dispatcher = new ControllableDispatcher();
        var fixture = new Fixture(dispatcher);
        await fixture.ViewModel.LoadAsync();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationResult = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusUpdated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RemoveOperation = _ =>
        {
            operationStarted.TrySetResult();
            return operationResult.Task;
        };
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.StatusText) &&
                fixture.ViewModel.StatusText == "WPF command failure")
            {
                statusUpdated.TrySetResult();
            }
        };

        ((ICommand)fixture.ViewModel.RemoveCommand).Execute(null);
        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busyClearDispatch = dispatcher.DelayNextInvocation();
        operationResult.SetException(new InvalidOperationException("WPF command failure"));
        await busyClearDispatch.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var statusDispatch = dispatcher.DelayNextInvocation();
        busyClearDispatch.Release();
        await statusDispatch.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual("WPF command failure", fixture.ViewModel.StatusText);

        statusDispatch.Release();
        await statusUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("WPF command failure", fixture.ViewModel.StatusText);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    [Fact]
    public async Task Unrelated_operation_cancellation_updates_status_as_an_error()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.RemoveOperation = _ => Task.FromException<CommandResult>(
            new OperationCanceledException("internal operation canceled"));

        await fixture.ViewModel.RemoveCommand.ExecuteAsync();

        Assert.Equal("internal operation canceled", fixture.ViewModel.StatusText);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    private static CommandResult Succeeded() => new(0, string.Empty, string.Empty);

    private static ApplicationExitCoordinator CreateExitCoordinator(
        ActiveOperationTracker tracker,
        ICollection<string> events) => new(
            tracker,
            rejected: () => events.Add("rejected"),
            disposeTray: () => events.Add("disposed"),
            closeWindow: () => events.Add("closed"),
            shutdown: () => events.Add("shutdown"));

    private sealed class Fixture
    {
        public Fixture(
            IUiDispatcher? dispatcher = null,
            ActiveOperationTracker? activityTracker = null)
        {
            First = Accounts.Record("first-key", "first@example.com", "First", "first-account");
            Second = Accounts.Record("second-key", "second@example.com", "Second", "second-account");
            Third = Accounts.Record("third-key", "third@example.com", "Third", "third-account");
            Registry = new AccountRegistry(3, First.AccountKey, [First, Second]);
            Registries.Enqueue(Registry);
            Dialog = new FakeDialogService();
            Dispatcher = dispatcher ?? new ImmediateDispatcher();
            ViewModel = new MainWindowViewModel(
                LoadRegistryAsync,
                RefreshQuotaAsync,
                LoginAsync,
                RemoveAsync,
                SwitchAsync,
                Dialog,
                Dispatcher,
                activityTracker ?? new ActiveOperationTracker());
        }

        public AccountRecord First { get; }

        public AccountRecord Second { get; }

        public AccountRecord Third { get; }

        public AccountRegistry Registry { get; set; }

        public Queue<AccountRegistry> Registries { get; } = new();

        public IReadOnlyList<QuotaUpdate> QuotaUpdates { get; set; } = [];

        public Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> LoginOperation { get; set; } =
            (_, _) => Task.FromResult(Succeeded());

        public Func<CancellationToken, Task<CommandResult>> RemoveOperation { get; set; } =
            _ => Task.FromResult(Succeeded());

        public Func<CancellationToken, Task<AccountRegistry>>? LoadRegistryOperation { get; set; }

        public Func<
            IReadOnlyList<AccountRecord>,
            IProgress<QuotaUpdate>,
            CancellationToken,
            Task>? QuotaRefreshOperation { get; set; }

        public Func<
            AccountRecord,
            AccountRegistry,
            CancellationToken,
            Task<SwitchResult>>? SwitchOperation { get; set; }

        public SwitchResult SwitchResult { get; set; } = new(false, "switch failed", true);

        public Action? BeforeSwitchReturn { get; set; }

        public int LoadCallCount { get; private set; }

        public int QuotaRefreshCallCount { get; private set; }

        public int LoginCallCount { get; private set; }

        public int RemoveCallCount { get; private set; }

        public int SwitchCallCount { get; private set; }

        public FakeDialogService Dialog { get; }

        public IUiDispatcher Dispatcher { get; }

        public MainWindowViewModel ViewModel { get; }

        public AccountRowViewModel Row(AccountRecord account) =>
            Assert.Single(ViewModel.Accounts, row => row.Account.AccountKey == account.AccountKey);

        private Task<AccountRegistry> LoadRegistryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCallCount++;
            if (LoadRegistryOperation is not null)
            {
                return LoadRegistryOperation(cancellationToken);
            }

            if (Registries.Count > 0)
            {
                Registry = Registries.Dequeue();
            }

            return Task.FromResult(Registry);
        }

        private Task RefreshQuotaAsync(
            IReadOnlyList<AccountRecord> accounts,
            IProgress<QuotaUpdate> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuotaRefreshCallCount++;
            if (QuotaRefreshOperation is not null)
            {
                return QuotaRefreshOperation(accounts, progress, cancellationToken);
            }

            foreach (var update in QuotaUpdates)
            {
                progress.Report(update);
            }

            return Task.CompletedTask;
        }

        private Task<CommandResult> LoginAsync(
            ProcessOutputHandler outputHandler,
            CancellationToken cancellationToken)
        {
            LoginCallCount++;
            return LoginOperation(outputHandler, cancellationToken);
        }

        private Task<CommandResult> RemoveAsync(CancellationToken cancellationToken)
        {
            RemoveCallCount++;
            return RemoveOperation(cancellationToken);
        }

        private Task<SwitchResult> SwitchAsync(
            AccountRecord target,
            AccountRegistry before,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SwitchCallCount++;
            if (SwitchOperation is not null)
            {
                return SwitchOperation(target, before, cancellationToken);
            }

            BeforeSwitchReturn?.Invoke();
            return Task.FromResult(SwitchResult);
        }
    }

    private sealed class FakeDialogService : IAccountDialogService
    {
        public bool ConfirmResult { get; set; }

        public TaskCompletionSource LoginStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ConfirmSwitchAsync(
            AccountRowViewModel target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ConfirmResult);
        }

        public Task<CommandResult> RunLoginAsync(
            Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> operation,
            CancellationToken cancellationToken)
        {
            LoginStarted.TrySetResult();
            return operation(static (_, _) => ValueTask.CompletedTask, cancellationToken);
        }

        public Task<CommandResult> RunRemoveAsync(
            Func<CancellationToken, Task<CommandResult>> operation,
            CancellationToken cancellationToken) => operation(cancellationToken);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class ControllableDispatcher : IUiDispatcher
    {
        private DelayedInvocation? _nextInvocation;
        private int _invocationsBeforeDelay;
        private int _isDispatching;

        public bool IsDispatching => Volatile.Read(ref _isDispatching) != 0;

        public DelayedInvocation DelayNextInvocation() => DelayInvocation(1);

        public DelayedInvocation DelayInvocation(int invocationNumber)
        {
            Assert.True(invocationNumber > 0);
            var invocation = new DelayedInvocation();
            Assert.Null(Interlocked.CompareExchange(ref _nextInvocation, invocation, null));
            Volatile.Write(ref _invocationsBeforeDelay, invocationNumber - 1);
            return invocation;
        }

        public async Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            DelayedInvocation? delayed = null;
            if (Volatile.Read(ref _nextInvocation) is not null &&
                Interlocked.Decrement(ref _invocationsBeforeDelay) < 0)
            {
                delayed = Interlocked.Exchange(ref _nextInvocation, null);
            }
            if (delayed is not null)
            {
                delayed.MarkEntered();
                await delayed.WaitForReleaseAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _isDispatching);
            try
            {
                action();
            }
            finally
            {
                Interlocked.Decrement(ref _isDispatching);
            }
        }
    }

    private sealed class DelayedInvocation
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void MarkEntered() => _entered.TrySetResult();

        public void Release() => _release.TrySetResult();

        public Task WaitForReleaseAsync(CancellationToken cancellationToken) =>
            _release.Task.WaitAsync(cancellationToken);
    }
}
