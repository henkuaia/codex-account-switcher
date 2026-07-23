using System.Windows.Input;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.ViewModels;
using CodexAccountSwitcher.Views;

namespace CodexAccountSwitcher.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Dialog_service_does_not_expose_redundant_add_confirmation()
    {
        var method = typeof(IAccountDialogService).GetMethod(
            "ConfirmAddAsync",
            [typeof(CancellationToken)]);

        Assert.Null(method);
    }

    [Fact]
    public void Dialog_service_exposes_app_owned_removal_selection()
    {
        var method = typeof(IAccountDialogService).GetMethod(
            "SelectRemovalTargetAsync",
            [typeof(IReadOnlyList<AccountRowViewModel>), typeof(CancellationToken)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<AccountRowViewModel>), method.ReturnType);
    }

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
    public void Account_row_uses_email_when_alias_is_empty_even_if_account_name_is_present()
    {
        var account = new AccountRecord(
            "key",
            "account-id",
            "user-id",
            "account@example.com",
            string.Empty,
            "Account name",
            "plus",
            "chatgpt");

        var row = new AccountRowViewModel(
            account,
            isActive: false,
            canSwitch: true,
            switchUnavailableReason: null);

        Assert.Equal(account.Email, row.DisplayIdentity);
    }

    [Fact]
    public async Task Initial_load_does_not_refresh_quota()
    {
        var fixture = new Fixture();

        await fixture.ViewModel.LoadAsync();

        Assert.Equal(0, fixture.QuotaRefreshCallCount);
    }

    [Fact]
    public async Task Invalid_registry_load_keeps_empty_state_and_helper_commands_available()
    {
        var fixture = new Fixture();
        fixture.LoadRegistryOperation = _ => Task.FromException<AccountRegistry>(
            new System.IO.InvalidDataException("registry is invalid"));

        await fixture.ViewModel.LoadAsync();

        Assert.Empty(fixture.ViewModel.Accounts);
        Assert.True(fixture.ViewModel.IsHelperAvailable);
        Assert.Equal(string.Empty, fixture.ViewModel.HelperAvailabilityError);
        Assert.True(fixture.ViewModel.AddCommand.CanExecute(null));
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
        Assert.Equal("额度刷新完成。", fixture.ViewModel.StatusText);
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
    public async Task Busy_window_open_requests_coalesce_to_one_reload_after_the_active_operation_releases()
    {
        var activityTracker = new RecordingOperationTracker();
        var fixture = new Fixture(activityTracker: activityTracker);
        await fixture.ViewModel.LoadAsync();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.QuotaRefreshOperation = async (_, _, cancellationToken) =>
        {
            refreshStarted.TrySetResult();
            await releaseRefresh.Task.WaitAsync(cancellationToken);
        };
        var deferredReloadObserved = false;
        fixture.LoadRegistryOperation = cancellationToken =>
        {
            deferredReloadObserved = activityTracker.CompletedOperationCount == 2;
            return Task.FromResult(fixture.Registry);
        };

        var refresh = fixture.ViewModel.RefreshCommand.ExecuteAsync();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.WhenAll(
            fixture.ViewModel.LoadAsync(),
            fixture.ViewModel.LoadAsync(),
            fixture.ViewModel.LoadAsync());

        Assert.Equal(1, fixture.LoadCallCount);

        releaseRefresh.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, fixture.LoadCallCount);
        Assert.True(deferredReloadObserved);
    }

    [Fact]
    public async Task Faulted_busy_operation_runs_queued_reload_after_releasing_gate_and_tracker()
    {
        var activityTracker = new RecordingOperationTracker();
        var fixture = new Fixture(activityTracker: activityTracker);
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadAttempt = 0;
        var deferredReloadObserved = false;
        fixture.LoadRegistryOperation = async cancellationToken =>
        {
            if (Interlocked.Increment(ref loadAttempt) == 1)
            {
                firstLoadStarted.TrySetResult();
                await releaseFirstLoad.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("expected load failure");
            }

            deferredReloadObserved = activityTracker.CompletedOperationCount == 1;
            return fixture.Registry;
        };

        var faultedLoad = fixture.ViewModel.LoadAsync();
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            fixture.ViewModel.LoadAsync(),
            fixture.ViewModel.LoadAsync(),
            fixture.ViewModel.LoadAsync());

        releaseFirstLoad.SetResult();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => faultedLoad);

        Assert.Equal("expected load failure", exception.Message);
        Assert.Equal(2, fixture.LoadCallCount);
        Assert.True(deferredReloadObserved);
    }

    [Fact]
    public async Task Canceled_busy_operation_runs_queued_reload_after_releasing_gate_and_tracker()
    {
        var activityTracker = new RecordingOperationTracker();
        var fixture = new Fixture(activityTracker: activityTracker);
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadAttempt = 0;
        var deferredReloadObserved = false;
        fixture.LoadRegistryOperation = async cancellationToken =>
        {
            if (Interlocked.Increment(ref loadAttempt) == 1)
            {
                firstLoadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            deferredReloadObserved = activityTracker.CompletedOperationCount == 1;
            return fixture.Registry;
        };
        using var cancellationSource = new CancellationTokenSource();

        var canceledLoad = fixture.ViewModel.LoadAsync(cancellationSource.Token);
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            fixture.ViewModel.LoadAsync(),
            fixture.ViewModel.LoadAsync(),
            fixture.ViewModel.LoadAsync());

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledLoad);
        Assert.Equal(2, fixture.LoadCallCount);
        Assert.True(deferredReloadObserved);
    }

    [Fact]
    public async Task Faulted_busy_operation_preserves_its_exception_when_a_new_owner_inherits_the_queued_reload()
    {
        var activityTracker = new BlockingFirstCompletionTracker();
        var fixture = new Fixture(activityTracker: activityTracker);
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadAttempt = 0;
        fixture.LoadRegistryOperation = async cancellationToken =>
        {
            if (Interlocked.Increment(ref loadAttempt) == 1)
            {
                firstLoadStarted.TrySetResult();
                await releaseFirstLoad.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("expected load failure");
            }

            return fixture.Registry;
        };
        fixture.QuotaRefreshOperation = async (_, _, cancellationToken) =>
        {
            refreshStarted.TrySetResult();
            await releaseRefresh.Task.WaitAsync(cancellationToken);
        };

        var faultedLoad = fixture.ViewModel.LoadAsync();
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ViewModel.LoadAsync();
        releaseFirstLoad.SetResult();
        await activityTracker.FirstCompletionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var refresh = fixture.ViewModel.RefreshCommand.ExecuteAsync();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activityTracker.ReleaseFirstCompletion.SetResult();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => faultedLoad);

        Assert.Equal("expected load failure", exception.Message);
        Assert.Equal(1, fixture.LoadCallCount);

        releaseRefresh.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, fixture.LoadCallCount);
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
    public async Task Add_starts_the_single_login_window_without_pre_confirmation()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.ViewModel.AddCommand.ExecuteAsync();

        Assert.Equal(["run-login"], fixture.Dialog.AddEvents);
        Assert.Equal(1, fixture.LoginCallCount);
        Assert.Equal(2, fixture.LoadCallCount);
    }

    [Fact]
    public async Task Safe_login_result_reloads_registry_and_exposes_launch_retry()
    {
        var first = Accounts.Record("first-key", "first@example.com", "First", "first-account");
        var added = Accounts.Record("added-key", "added@example.com", "Added", "added-account");
        var registries = new Queue<AccountRegistry>(
        [
            new AccountRegistry(3, first.AccountKey, [first]),
            new AccountRegistry(3, added.AccountKey, [first, added]),
        ]);
        var loginResult = new LoginResult(
            true,
            "Account login was verified, but Codex launch failed.",
            false)
        {
            CanRetryLaunch = true,
        };
        var safeLoginCalls = 0;
        var dialog = new FakeDialogService();
        var constructor = typeof(MainWindowViewModel)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 10 &&
                    parameters[2].ParameterType == typeof(
                        Func<ProcessOutputHandler, CancellationToken, Task<LoginResult>>) &&
                    parameters[3].ParameterType == typeof(
                        Func<AccountRecord, AccountRegistry, CancellationToken, Task<RemovalResult>>);
            });

        Assert.NotNull(constructor);
        var viewModel = Assert.IsType<MainWindowViewModel>(constructor.Invoke(
        [
            (Func<CancellationToken, Task<AccountRegistry>>)(_ => Task.FromResult(registries.Dequeue())),
            (Func<IReadOnlyList<AccountRecord>, IProgress<QuotaUpdate>, CancellationToken, Task>)((_, _, _) => Task.CompletedTask),
            (Func<ProcessOutputHandler, CancellationToken, Task<LoginResult>>)((_, _) =>
            {
                safeLoginCalls++;
                return Task.FromResult(loginResult);
            }),
            (Func<AccountRecord, AccountRegistry, CancellationToken, Task<RemovalResult>>)((_, _, _) => Task.FromResult(new RemovalResult(true, "unused"))),
            (Func<AccountRecord, AccountRegistry, CancellationToken, Task<SwitchResult>>)((_, _, _) => Task.FromResult(new SwitchResult(false, "unused", true))),
            (Func<CancellationToken, Task<bool>>)(_ => Task.FromResult(true)),
            (Func<HelperAvailability>)(() => new HelperAvailability(true, @"C:\tools\codex-auth.exe", string.Empty)),
            dialog,
            new ImmediateDispatcher(),
            new ActiveOperationTracker(),
        ]));
        await viewModel.LoadAsync();

        await viewModel.AddCommand.ExecuteAsync();

        Assert.Equal(1, safeLoginCalls);
        Assert.Equal(2, viewModel.Accounts.Count);
        Assert.True(Assert.Single(viewModel.Accounts, row => row.Account.AccountKey == added.AccountKey).IsActive);
        Assert.Equal(loginResult.Message, viewModel.StatusText);
        Assert.True(viewModel.CanRetryLaunch);
        Assert.Equal(["run-login"], dialog.AddEvents);
    }

    [Fact]
    public async Task Missing_helper_disables_helper_dependent_commands_but_not_retry_launch()
    {
        var first = Accounts.Record("first-key", "first@example.com", "First", "first-account");
        var second = Accounts.Record("second-key", "second@example.com", "Second", "second-account");
        var registry = new AccountRegistry(3, first.AccountKey, [first, second]);
        const string expectedPath = @"C:\expected\tools\codex-auth.exe";
        var availability = new HelperAvailability(
            false,
            expectedPath,
            $"The codex-auth helper is unavailable at the expected path: {expectedPath}");
        var constructor = typeof(MainWindowViewModel)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 10 &&
                    parameters[2].ParameterType == typeof(
                        Func<ProcessOutputHandler, CancellationToken, Task<LoginResult>>) &&
                    parameters[3].ParameterType == typeof(
                        Func<AccountRecord, AccountRegistry, CancellationToken, Task<RemovalResult>>);
            });
        var viewModel = Assert.IsType<MainWindowViewModel>(constructor.Invoke(
        [
            (Func<CancellationToken, Task<AccountRegistry>>)(_ => Task.FromResult(registry)),
            (Func<IReadOnlyList<AccountRecord>, IProgress<QuotaUpdate>, CancellationToken, Task>)((_, _, _) => Task.CompletedTask),
            (Func<ProcessOutputHandler, CancellationToken, Task<LoginResult>>)((_, _) => Task.FromResult(new LoginResult(false, "unused", true))),
            (Func<AccountRecord, AccountRegistry, CancellationToken, Task<RemovalResult>>)((_, _, _) => Task.FromResult(new RemovalResult(true, "unused"))),
            (Func<AccountRecord, AccountRegistry, CancellationToken, Task<SwitchResult>>)((_, _, _) => Task.FromResult(new SwitchResult(false, "unused", true))),
            (Func<CancellationToken, Task<bool>>)(_ => Task.FromResult(true)),
            (Func<HelperAvailability>)(() => availability),
            new FakeDialogService(),
            new ImmediateDispatcher(),
            new ActiveOperationTracker(),
        ]));

        await viewModel.LoadAsync();
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.CanRetryLaunch))!
            .SetValue(viewModel, true);

        Assert.False(viewModel.AddCommand.CanExecute(null));
        Assert.False(viewModel.RemoveCommand.CanExecute(null));
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.False(viewModel.SwitchCommand.CanExecute(
            Assert.Single(viewModel.Accounts, row => row.Account.AccountKey == second.AccountKey)));
        Assert.True(viewModel.RetryLaunchCommand.CanExecute(null));
        Assert.Contains(expectedPath, viewModel.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("refresh")]
    [InlineData("switch")]
    public async Task Helper_dependent_commands_recheck_availability_before_any_side_effect(
        string command)
    {
        var fixture = new DynamicAvailabilityFixture();
        await fixture.ViewModel.LoadAsync();
        var switchTarget = fixture.Row(fixture.Second);
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.CanRetryLaunch))!
            .SetValue(fixture.ViewModel, true);
        fixture.Availability = fixture.MissingAvailability;

        await fixture.ExecuteAsync(command, switchTarget);

        Assert.Equal(0, fixture.Dialog.SelectRemovalTargetCallCount);
        Assert.Equal(0, fixture.Dialog.ConfirmSwitchCallCount);
        Assert.Equal(0, fixture.Dialog.RunLoginCallCount);
        Assert.Equal(0, fixture.LoginCallCount);
        Assert.Equal(0, fixture.RemoveCallCount);
        Assert.Equal(0, fixture.QuotaRefreshCallCount);
        Assert.Equal(0, fixture.SwitchCallCount);
        Assert.Equal(1, fixture.LoadCallCount);
        Assert.False(fixture.ViewModel.IsHelperAvailable);
        Assert.Equal(fixture.MissingAvailability.Error, fixture.ViewModel.HelperAvailabilityError);
        Assert.Equal(fixture.MissingAvailability.Error, fixture.ViewModel.StatusText);
        Assert.False(fixture.ViewModel.AddCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.RemoveCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.RefreshCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.SwitchCommand.CanExecute(switchTarget));
        Assert.True(fixture.ViewModel.RetryLaunchCommand.CanExecute(null));
    }

    [Fact]
    public async Task Load_reenables_helper_dependent_commands_after_helper_is_restored()
    {
        var fixture = new DynamicAvailabilityFixture();
        await fixture.ViewModel.LoadAsync();
        var switchTarget = fixture.Row(fixture.Second);
        fixture.Availability = fixture.MissingAvailability;
        await fixture.ViewModel.RefreshCommand.ExecuteAsync();

        fixture.Availability = fixture.AvailableAvailability;
        await fixture.ViewModel.LoadAsync();

        Assert.True(fixture.ViewModel.IsHelperAvailable);
        Assert.Equal(string.Empty, fixture.ViewModel.HelperAvailabilityError);
        Assert.True(fixture.ViewModel.AddCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.RemoveCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.RefreshCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.SwitchCommand.CanExecute(switchTarget));
    }

    [Fact]
    public async Task Helper_recovery_clears_the_prior_helper_error_status()
    {
        var fixture = new DynamicAvailabilityFixture();
        fixture.Availability = fixture.MissingAvailability;
        await fixture.ViewModel.LoadAsync();

        fixture.Availability = fixture.AvailableAvailability;
        await fixture.ViewModel.LoadAsync();

        Assert.Equal(string.Empty, fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task Helper_recovery_preserves_an_unrelated_status_message()
    {
        var fixture = new DynamicAvailabilityFixture();
        fixture.Availability = fixture.MissingAvailability;
        await fixture.ViewModel.LoadAsync();
        fixture.RetryLaunchResult = false;
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.CanRetryLaunch))!
            .SetValue(fixture.ViewModel, true);
        await fixture.ViewModel.RetryLaunchCommand.ExecuteAsync();

        fixture.Availability = fixture.AvailableAvailability;
        await fixture.ViewModel.LoadAsync();

        Assert.Equal("Codex launch retry failed.", fixture.ViewModel.StatusText);
    }

    [Theory]
    [InlineData("login", "returned failure")]
    [InlineData("login", "operational start failure")]
    [InlineData("remove", "returned failure")]
    [InlineData("remove", "operational start failure")]
    [InlineData("switch", "returned failure")]
    [InlineData("switch", "operational start failure")]
    public async Task Structured_missing_helper_result_disables_commands_after_dialog(
        string operation,
        string resultMessage)
    {
        var fixture = new DynamicAvailabilityFixture();
        await fixture.ViewModel.LoadAsync();
        var switchTarget = fixture.Row(fixture.Second);
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.CanRetryLaunch))!
            .SetValue(fixture.ViewModel, true);
        fixture.Dialog.AfterDialog = () => fixture.Availability = fixture.MissingAvailability;
        fixture.LoginResult = WithHelperAvailability(
            new LoginResult(false, resultMessage, true),
            fixture.MissingAvailability);
        fixture.RemovalResult = WithHelperAvailability(
            new RemovalResult(false, resultMessage),
            fixture.MissingAvailability);
        fixture.SwitchResult = WithHelperAvailability(
            new SwitchResult(false, resultMessage, true),
            fixture.MissingAvailability);

        await fixture.ExecuteAsync(operation, switchTarget);

        Assert.Equal(operation == "login" ? 0 : 1, fixture.Dialog.TotalConfirmationOrSelectionCalls);
        Assert.Equal(operation == "login" ? 1 : 0, fixture.Dialog.RunLoginCallCount);
        Assert.Equal(operation == "login" ? 1 : 0, fixture.LoginCallCount);
        Assert.Equal(operation == "remove" ? 1 : 0, fixture.RemoveCallCount);
        Assert.Equal(operation == "switch" ? 1 : 0, fixture.SwitchCallCount);
        Assert.Equal(operation is "login" or "remove" ? 2 : 1, fixture.LoadCallCount);
        Assert.False(fixture.ViewModel.IsHelperAvailable);
        Assert.Equal(fixture.MissingAvailability.Error, fixture.ViewModel.HelperAvailabilityError);
        Assert.Equal(fixture.MissingAvailability.Error, fixture.ViewModel.StatusText);
        Assert.False(fixture.ViewModel.AddCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.RemoveCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.RefreshCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.SwitchCommand.CanExecute(switchTarget));
        Assert.True(fixture.ViewModel.RetryLaunchCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("login")]
    [InlineData("remove")]
    public async Task Login_and_removal_reload_registry_when_helper_becomes_unavailable_after_operation(
        string operation)
    {
        var fixture = new DynamicAvailabilityFixture();
        await fixture.ViewModel.LoadAsync();
        var switchTarget = fixture.Row(fixture.Second);
        fixture.Dialog.AfterDialog = () => fixture.Availability = fixture.MissingAvailability;
        fixture.LoginResult = WithHelperAvailability(
            new LoginResult(false, "login failed", true),
            fixture.MissingAvailability);
        fixture.RemovalResult = WithHelperAvailability(
            new RemovalResult(false, "remove failed"),
            fixture.MissingAvailability);

        await fixture.ExecuteAsync(operation, switchTarget);

        Assert.Equal(2, fixture.LoadCallCount);
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
    public async Task Login_and_removal_invalid_registry_reload_replaces_existing_rows_with_empty_state(
        bool login)
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.LoadRegistryOperation = _ => Task.FromException<AccountRegistry>(
            new System.IO.InvalidDataException("registry is invalid"));

        var command = login ? fixture.ViewModel.AddCommand : fixture.ViewModel.RemoveCommand;
        await command.ExecuteAsync();

        Assert.Equal(2, fixture.LoadCallCount);
        Assert.Empty(fixture.ViewModel.Accounts);
        Assert.True(fixture.ViewModel.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task Canceled_removal_selection_never_calls_remove_or_reloads_registry()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Dialog.CancelRemovalSelection = true;

        await fixture.ViewModel.RemoveCommand.ExecuteAsync();

        Assert.Equal(0, fixture.RemoveCallCount);
        Assert.Equal(1, fixture.LoadCallCount);
    }

    [Fact]
    public async Task Product_remove_targets_selected_non_active_account_without_quota_refresh()
    {
        var first = Accounts.Record("first-key", "first@example.com", "First", "first-account");
        var second = Accounts.Record("second-key", "second@example.com", "Second", "second-account");
        var third = Accounts.Record("third-key", "third@example.com", "Third", "third-account");
        var before = new AccountRegistry(3, first.AccountKey, [first, second, third]);
        var after = new AccountRegistry(3, first.AccountKey, [first, third]);
        var registries = new Queue<AccountRegistry>([before, after]);
        var dialog = new FakeDialogService();
        var quotaRefreshCalls = 0;
        var removalCalls = 0;
        AccountRecord? removedTarget = null;
        AccountRegistry? observedRegistry = null;
        var constructor = typeof(MainWindowViewModel)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 10 &&
                    parameters[3].ParameterType == typeof(
                        Func<AccountRecord, AccountRegistry, CancellationToken, Task<RemovalResult>>);
            });

        Assert.NotNull(constructor);
        var viewModel = Assert.IsType<MainWindowViewModel>(constructor.Invoke(
        [
            (Func<CancellationToken, Task<AccountRegistry>>)(_ => Task.FromResult(registries.Dequeue())),
            (Func<IReadOnlyList<AccountRecord>, IProgress<QuotaUpdate>, CancellationToken, Task>)((_, _, _) =>
            {
                quotaRefreshCalls++;
                return Task.CompletedTask;
            }),
            (Func<ProcessOutputHandler, CancellationToken, Task<LoginResult>>)((_, _) => Task.FromResult(new LoginResult(false, "unused", true))),
            (Func<AccountRecord, AccountRegistry, CancellationToken, Task<RemovalResult>>)((target, registry, _) =>
            {
                removalCalls++;
                removedTarget = target;
                observedRegistry = registry;
                return Task.FromResult(new RemovalResult(true, "Account removal verified."));
            }),
            (Func<AccountRecord, AccountRegistry, CancellationToken, Task<SwitchResult>>)((_, _, _) => Task.FromResult(new SwitchResult(false, "unused", true))),
            (Func<CancellationToken, Task<bool>>)(_ => Task.FromResult(true)),
            (Func<HelperAvailability>)(() => new HelperAvailability(true, @"C:\tools\codex-auth.exe", string.Empty)),
            dialog,
            new ImmediateDispatcher(),
            new ActiveOperationTracker(),
        ]));
        await viewModel.LoadAsync();
        dialog.RemovalTarget = Assert.Single(
            viewModel.Accounts,
            row => row.Account.AccountKey == second.AccountKey);

        await viewModel.RemoveCommand.ExecuteAsync();

        Assert.Equal(1, removalCalls);
        Assert.Same(second, removedTarget);
        Assert.Equal(before, observedRegistry);
        Assert.Equal(0, quotaRefreshCalls);
        Assert.DoesNotContain(viewModel.Accounts, row => row.Account.AccountKey == second.AccountKey);
        Assert.Equal("Account removal verified.", viewModel.StatusText);
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
    public async Task Failed_switch_dispatches_structured_result_after_late_caller_cancellation()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        using var cancellationSource = new CancellationTokenSource();
        fixture.Dialog.ConfirmResult = true;
        fixture.SwitchResult = new SwitchResult(
            false,
            "The prior authentication state was restored, but Codex launch failed.",
            false);
        fixture.BeforeSwitchReturn = cancellationSource.Cancel;

        await fixture.ViewModel.SwitchCommand.ExecuteAsync(
            fixture.Row(fixture.Second),
            cancellationSource.Token);

        Assert.Equal(
            "The prior authentication state was restored, but Codex launch failed.",
            fixture.ViewModel.StatusText);
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
    public async Task Registry_reloads_preserve_quota_by_account_key_and_refresh_row_identity()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        var display = new QuotaDisplay(
            QuotaPeriod.Weekly,
            73,
            DateTimeOffset.Parse("2026-07-25T12:34:00Z"),
            TimeSpan.FromDays(7),
            "weekly reset");
        fixture.QuotaUpdates =
        [
            new QuotaUpdate(fixture.First.AccountKey, display, null),
            new QuotaUpdate(fixture.Second.AccountKey, null, "quota failed"),
        ];
        await fixture.ViewModel.RefreshCommand.ExecuteAsync();
        var renamedFirst = fixture.First with { Alias = "Renamed first" };
        var renamedSecond = fixture.Second with { AccountName = "Renamed second" };
        fixture.Registries.Enqueue(new AccountRegistry(
            3,
            renamedSecond.AccountKey,
            [renamedFirst, renamedSecond, fixture.Third]));

        await fixture.ViewModel.LoadAsync();

        Assert.Equal(display, fixture.Row(renamedFirst).QuotaDisplay);
        Assert.Equal("Renamed first", fixture.Row(renamedFirst).DisplayIdentity);
        Assert.Equal("quota failed", fixture.Row(renamedSecond).QuotaError);
        Assert.True(fixture.Row(renamedSecond).IsActive);
        Assert.Equal("Not queried", fixture.Row(fixture.Third).QuotaLabel);

        fixture.Registries.Enqueue(new AccountRegistry(
            3,
            fixture.Third.AccountKey,
            [renamedFirst, fixture.Third]));

        await fixture.ViewModel.LoadAsync();

        Assert.DoesNotContain(
            fixture.ViewModel.Accounts,
            row => row.Account.AccountKey == renamedSecond.AccountKey);
        Assert.Equal(display, fixture.Row(renamedFirst).QuotaDisplay);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("remove")]
    [InlineData("switch")]
    public async Task Account_operations_preserve_existing_quota_state(string operation)
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        var display = new QuotaDisplay(
            QuotaPeriod.Monthly,
            41,
            null,
            TimeSpan.FromDays(30),
            "monthly");
        fixture.QuotaUpdates = [new QuotaUpdate(fixture.First.AccountKey, display, null)];
        await fixture.ViewModel.RefreshCommand.ExecuteAsync();
        fixture.Registries.Enqueue(fixture.Registry with
        {
            ActiveAccountKey = operation == "switch"
                ? fixture.Second.AccountKey
                : fixture.First.AccountKey,
        });
        fixture.Dialog.ConfirmResult = true;
        fixture.SwitchResult = new SwitchResult(true, "Account switch verified.", true);

        if (operation == "login")
        {
            await fixture.ViewModel.AddCommand.ExecuteAsync();
        }
        else if (operation == "remove")
        {
            await fixture.ViewModel.RemoveCommand.ExecuteAsync();
        }
        else
        {
            await fixture.ViewModel.SwitchCommand.ExecuteAsync(fixture.Row(fixture.Second));
        }

        Assert.Equal(display, fixture.Row(fixture.First).QuotaDisplay);
    }

    [Fact]
    public void Quota_row_exposes_exact_reset_status_error_and_tooltip()
    {
        var row = new AccountRowViewModel(
            Accounts.Record("key", "first@example.com"),
            isActive: false,
            canSwitch: true,
            switchUnavailableReason: null);
        var reset = DateTimeOffset.Parse("2026-07-25T12:34:00Z");
        row.ApplyQuota(new QuotaUpdate(
            row.Account.AccountKey,
            new QuotaDisplay(
                QuotaPeriod.Unknown,
                42,
                reset,
                TimeSpan.FromDays(12),
                "Unknown: 42% remaining; reset 2026-07-25 12:34 UTC"),
            null));

        Assert.Equal(
            "Resets 2026-07-25 12:34 UTC",
            RequiredProperty<string>(row, "QuotaStatusText"));
        Assert.Contains(
            "Unknown",
            RequiredProperty<string>(row, "QuotaToolTip"),
            StringComparison.Ordinal);
        Assert.True(RequiredProperty<bool>(row, "HasQuotaStatus"));

        row.ApplyQuota(new QuotaUpdate(row.Account.AccountKey, null, "quota failed (HTTP 403)."));

        Assert.Equal("quota failed (HTTP 403).", RequiredProperty<string>(row, "QuotaStatusText"));
        Assert.Equal("quota failed (HTTP 403).", RequiredProperty<string>(row, "QuotaToolTip"));
    }

    [Fact]
    public void Account_row_formats_server_reset_snapshot_and_local_metadata_separately()
    {
        var account = Accounts.Record("first-key", "first@example.com");
        var row = new AccountRowViewModel(
            account,
            isActive: true,
            canSwitch: false,
            switchUnavailableReason: null);
        row.ApplyMetadata(new AccountMetadata(40m, 3));
        row.ApplyQuota(new QuotaUpdate(
            account.AccountKey,
            new QuotaDisplay(
                QuotaPeriod.Weekly,
                73,
                null,
                TimeSpan.FromDays(7),
                "weekly")
            {
                AvailableResetCount = 2,
                IndividualLimitUsd = 200m,
            },
            null));

        Assert.Equal("可用重置 2", row.AvailableResetText);
        Assert.Equal("已用重置 3（本机）", row.UsedResetText);
        Assert.Equal("单次周额度 US$40", row.PeriodQuotaText);
        Assert.Equal("官方月度上限 US$200", row.OfficialMonthlyLimitText);
        Assert.True(row.HasOfficialMonthlyLimit);
    }

    [Fact]
    public void Missing_server_and_local_values_are_not_presented_as_zero()
    {
        var account = Accounts.Record("first-key", "first@example.com");
        var row = new AccountRowViewModel(
            account,
            isActive: true,
            canSwitch: false,
            switchUnavailableReason: null);
        row.ApplyMetadata(new AccountMetadata(null, 0));

        Assert.Equal("可用重置 —", row.AvailableResetText);
        Assert.Equal("已用重置 0（本机）", row.UsedResetText);
        Assert.Equal("单次额度 —", row.PeriodQuotaText);
        Assert.False(row.HasOfficialMonthlyLimit);
    }

    [Fact]
    public async Task Metadata_load_and_edit_are_isolated_by_account_key_and_saved_before_display()
    {
        var first = Accounts.Record("first-key", "first@example.com");
        var second = Accounts.Record("second-key", "second@example.com");
        var registry = new AccountRegistry(3, first.AccountKey, [first, second]);
        var dialog = new FakeDialogService
        {
            MetadataResult = new AccountMetadata(60m, 4),
        };
        IReadOnlyDictionary<string, AccountMetadata>? saved = null;
        var viewModel = new MainWindowViewModel(
            _ => Task.FromResult(registry),
            (_, _, _) => Task.CompletedTask,
            (_, _) => Task.FromResult(new LoginResult(false, "unused", true)),
            (_, _, _) => Task.FromResult(new RemovalResult(false, "unused")),
            (_, _, _) => Task.FromResult(new SwitchResult(false, "unused", true)),
            _ => Task.FromResult(true),
            () => new HelperAvailability(true, "codex-auth.exe", string.Empty),
            dialog,
            new ImmediateDispatcher(),
            new ActiveOperationTracker(),
            _ => Task.FromResult(new AccountMetadataLoadResult(
                new Dictionary<string, AccountMetadata>
                {
                    [second.AccountKey] = new AccountMetadata(40m, 3),
                },
                null)),
            (metadata, _) =>
            {
                saved = new Dictionary<string, AccountMetadata>(metadata);
                return Task.CompletedTask;
            });

        await viewModel.LoadAsync();
        var firstRow = Assert.Single(viewModel.Accounts, row => row.Account.AccountKey == first.AccountKey);
        var secondRow = Assert.Single(viewModel.Accounts, row => row.Account.AccountKey == second.AccountKey);
        Assert.Equal("单次额度 —", firstRow.PeriodQuotaText);
        Assert.Equal("单次额度 US$40", secondRow.PeriodQuotaText);

        await viewModel.EditMetadataCommand.ExecuteAsync(secondRow);

        Assert.NotNull(saved);
        Assert.Equal(new AccountMetadata(60m, 4), saved[second.AccountKey]);
        Assert.Equal("单次额度 US$60", secondRow.PeriodQuotaText);
        Assert.Equal("已用重置 4（本机）", secondRow.UsedResetText);
        Assert.Equal("额度记录已保存。", viewModel.StatusText);
    }

    [Theory]
    [InlineData("success", "Codex launched.", false)]
    [InlineData("failure", "Codex launch retry failed.", true)]
    [InlineData("exception", "Codex launch retry failed.", true)]
    public async Task Retry_launch_is_conditional_launch_only_and_uses_shared_operation_state(
        string outcome,
        string expectedStatus,
        bool expectedCanRetry)
    {
        var first = Accounts.Record("first-key", "first@example.com", "First", "first-account");
        var second = Accounts.Record("second-key", "second@example.com", "Second", "second-account");
        var registry = new AccountRegistry(3, first.AccountKey, [first, second]);
        var launchFailure = new SwitchResult(true, "Account switch was verified, but Codex launch failed.", false);
        var retryProperty = typeof(SwitchResult).GetProperty("CanRetryLaunch");
        Assert.NotNull(retryProperty);
        retryProperty.SetValue(launchFailure, true);
        var dialog = new FakeDialogService { ConfirmResult = true };
        var tracker = new ActiveOperationTracker();
        MainWindowViewModel? viewModel = null;
        var retryCalls = 0;
        var observedBusy = false;
        var observedActivity = false;
        Func<CancellationToken, Task<bool>> retryLaunchAsync = _ =>
        {
            retryCalls++;
            observedBusy = viewModel!.IsBusy;
            observedActivity = tracker.IsActive;
            return outcome == "exception"
                ? Task.FromException<bool>(new InvalidOperationException("raw launch secret"))
                : Task.FromResult(outcome == "success");
        };
        var constructor = typeof(MainWindowViewModel)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 9 &&
                    parameters[5].ParameterType == typeof(Func<CancellationToken, Task<bool>>);
            });
        Assert.NotNull(constructor);
        viewModel = Assert.IsType<MainWindowViewModel>(constructor.Invoke(
        [
            (Func<CancellationToken, Task<AccountRegistry>>)(_ => Task.FromResult(registry)),
            (Func<IReadOnlyList<AccountRecord>, IProgress<QuotaUpdate>, CancellationToken, Task>)((_, _, _) => Task.CompletedTask),
            (Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>>)((_, _) => Task.FromResult(Succeeded())),
            (Func<CancellationToken, Task<CommandResult>>)(_ => Task.FromResult(Succeeded())),
            (Func<AccountRecord, AccountRegistry, CancellationToken, Task<SwitchResult>>)((_, _, _) => Task.FromResult(launchFailure)),
            retryLaunchAsync,
            dialog,
            new ImmediateDispatcher(),
            tracker,
        ]));
        await viewModel.LoadAsync();

        await viewModel.SwitchCommand.ExecuteAsync(Assert.Single(
            viewModel.Accounts,
            row => row.Account.AccountKey == second.AccountKey));

        Assert.True(RequiredProperty<bool>(viewModel, "CanRetryLaunch"));
        var command = RequiredProperty<AsyncCommand>(viewModel, "RetryLaunchCommand");
        Assert.True(command.CanExecute(null));

        await command.ExecuteAsync();

        Assert.Equal(1, retryCalls);
        Assert.True(observedBusy);
        Assert.True(observedActivity);
        Assert.Equal(expectedStatus, viewModel.StatusText);
        Assert.DoesNotContain("raw launch secret", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(expectedCanRetry, RequiredProperty<bool>(viewModel, "CanRetryLaunch"));
        Assert.False(viewModel.IsBusy);
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

    private static T WithHelperAvailability<T>(T result, HelperAvailability availability)
        where T : notnull
    {
        var property = typeof(T).GetProperty("HelperAvailability");
        Assert.NotNull(property);
        property.SetValue(result, availability);
        return result;
    }

    private static T RequiredProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(instance));
    }

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
            IOperationActivityTracker? activityTracker = null)
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
                _ => Task.FromResult(true),
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

    private sealed class DynamicAvailabilityFixture
    {
        public DynamicAvailabilityFixture()
        {
            First = Accounts.Record("first-key", "first@example.com", "First", "first-account");
            Second = Accounts.Record("second-key", "second@example.com", "Second", "second-account");
            Registry = new AccountRegistry(3, First.AccountKey, [First, Second]);
            Dialog = new RecordingDialogService();
            ViewModel = new MainWindowViewModel(
                LoadRegistryAsync,
                RefreshQuotaAsync,
                LoginAsync,
                RemoveAsync,
                SwitchAsync,
                _ => Task.FromResult(RetryLaunchResult),
                () => Availability,
                Dialog,
                new ImmediateDispatcher(),
                new ActiveOperationTracker());
        }

        public HelperAvailability AvailableAvailability { get; } =
            new(true, @"C:\expected\tools\codex-auth.exe", string.Empty);

        public HelperAvailability MissingAvailability { get; } = new(
            false,
            @"C:\expected\tools\codex-auth.exe",
            @"The codex-auth helper is unavailable at the expected path: C:\expected\tools\codex-auth.exe");

        public HelperAvailability Availability { get; set; } =
            new(true, @"C:\expected\tools\codex-auth.exe", string.Empty);

        public AccountRecord First { get; }

        public AccountRecord Second { get; }

        public AccountRegistry Registry { get; }

        public LoginResult LoginResult { get; set; } = new(true, "login completed", true);

        public RemovalResult RemovalResult { get; set; } = new(true, "removal completed");

        public SwitchResult SwitchResult { get; set; } = new(false, "switch failed", true);

        public bool RetryLaunchResult { get; set; } = true;

        public int LoadCallCount { get; private set; }

        public int QuotaRefreshCallCount { get; private set; }

        public int LoginCallCount { get; private set; }

        public int RemoveCallCount { get; private set; }

        public int SwitchCallCount { get; private set; }

        public RecordingDialogService Dialog { get; }

        public MainWindowViewModel ViewModel { get; }

        public AccountRowViewModel Row(AccountRecord account) =>
            Assert.Single(ViewModel.Accounts, row => row.Account.AccountKey == account.AccountKey);

        public Task ExecuteAsync(string operation, AccountRowViewModel switchTarget) => operation switch
        {
            "add" or "login" => ViewModel.AddCommand.ExecuteAsync(),
            "remove" => ViewModel.RemoveCommand.ExecuteAsync(),
            "refresh" => ViewModel.RefreshCommand.ExecuteAsync(),
            "switch" => ViewModel.SwitchCommand.ExecuteAsync(switchTarget),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        private Task<AccountRegistry> LoadRegistryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCallCount++;
            return Task.FromResult(Registry);
        }

        private Task RefreshQuotaAsync(
            IReadOnlyList<AccountRecord> accounts,
            IProgress<QuotaUpdate> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuotaRefreshCallCount++;
            return Task.CompletedTask;
        }

        private Task<LoginResult> LoginAsync(
            ProcessOutputHandler outputHandler,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoginCallCount++;
            return Task.FromResult(LoginResult);
        }

        private Task<RemovalResult> RemoveAsync(
            AccountRecord target,
            AccountRegistry before,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveCallCount++;
            return Task.FromResult(RemovalResult);
        }

        private Task<SwitchResult> SwitchAsync(
            AccountRecord target,
            AccountRegistry before,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SwitchCallCount++;
            return Task.FromResult(SwitchResult);
        }
    }

    private sealed class RecordingDialogService : IAccountDialogService
    {
        public Action? AfterDialog { get; set; }

        public int SelectRemovalTargetCallCount { get; private set; }

        public int ConfirmSwitchCallCount { get; private set; }

        public int RunLoginCallCount { get; private set; }

        public int TotalConfirmationOrSelectionCalls =>
            SelectRemovalTargetCallCount + ConfirmSwitchCallCount;

        public Task<AccountRowViewModel?> SelectRemovalTargetAsync(
            IReadOnlyList<AccountRowViewModel> accounts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SelectRemovalTargetCallCount++;
            AfterDialog?.Invoke();
            return Task.FromResult(accounts.FirstOrDefault(account => !account.IsActive));
        }

        public Task<AccountMetadata?> EditMetadataAsync(
            AccountRowViewModel target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AccountMetadata?>(null);
        }

        public Task<bool> ConfirmSwitchAsync(
            AccountRowViewModel target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmSwitchCallCount++;
            AfterDialog?.Invoke();
            return Task.FromResult(true);
        }

        public Task<CommandResult> RunLoginAsync(
            Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> operation,
            CancellationToken cancellationToken)
        {
            RunLoginCallCount++;
            return operation(static (_, _) => ValueTask.CompletedTask, cancellationToken);
        }

        public Task<CommandResult> RunRemoveAsync(
            Func<CancellationToken, Task<CommandResult>> operation,
            CancellationToken cancellationToken) => operation(cancellationToken);
    }

    private sealed class FakeDialogService : IAccountDialogService
    {
        public bool ConfirmResult { get; set; }

        public List<string> AddEvents { get; } = [];

        public AccountRowViewModel? RemovalTarget { get; set; }

        public AccountMetadata? MetadataResult { get; set; }

        public bool CancelRemovalSelection { get; set; }

        public TaskCompletionSource LoginStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AccountRowViewModel?> SelectRemovalTargetAsync(
            IReadOnlyList<AccountRowViewModel> accounts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CancelRemovalSelection
                ? null
                : RemovalTarget ?? accounts.FirstOrDefault(account => !account.IsActive));
        }

        public Task<AccountMetadata?> EditMetadataAsync(
            AccountRowViewModel target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(MetadataResult);
        }

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
            AddEvents.Add("run-login");
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

    private sealed class RecordingOperationTracker : IOperationActivityTracker
    {
        private int _activeCount;
        private int _completedOperationCount;

        public bool IsActive => Volatile.Read(ref _activeCount) != 0;

        public int CompletedOperationCount => Volatile.Read(ref _completedOperationCount);

        public IDisposable Begin()
        {
            Interlocked.Increment(ref _activeCount);
            return new Activity(this);
        }

        private sealed class Activity(RecordingOperationTracker owner) : IDisposable
        {
            private RecordingOperationTracker? _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                {
                    Interlocked.Decrement(ref owner._activeCount);
                    Interlocked.Increment(ref owner._completedOperationCount);
                }
            }
        }
    }

    private sealed class BlockingFirstCompletionTracker : IOperationActivityTracker
    {
        private int _completionCount;

        public TaskCompletionSource FirstCompletionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsActive => false;

        public IDisposable Begin() => new Activity(this);

        private sealed class Activity(BlockingFirstCompletionTracker owner) : IDisposable
        {
            private BlockingFirstCompletionTracker? _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is null || Interlocked.Increment(ref owner._completionCount) != 1)
                {
                    return;
                }

                owner.FirstCompletionEntered.TrySetResult();
                owner.ReleaseFirstCompletion.Task.GetAwaiter().GetResult();
            }
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
