using System.Windows.Input;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.ViewModels;

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

        fixture.RemoveOperation = _ => Task.FromException<CommandResult>(
            new InvalidOperationException("WPF command failure"));
        ((ICommand)fixture.ViewModel.RemoveCommand).Execute(null);
        Assert.Equal("WPF command failure", fixture.ViewModel.StatusText);
    }

    private static CommandResult Succeeded() => new(0, string.Empty, string.Empty);

    private sealed class Fixture
    {
        public Fixture()
        {
            First = Accounts.Record("first-key", "first@example.com", "First", "first-account");
            Second = Accounts.Record("second-key", "second@example.com", "Second", "second-account");
            Third = Accounts.Record("third-key", "third@example.com", "Third", "third-account");
            Registry = new AccountRegistry(3, First.AccountKey, [First, Second]);
            Registries.Enqueue(Registry);
            Dialog = new FakeDialogService();
            Dispatcher = new ImmediateDispatcher();
            ViewModel = new MainWindowViewModel(
                LoadRegistryAsync,
                RefreshQuotaAsync,
                LoginAsync,
                RemoveAsync,
                SwitchAsync,
                Dialog,
                Dispatcher);
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

        public SwitchResult SwitchResult { get; set; } = new(false, "switch failed", true);

        public int LoadCallCount { get; private set; }

        public int QuotaRefreshCallCount { get; private set; }

        public int LoginCallCount { get; private set; }

        public int RemoveCallCount { get; private set; }

        public int SwitchCallCount { get; private set; }

        public FakeDialogService Dialog { get; }

        public ImmediateDispatcher Dispatcher { get; }

        public MainWindowViewModel ViewModel { get; }

        public AccountRowViewModel Row(AccountRecord account) =>
            Assert.Single(ViewModel.Accounts, row => row.Account.AccountKey == account.AccountKey);

        private Task<AccountRegistry> LoadRegistryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCallCount++;
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
}
