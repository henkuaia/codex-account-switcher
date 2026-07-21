using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class SafeSwitchCoordinatorTests
{
    [Fact]
    public async Task Successful_switch_captures_switches_verifies_and_launches_in_order()
    {
        var fixture = new Fixture();

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.True(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(
            ["close", "capture", "switch:target", "reload", "verify-auth", "launch"],
            fixture.Operations);
    }

    [Fact]
    public async Task Failed_helper_restores_prior_bytes_then_relaunches_without_success()
    {
        var fixture = new Fixture
        {
            SwitchResult = CommandResult.Failed("simulated failure"),
        };

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal("prior", fixture.AuthStateMarker);
        Assert.Equal(["close", "capture", "switch:target", "restore", "launch"], fixture.Operations);
    }

    [Fact]
    public async Task Registry_verification_mismatch_restores_and_relaunches_prior_state()
    {
        var fixture = new Fixture();
        fixture.RegistryAfterSwitch = fixture.Registry;

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(
            ["close", "capture", "switch:target", "reload", "verify-auth", "restore", "launch"],
            fixture.Operations);
    }

    [Fact]
    public async Task Auth_account_id_mismatch_restores_and_relaunches_prior_state()
    {
        var fixture = new Fixture
        {
            AuthAccountIdAfterSwitch = "wrong-account",
        };

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal("restore", fixture.Operations[^2]);
        Assert.Equal("launch", fixture.Operations[^1]);
    }

    [Fact]
    public async Task Close_timeout_forces_exact_returned_ids_before_capture_and_switch()
    {
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41, 73]);

        await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.Equal([41, 73], fixture.ProcessController.ForcedProcessIds);
        Assert.Equal(
            ["close", "force:41,73", "capture", "switch:target", "reload", "verify-auth", "launch"],
            fixture.Operations);
        Assert.Equal(TimeSpan.FromSeconds(8), fixture.ProcessController.CloseTimeout);
    }

    [Fact]
    public async Task Already_active_target_is_successful_no_op()
    {
        var fixture = new Fixture();
        var activeRegistry = fixture.Registry with { ActiveAccountKey = fixture.Target.AccountKey };

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, activeRegistry, default);

        Assert.True(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Empty(fixture.Operations);
    }

    [Fact]
    public async Task Unavailable_selector_returns_before_closing_codex()
    {
        var fixture = new Fixture();
        var duplicate = Accounts.Record("duplicate", fixture.Target.Email, fixture.Target.Alias, "other-account");
        var registry = fixture.Registry with { Accounts = [fixture.Prior, fixture.Target, duplicate] };

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, registry, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Empty(fixture.Operations);
    }

    [Fact]
    public async Task Pre_cancelled_request_throws_without_side_effects()
    {
        var fixture = new Fixture();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, cancellationSource.Token));

        Assert.Empty(fixture.Operations);
    }

    [Fact]
    public async Task Cancellation_thrown_after_close_side_effect_relaunches_without_checkpoint()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseCallback = cancellationSource.Cancel;
        fixture.ProcessController.CloseException = new OperationCanceledException(cancellationSource.Token);

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(["close", "launch"], fixture.Operations);
        Assert.False(fixture.ProcessController.LaunchToken.CanBeCanceled);
    }

    [Fact]
    public async Task Cancellation_thrown_during_force_relaunches_without_checkpoint()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41, 73]);
        fixture.ProcessController.ForceCallback = cancellationSource.Cancel;
        fixture.ProcessController.ForceException = new OperationCanceledException(cancellationSource.Token);

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(["close", "force:41,73", "launch"], fixture.Operations);
        Assert.False(fixture.ProcessController.LaunchToken.CanBeCanceled);
    }

    [Fact]
    public async Task Cancellation_after_close_restores_and_launches_with_recovery_tokens()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture
        {
            CancelAfterClose = cancellationSource,
        };

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(["close", "capture", "restore", "launch"], fixture.Operations);
        Assert.False(fixture.CaptureToken.CanBeCanceled);
        Assert.False(fixture.Checkpoint.RestoreToken.CanBeCanceled);
        Assert.False(fixture.ProcessController.LaunchToken.CanBeCanceled);
    }

    [Fact]
    public async Task Cancellation_recovery_launch_failure_is_reported_without_losing_cancellation_context()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture
        {
            CancelAfterClose = cancellationSource,
        };
        fixture.ProcessController.LaunchException = new InvalidOperationException("Codex launch failed.");

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.Equal(
            "Account switch was canceled after Codex closed. " +
            "The prior authentication state was restored, but Codex launch failed.",
            result.Message);
    }

    [Fact]
    public async Task Restore_verification_failure_suppresses_launch()
    {
        var fixture = new Fixture
        {
            SwitchResult = CommandResult.Failed("simulated failure"),
        };
        fixture.Checkpoint.RestoreSucceeded = false;

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.Equal(
            "Authentication state recovery could not be verified. Codex was not launched.",
            result.Message);
        Assert.Equal(["close", "capture", "switch:target", "restore"], fixture.Operations);
    }

    [Fact]
    public async Task Launch_failure_after_verified_switch_preserves_switch_success()
    {
        var fixture = new Fixture();
        fixture.ProcessController.LaunchException = new InvalidOperationException("Codex launch failed.");

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.True(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.Equal("Account switch was verified, but Codex launch failed.", result.Message);
        Assert.Equal("launch", fixture.Operations[^1]);
    }

    [Fact]
    public async Task Unexpected_launch_invalid_state_is_rethrown_after_verified_switch()
    {
        var fixture = new Fixture();
        fixture.ProcessController.LaunchException =
            new InvalidOperationException("unexpected-launch-state");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("unexpected-launch-state", exception.Message);
        Assert.Equal("launch", fixture.Operations[^1]);
    }

    [Fact]
    public async Task Verification_error_restores_and_relaunches_without_exposing_raw_detail()
    {
        var fixture = new Fixture
        {
            VerificationException = new InvalidDataException("raw auth json secret"),
        };

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.DoesNotContain("raw auth json secret", result.Message, StringComparison.Ordinal);
        Assert.Equal("restore", fixture.Operations[^2]);
        Assert.Equal("launch", fixture.Operations[^1]);
    }

    [Fact]
    public async Task Unexpected_close_error_relaunches_then_rethrows_original_exception()
    {
        var fixture = new Fixture();
        fixture.ProcessController.CloseException = new UnexpectedTestException("unexpected-close");

        var exception = await Assert.ThrowsAsync<UnexpectedTestException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("unexpected-close", exception.Message);
        Assert.Equal(["close", "launch"], fixture.Operations);
        Assert.False(fixture.ProcessController.LaunchToken.CanBeCanceled);
    }

    [Fact]
    public async Task Unexpected_force_error_relaunches_then_rethrows_original_exception()
    {
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41]);
        fixture.ProcessController.ForceException = new UnexpectedTestException("unexpected-force");

        var exception = await Assert.ThrowsAsync<UnexpectedTestException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("unexpected-force", exception.Message);
        Assert.Equal(["close", "force:41", "launch"], fixture.Operations);
    }

    [Fact]
    public async Task Unexpected_capture_error_relaunches_then_rethrows_original_exception()
    {
        var fixture = new Fixture
        {
            CaptureException = new InvalidOperationException("unexpected-capture"),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("unexpected-capture", exception.Message);
        Assert.Equal(["close", "capture", "launch"], fixture.Operations);
    }

    [Fact]
    public async Task Unexpected_helper_error_restores_and_launches_then_rethrows_original_exception()
    {
        var fixture = new Fixture
        {
            SwitchException = new InvalidOperationException("unexpected-helper"),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("unexpected-helper", exception.Message);
        Assert.Equal("prior", fixture.AuthStateMarker);
        Assert.Equal(["close", "capture", "switch:target", "restore", "launch"], fixture.Operations);
    }

    [Fact]
    public async Task Unexpected_verification_error_restores_and_launches_then_rethrows_original_exception()
    {
        var fixture = new Fixture
        {
            VerificationException = new NullReferenceException("unexpected-verify"),
        };

        var exception = await Assert.ThrowsAsync<NullReferenceException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("unexpected-verify", exception.Message);
        Assert.Equal(["close", "capture", "switch:target", "reload", "restore", "launch"], fixture.Operations);
    }

    [Fact]
    public async Task Unexpected_helper_error_is_preserved_when_restore_cannot_be_verified()
    {
        var fixture = new Fixture
        {
            SwitchException = new InvalidOperationException("unexpected-helper"),
        };
        fixture.Checkpoint.RestoreSucceeded = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("unexpected-helper", exception.Message);
        Assert.Equal(["close", "capture", "switch:target", "restore"], fixture.Operations);
    }

    private sealed class Fixture
    {
        private readonly CodexPackageInfo _package = new(
            "OpenAI.Codex_family",
            "OpenAI.Codex_family!App",
            @"C:\Program Files\WindowsApps\OpenAI.Codex",
            @"C:\Program Files\WindowsApps\OpenAI.Codex\app\ChatGPT.exe",
            @"C:\Program Files\WindowsApps\OpenAI.Codex\app\resources");

        public Fixture()
        {
            Prior = Accounts.Record("prior-key", "prior@example.com", "prior", "prior-account");
            Target = Accounts.Record("target-key", "target@example.com", "target", "target-account");
            Registry = new AccountRegistry(3, Prior.AccountKey, [Prior, Target]);
            RegistryAfterSwitch = Registry with { ActiveAccountKey = Target.AccountKey };
            AuthAccountIdAfterSwitch = Target.ChatGptAccountId;
            ProcessController = new FakeProcessController(Operations, () => CancelAfterClose?.Cancel());
            Checkpoint = new FakeCheckpoint(Operations, () => AuthStateMarker = "prior");
            Coordinator = new SafeSwitchCoordinator(
                _package,
                "test-codex-home",
                ProcessController,
                SwitchAsync,
                ReloadAsync,
                ReadAuthAccountIdAsync,
                CaptureAsync);
        }

        public List<string> Operations { get; } = [];

        public AccountRecord Prior { get; }

        public AccountRecord Target { get; }

        public AccountRegistry Registry { get; }

        public AccountRegistry RegistryAfterSwitch { get; set; }

        public string AuthAccountIdAfterSwitch { get; set; }

        public CommandResult SwitchResult { get; init; } = new(0, string.Empty, string.Empty);

        public Exception? SwitchException { get; init; }

        public Exception? VerificationException { get; init; }

        public Exception? CaptureException { get; init; }

        public CancellationTokenSource? CancelAfterClose { get; init; }

        public CancellationToken CaptureToken { get; private set; }

        public string AuthStateMarker { get; private set; } = "prior";

        public FakeProcessController ProcessController { get; }

        public FakeCheckpoint Checkpoint { get; }

        public SafeSwitchCoordinator Coordinator { get; }

        private Task<CommandResult> SwitchAsync(string selector, CancellationToken cancellationToken)
        {
            Operations.Add($"switch:{selector}");
            cancellationToken.ThrowIfCancellationRequested();
            AuthStateMarker = "target-mutated";
            if (SwitchException is not null)
            {
                throw SwitchException;
            }

            return Task.FromResult(SwitchResult);
        }

        private Task<AccountRegistry> ReloadAsync(string codexHome, CancellationToken cancellationToken)
        {
            Operations.Add("reload");
            cancellationToken.ThrowIfCancellationRequested();
            if (VerificationException is not null)
            {
                throw VerificationException;
            }

            return Task.FromResult(RegistryAfterSwitch);
        }

        private Task<string> ReadAuthAccountIdAsync(string codexHome, CancellationToken cancellationToken)
        {
            Operations.Add("verify-auth");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuthAccountIdAfterSwitch);
        }

        private Task<IAuthStateCheckpoint> CaptureAsync(
            string codexHome,
            CancellationToken cancellationToken)
        {
            Operations.Add("capture");
            CaptureToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (CaptureException is not null)
            {
                throw CaptureException;
            }

            return Task.FromResult<IAuthStateCheckpoint>(Checkpoint);
        }
    }

    private sealed class FakeCheckpoint(
        List<string> operations,
        Action restoreState) : IAuthStateCheckpoint
    {
        public bool RestoreSucceeded { get; set; } = true;

        public CancellationToken RestoreToken { get; private set; }

        public Task<bool> RestoreAndVerifyAsync(CancellationToken cancellationToken)
        {
            operations.Add("restore");
            RestoreToken = cancellationToken;
            restoreState();
            return Task.FromResult(RestoreSucceeded);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeProcessController(
        List<string> operations,
        Action afterClose) : ICodexProcessController
    {
        public CloseResult CloseResult { get; set; } = new(true, []);

        public TimeSpan CloseTimeout { get; private set; }

        public IReadOnlyList<int> ForcedProcessIds { get; private set; } = [];

        public CancellationToken LaunchToken { get; private set; }

        public Exception? LaunchException { get; set; }

        public Exception? CloseException { get; set; }

        public Exception? ForceException { get; set; }

        public Action? CloseCallback { get; set; }

        public Action? ForceCallback { get; set; }

        public Task<CloseResult> CloseAsync(
            CodexPackageInfo package,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("close");
            CloseTimeout = timeout;
            afterClose();
            CloseCallback?.Invoke();
            if (CloseException is not null)
            {
                throw CloseException;
            }

            return Task.FromResult(CloseResult);
        }

        public Task ForceTerminateAsync(
            IReadOnlyList<int> processIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForcedProcessIds = processIds.ToArray();
            operations.Add($"force:{string.Join(',', processIds)}");
            ForceCallback?.Invoke();
            if (ForceException is not null)
            {
                throw ForceException;
            }

            return Task.CompletedTask;
        }

        public Task LaunchAsync(CodexPackageInfo package, CancellationToken cancellationToken)
        {
            operations.Add("launch");
            LaunchToken = cancellationToken;
            if (LaunchException is not null)
            {
                throw LaunchException;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class UnexpectedTestException(string message) : Exception(message);
}
