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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Helper_disappearance_after_preflight_is_attached_to_failed_switch_result(
        bool operationalStartFailure)
    {
        var missingAvailability = MissingAvailability();
        var fixture = new Fixture
        {
            AvailabilityAfterSwitch = missingAvailability,
            SwitchResult = CommandResult.Failed("simulated failure"),
            SwitchException = operationalStartFailure
                ? new IOException("simulated operational start failure")
                : null,
        };

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.False(result.Succeeded);
        Assert.Same(missingAvailability, result.HelperAvailability);
        Assert.Equal(2, fixture.AvailabilityCheckCount);
    }

    [Fact]
    public async Task False_start_rechecks_availability_and_returns_structured_switch_failure()
    {
        var runner = new ProcessRunner(new ConfiguredProcessFactory(
            new ConfiguredStartedProcess { StartResult = false }));
        var startException = await Record.ExceptionAsync(() => runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["switch"]),
            default));
        Assert.NotNull(startException);
        var missingAvailability = MissingAvailability();
        var fixture = new Fixture
        {
            AvailabilityAfterSwitch = missingAvailability,
            SwitchException = startException,
        };

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("Account switch failed. The prior authentication state was restored.", result.Message);
        Assert.Same(missingAvailability, result.HelperAvailability);
        Assert.Equal(2, fixture.AvailabilityCheckCount);
    }

    [Fact]
    public async Task Initial_wait_unknown_exit_suppresses_switch_recovery()
    {
        var runner = new ProcessRunner(new ConfiguredProcessFactory(
            new ConfiguredStartedProcess
            {
                InitialWaitException = new IOException("secret initial wait failure"),
                KeepAliveAfterKill = true,
                FinalWaitException = new IOException("secret final wait failure"),
            }));
        var waitException = await Record.ExceptionAsync(() => runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["switch"]),
            default));
        Assert.NotNull(waitException);
        var fixture = new Fixture { SwitchException = waitException };

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            default);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "Codex remains closed because helper process exit could not be verified.",
            result.Message);
        Assert.False(result.CanRetryLaunch);
        Assert.DoesNotContain("restore", fixture.Operations);
        Assert.DoesNotContain("launch", fixture.Operations);
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
        Assert.Equal(["verify-auth"], fixture.Operations);
    }

    [Fact]
    public async Task Registry_active_target_with_mismatched_auth_runs_transactional_repair()
    {
        var fixture = new Fixture
        {
            AuthAccountIdBeforeSwitch = "wrong-account",
        };
        var activeRegistry = fixture.Registry with { ActiveAccountKey = fixture.Target.AccountKey };

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, activeRegistry, default);

        Assert.True(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(
            ["verify-auth", "close", "capture", "switch:target", "reload", "verify-auth", "launch"],
            fixture.Operations);
    }

    [Fact]
    public async Task Registry_active_target_with_unreadable_auth_runs_transactional_repair()
    {
        var fixture = new Fixture
        {
            InitialAuthReadException = new InvalidDataException("invalid initial auth"),
        };
        var activeRegistry = fixture.Registry with { ActiveAccountKey = fixture.Target.AccountKey };

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, activeRegistry, default);

        Assert.True(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(
            ["verify-auth", "close", "capture", "switch:target", "reload", "verify-auth", "launch"],
            fixture.Operations);
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
    public async Task Missing_helper_returns_expected_path_before_closing_codex()
    {
        using var directory = new TemporaryDirectory();
        var missingHelperPath = Path.Combine(directory.Path, "tools", "codex-auth.exe");
        var operations = new List<string>();
        var processController = new FakeProcessController(operations, static () => { });
        var authService = new CodexAuthService(
            missingHelperPath,
            directory.Path,
            new NeverProcessRunner());
        var prior = Accounts.Record("prior-key", "prior@example.com", "prior", "prior-account");
        var target = Accounts.Record("target-key", "target@example.com", "target", "target-account");
        var registry = new AccountRegistry(3, prior.AccountKey, [prior, target]);
        var coordinator = new SafeSwitchCoordinator(
            new CodexPackageInfo(
                "OpenAI.Codex_family",
                "OpenAI.Codex_family!App",
                @"C:\Program Files\WindowsApps\OpenAI.Codex",
                @"C:\Program Files\WindowsApps\OpenAI.Codex\app\ChatGPT.exe",
                @"C:\Program Files\WindowsApps\OpenAI.Codex\app\resources"),
            directory.Path,
            processController,
            authService,
            new AccountRegistryService());

        var result = await coordinator.SwitchAsync(target, registry, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Contains(Path.GetFullPath(missingHelperPath), result.Message, StringComparison.Ordinal);
        var availabilityProperty = typeof(SwitchResult).GetProperty("HelperAvailability");
        Assert.NotNull(availabilityProperty);
        var availability = Assert.IsType<HelperAvailability>(availabilityProperty.GetValue(result));
        Assert.False(availability.IsAvailable);
        Assert.Equal(Path.GetFullPath(missingHelperPath), availability.ExpectedPath);
        Assert.Empty(operations);
    }

    [Fact]
    public async Task Unresolved_process_discovery_stops_before_capture_or_switch()
    {
        var fixture = new Fixture();
        fixture.ProcessController.CloseException = new CodexProcessDiscoveryException();

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.Equal("Account switch failed before authentication changed.", result.Message);
        Assert.Equal(["close"], fixture.Operations);
        Assert.Equal("prior", fixture.AuthStateMarker);
    }

    [Fact]
    public async Task Reused_package_pid_force_failure_stops_before_capture_or_switch()
    {
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41]);
        fixture.ProcessController.ForceException = new CodexProcessDiscoveryException();

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.Equal("Account switch failed before authentication changed.", result.Message);
        Assert.Equal(["close", "force:41"], fixture.Operations);
        Assert.Equal("prior", fixture.AuthStateMarker);
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
    public async Task Close_cancellation_after_side_effect_suppresses_launch_without_checkpoint()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseCallback = cancellationSource.Cancel;
        fixture.ProcessController.CloseException =
            new CodexCloseCanceledException(sideEffectsStarted: true, cancellationSource.Token);

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.False(result.CanRetryLaunch);
        Assert.Equal(
            "Account switch was canceled before authentication changed. " +
            "Codex was not launched because process exit could not be verified.",
            result.Message);
        Assert.Equal(["close"], fixture.Operations);
        Assert.Equal("prior", fixture.AuthStateMarker);
    }

    [Fact]
    public async Task Cancellation_before_close_side_effect_is_rethrown_without_launch()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseCallback = cancellationSource.Cancel;
        fixture.ProcessController.CloseException =
            new CodexCloseCanceledException(sideEffectsStarted: false, cancellationSource.Token);

        var exception = await Assert.ThrowsAsync<CodexCloseCanceledException>(() =>
            fixture.Coordinator.SwitchAsync(
                fixture.Target,
                fixture.Registry,
                cancellationSource.Token));

        Assert.False(exception.SideEffectsStarted);
        Assert.Equal("Codex close was canceled.", exception.Message);
        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.Equal(["close"], fixture.Operations);
    }

    [Fact]
    public async Task Close_cancellation_suppresses_launch_even_when_launch_would_fail()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseCallback = cancellationSource.Cancel;
        fixture.ProcessController.CloseException =
            new CodexCloseCanceledException(sideEffectsStarted: true, cancellationSource.Token);
        fixture.ProcessController.LaunchException = new CodexLaunchException();

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.False(result.CanRetryLaunch);
        Assert.Equal(
            "Account switch was canceled before authentication changed. " +
            "Codex was not launched because process exit could not be verified.",
            result.Message);
        Assert.Equal(["close"], fixture.Operations);
        Assert.Equal("prior", fixture.AuthStateMarker);
    }

    [Fact]
    public async Task Force_cancellation_without_close_or_force_side_effect_is_rethrown()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41, 73]);
        fixture.ProcessController.ForceCallback = cancellationSource.Cancel;
        fixture.ProcessController.ForceException =
            new CodexForceTerminateCanceledException(sideEffectsStarted: false, cancellationSource.Token);

        var exception = await Assert.ThrowsAsync<CodexForceTerminateCanceledException>(() =>
            fixture.Coordinator.SwitchAsync(
                fixture.Target,
                fixture.Registry,
                cancellationSource.Token));

        Assert.False(exception.SideEffectsStarted);
        Assert.Equal("Codex force termination was canceled.", exception.Message);
        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.Equal(["close", "force:41,73"], fixture.Operations);
    }

    [Fact]
    public async Task Force_cancellation_with_close_side_effect_suppresses_launch()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41, 73])
        {
            SideEffectsStarted = true,
        };
        fixture.ProcessController.ForceCallback = cancellationSource.Cancel;
        fixture.ProcessController.ForceException =
            new CodexForceTerminateCanceledException(sideEffectsStarted: false, cancellationSource.Token);

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.False(result.CanRetryLaunch);
        Assert.Equal(
            "Account switch was canceled before authentication changed. " +
            "Codex was not launched because process exit could not be verified.",
            result.Message);
        Assert.Equal(["close", "force:41,73"], fixture.Operations);
        Assert.Equal("prior", fixture.AuthStateMarker);
    }

    [Fact]
    public async Task Force_cancellation_after_kill_suppresses_launch_without_checkpoint()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41, 73]);
        fixture.ProcessController.ForceCallback = cancellationSource.Cancel;
        fixture.ProcessController.ForceException =
            new CodexForceTerminateCanceledException(sideEffectsStarted: true, cancellationSource.Token);

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.False(result.CanRetryLaunch);
        Assert.Equal(
            "Account switch was canceled before authentication changed. " +
            "Codex was not launched because process exit could not be verified.",
            result.Message);
        Assert.Equal(["close", "force:41,73"], fixture.Operations);
        Assert.Equal("prior", fixture.AuthStateMarker);
    }

    [Fact]
    public async Task Cancellation_after_force_before_checkpoint_uses_close_side_effect_evidence()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41])
        {
            SideEffectsStarted = true,
        };
        fixture.ProcessController.ForceCallback = cancellationSource.Cancel;

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(["close", "force:41", "launch"], fixture.Operations);
    }

    [Fact]
    public async Task Cancellation_after_successful_force_uses_committed_recovery_boundary()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41]);
        fixture.ProcessController.ForceCallback = cancellationSource.Cancel;

        var result = await fixture.Coordinator.SwitchAsync(
            fixture.Target,
            fixture.Registry,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(
            "Account switch was canceled before authentication changed. Codex was restarted.",
            result.Message);
        Assert.Equal(["close", "force:41", "launch"], fixture.Operations);
        Assert.False(fixture.ProcessController.LaunchToken.CanBeCanceled);
    }

    [Fact]
    public async Task Force_exit_barrier_completes_before_auth_checkpoint_or_switch()
    {
        var forceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseForce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41]);
        fixture.ProcessController.ForceOperation = async cancellationToken =>
        {
            forceEntered.TrySetResult();
            await releaseForce.Task.WaitAsync(cancellationToken);
        };

        var running = fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);
        await forceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["close", "force:41"], fixture.Operations);

        releaseForce.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        Assert.True(fixture.Operations.IndexOf("force:41") < fixture.Operations.IndexOf("capture"));
        Assert.True(fixture.Operations.IndexOf("capture") < fixture.Operations.IndexOf("switch:target"));
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
        fixture.ProcessController.LaunchException = new CodexLaunchException();

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
        Assert.False(RequiredBooleanProperty(result, "CanRetryLaunch"));
    }

    [Fact]
    public async Task Unknown_helper_exit_suppresses_restore_launch_and_retry()
    {
        var fixture = new Fixture
        {
            SwitchException = CreateUnknownHelperExitException(),
        };

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.False(result.CanRetryLaunch);
        Assert.Equal(
            "Codex remains closed because helper process exit could not be verified.",
            result.Message);
        Assert.Equal(["close", "capture", "switch:target"], fixture.Operations);
    }

    [Fact]
    public async Task Launch_failure_after_verified_switch_preserves_switch_success()
    {
        var fixture = new Fixture();
        fixture.ProcessController.LaunchException = new CodexLaunchException();

        var result = await fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default);

        Assert.True(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.Equal("Account switch was verified, but Codex launch failed.", result.Message);
        Assert.Equal("launch", fixture.Operations[^1]);
        Assert.True(RequiredBooleanProperty(result, "CanRetryLaunch"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_launch_uses_existing_package_launch_only_and_returns_sanitized_outcome(
        bool launchSucceeds)
    {
        var fixture = new Fixture();
        if (!launchSucceeds)
        {
            fixture.ProcessController.LaunchException = new CodexLaunchException();
        }
        var method = typeof(SafeSwitchCoordinator).GetMethod("RetryLaunchAsync");
        Assert.NotNull(method);

        var invocation = method.Invoke(fixture.Coordinator, [CancellationToken.None]);
        var task = Assert.IsAssignableFrom<Task<bool>>(invocation);
        var result = await task;

        Assert.Equal(launchSucceeds, result);
        Assert.Equal(["launch"], fixture.Operations);
        Assert.False(fixture.ProcessController.LaunchToken.CanBeCanceled);
    }

    [Fact]
    public async Task Unexpected_launch_invalid_state_is_rethrown_after_verified_switch()
    {
        var fixture = new Fixture();
        fixture.ProcessController.LaunchException =
            new InvalidOperationException("Codex launch failed.");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("Codex launch failed.", exception.Message);
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

    [Fact]
    public async Task Unexpected_restore_error_does_not_replace_original_helper_error()
    {
        var fixture = new Fixture
        {
            SwitchException = new InvalidOperationException("unexpected-helper"),
        };
        fixture.Checkpoint.RestoreException = new NullReferenceException("unexpected-restore");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("unexpected-helper", exception.Message);
        Assert.DoesNotContain("launch", fixture.Operations);
    }

    [Fact]
    public async Task Unexpected_dispose_error_does_not_replace_original_and_safe_recovery_launches()
    {
        var fixture = new Fixture
        {
            SwitchException = new InvalidOperationException("unexpected-helper"),
        };
        fixture.Checkpoint.DisposeException = new NullReferenceException("unexpected-dispose");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.SwitchAsync(fixture.Target, fixture.Registry, default));

        Assert.Equal("unexpected-helper", exception.Message);
        Assert.Equal("launch", fixture.Operations[^1]);
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
            AuthAccountIdBeforeSwitch = Target.ChatGptAccountId;
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
                CaptureAsync,
                CheckAvailability);
        }

        public List<string> Operations { get; } = [];

        public AccountRecord Prior { get; }

        public AccountRecord Target { get; }

        public AccountRegistry Registry { get; }

        public AccountRegistry RegistryAfterSwitch { get; set; }

        public string AuthAccountIdAfterSwitch { get; set; }

        public string AuthAccountIdBeforeSwitch { get; init; }

        public CommandResult SwitchResult { get; init; } = new(0, string.Empty, string.Empty);

        public HelperAvailability Availability { get; private set; } =
            new(true, @"C:\expected\tools\codex-auth.exe", string.Empty);

        public HelperAvailability? AvailabilityAfterSwitch { get; init; }

        public int AvailabilityCheckCount { get; private set; }

        public Exception? SwitchException { get; init; }

        public Exception? VerificationException { get; init; }

        public Exception? InitialAuthReadException { get; init; }

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
            _switchStarted = true;
            AuthStateMarker = "target-mutated";
            if (AvailabilityAfterSwitch is not null)
            {
                Availability = AvailabilityAfterSwitch;
            }

            if (SwitchException is not null)
            {
                throw SwitchException;
            }

            return Task.FromResult(SwitchResult);
        }

        private HelperAvailability CheckAvailability()
        {
            AvailabilityCheckCount++;
            return Availability;
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
            if (!_switchStarted && InitialAuthReadException is not null)
            {
                throw InitialAuthReadException;
            }

            if (_switchStarted && VerificationException is not null)
            {
                throw VerificationException;
            }

            return Task.FromResult(
                _switchStarted ? AuthAccountIdAfterSwitch : AuthAccountIdBeforeSwitch);
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

        private bool _switchStarted;
    }

    private static bool RequiredBooleanProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<bool>(property.GetValue(instance));
    }

    private static Exception CreateUnknownHelperExitException()
    {
        var exceptionType = typeof(SafeSwitchCoordinator).Assembly.GetType(
            "CodexAccountSwitcher.Services.HelperProcessExitUnverifiedException");
        Assert.NotNull(exceptionType);
        return Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType));
    }

    private static HelperAvailability MissingAvailability() => new(
        false,
        @"C:\expected\tools\codex-auth.exe",
        @"The codex-auth helper is unavailable at the expected path: C:\expected\tools\codex-auth.exe");

    private sealed class FakeCheckpoint(
        List<string> operations,
        Action restoreState) : IAuthStateCheckpoint
    {
        public bool RestoreSucceeded { get; set; } = true;

        public CancellationToken RestoreToken { get; private set; }

        public Exception? RestoreException { get; set; }

        public Exception? DisposeException { get; set; }

        public Task<bool> RestoreAndVerifyAsync(CancellationToken cancellationToken)
        {
            operations.Add("restore");
            RestoreToken = cancellationToken;
            if (RestoreException is not null)
            {
                throw RestoreException;
            }

            restoreState();
            return Task.FromResult(RestoreSucceeded);
        }

        public void Dispose()
        {
            if (DisposeException is not null)
            {
                throw DisposeException;
            }
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

        public Func<CancellationToken, Task>? ForceOperation { get; set; }

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

        public async Task ForceTerminateAsync(
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

            if (ForceOperation is not null)
            {
                await ForceOperation(cancellationToken);
            }
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

    private sealed class NeverProcessRunner : IProcessRunner
    {
        public Task<CommandResult> RunCapturedAsync(
            ProcessRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The helper must be rejected before process start.");

        public Task<CommandResult> RunVisibleAsync(
            ProcessRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The helper must be rejected before process start.");
    }

    private sealed class UnexpectedTestException(string message) : Exception(message);
}
