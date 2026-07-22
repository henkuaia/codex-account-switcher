using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Tests;

public sealed class SafeLoginCoordinatorTests
{
    [Fact]
    public void Coordinator_exposes_awaited_streaming_login_contract()
    {
        var coordinatorType = typeof(SafeSwitchCoordinator).Assembly.GetType(
            "CodexAccountSwitcher.Services.SafeLoginCoordinator");

        Assert.NotNull(coordinatorType);
        var method = coordinatorType.GetMethod(
            "LoginAsync",
            [typeof(ProcessOutputHandler), typeof(CancellationToken)]);
        Assert.NotNull(method);
        Assert.True(method.ReturnType.IsGenericType);
        Assert.Equal(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
        Assert.Equal("LoginResult", method.ReturnType.GenericTypeArguments[0].Name);
    }

    [Fact]
    public async Task Successful_login_closes_captures_streams_verifies_and_launches_in_order()
    {
        var fixture = new Fixture();

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.True(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal(
            ["availability", "close", "capture", "login", "output", "reload", "verify-auth", "launch"],
            fixture.Operations);
        Assert.False(fixture.CaptureToken.CanBeCanceled);
        Assert.False(fixture.ProcessController.LaunchToken.CanBeCanceled);
    }

    [Fact]
    public async Task Missing_helper_returns_before_closing_codex()
    {
        var fixture = new Fixture
        {
            Availability = new HelperAvailability(
                false,
                @"C:\expected\tools\codex-auth.exe",
                @"The codex-auth helper is unavailable at the expected path: C:\expected\tools\codex-auth.exe"),
        };

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Contains(@"C:\expected\tools\codex-auth.exe", result.Message, StringComparison.Ordinal);
        var availabilityProperty = typeof(LoginResult).GetProperty("HelperAvailability");
        Assert.NotNull(availabilityProperty);
        Assert.Same(fixture.Availability, availabilityProperty.GetValue(result));
        Assert.Equal(["availability"], fixture.Operations);
    }

    [Fact]
    public async Task Unresolved_process_discovery_stops_before_capture_or_login()
    {
        var fixture = new Fixture();
        fixture.ProcessController.CloseException = new CodexProcessDiscoveryException();

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal("Account login failed before authentication changed.", result.Message);
        Assert.Equal(["availability", "close", "launch"], fixture.Operations);
    }

    [Fact]
    public async Task Close_timeout_forces_before_capture_and_login()
    {
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41, 73]);

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.True(result.Succeeded);
        Assert.Equal([41, 73], fixture.ProcessController.ForcedProcessIds);
        Assert.True(fixture.Operations.IndexOf("force:41,73") < fixture.Operations.IndexOf("capture"));
        Assert.True(fixture.Operations.IndexOf("capture") < fixture.Operations.IndexOf("login"));
    }

    [Fact]
    public async Task Reused_package_pid_force_failure_stops_before_capture_or_login()
    {
        var fixture = new Fixture();
        fixture.ProcessController.CloseResult = new CloseResult(false, [41]);
        fixture.ProcessController.ForceException = new CodexProcessDiscoveryException();

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal("Account login failed before authentication changed.", result.Message);
        Assert.Equal(["availability", "close", "force:41", "launch"], fixture.Operations);
    }

    [Fact]
    public async Task Helper_failure_restores_prior_checkpoint_then_relaunches()
    {
        var fixture = new Fixture
        {
            LoginResult = CommandResult.Failed("simulated failure"),
        };

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal("prior", fixture.AuthStateMarker);
        Assert.Equal(
            ["availability", "close", "capture", "login", "output", "restore", "launch"],
            fixture.Operations);
        Assert.False(fixture.Checkpoint.RestoreToken.CanBeCanceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Helper_disappearance_after_preflight_is_attached_to_failed_login_result(
        bool operationalStartFailure)
    {
        var missingAvailability = MissingAvailability();
        var fixture = new Fixture
        {
            AvailabilityAfterLogin = missingAvailability,
            LoginResult = CommandResult.Failed("simulated failure"),
            LoginException = operationalStartFailure
                ? new IOException("simulated operational start failure")
                : null,
        };

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.False(result.Succeeded);
        Assert.Same(missingAvailability, result.HelperAvailability);
        Assert.Equal(2, fixture.AvailabilityCheckCount);
    }

    [Fact]
    public async Task False_start_rechecks_availability_and_returns_structured_login_failure()
    {
        var runner = new ProcessRunner(new ConfiguredProcessFactory(
            new ConfiguredStartedProcess { StartResult = false }));
        var startException = await Record.ExceptionAsync(() => runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            default));
        Assert.NotNull(startException);
        var missingAvailability = MissingAvailability();
        var fixture = new Fixture
        {
            AvailabilityAfterLogin = missingAvailability,
            LoginException = startException,
        };

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Account login failed. The prior authentication state was restored.", result.Message);
        Assert.Same(missingAvailability, result.HelperAvailability);
        Assert.Equal(2, fixture.AvailabilityCheckCount);
    }

    [Fact]
    public async Task Cancellation_after_login_side_effect_restores_then_relaunches()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture
        {
            CancelDuringLogin = cancellationSource,
        };

        var result = await fixture.Coordinator.LoginAsync(
            fixture.OutputHandler,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal("prior", fixture.AuthStateMarker);
        Assert.Equal(
            ["availability", "close", "capture", "login", "output", "restore", "launch"],
            fixture.Operations);
    }

    [Theory]
    [InlineData("registry")]
    [InlineData("auth")]
    public async Task Coherence_verification_failure_restores_then_relaunches(string mismatch)
    {
        var fixture = new Fixture();
        if (mismatch == "registry")
        {
            fixture.RegistryAfterLogin = fixture.RegistryAfterLogin with { ActiveAccountKey = fixture.Prior.AccountKey };
        }
        else
        {
            fixture.AuthAccountIdAfterLogin = "wrong-account";
        }

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.False(result.Succeeded);
        Assert.True(result.LaunchSucceeded);
        Assert.Equal("prior", fixture.AuthStateMarker);
        Assert.Equal("restore", fixture.Operations[^2]);
        Assert.Equal("launch", fixture.Operations[^1]);
    }

    [Fact]
    public async Task Unverifiable_restore_suppresses_launch()
    {
        var fixture = new Fixture
        {
            LoginResult = CommandResult.Failed("simulated failure"),
        };
        fixture.Checkpoint.RestoreSucceeded = false;

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.False(result.CanRetryLaunch);
        Assert.Equal(
            "Authentication state recovery could not be verified. Codex was not launched.",
            result.Message);
        Assert.Equal("restore", fixture.Operations[^1]);
    }

    [Fact]
    public async Task Unknown_helper_exit_suppresses_restore_launch_and_retry()
    {
        var exceptionType = typeof(SafeLoginCoordinator).Assembly.GetType(
            "CodexAccountSwitcher.Services.HelperProcessExitUnverifiedException");
        Assert.NotNull(exceptionType);
        var fixture = new Fixture
        {
            LoginException = Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType)),
        };

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.False(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.False(result.CanRetryLaunch);
        Assert.Equal(
            "Codex remains closed because helper process exit could not be verified.",
            result.Message);
        Assert.Equal(["availability", "close", "capture", "login"], fixture.Operations);
    }

    [Fact]
    public async Task Launch_failure_after_verified_login_enables_retry()
    {
        var fixture = new Fixture();
        fixture.ProcessController.LaunchException = new CodexLaunchException();

        var result = await fixture.Coordinator.LoginAsync(fixture.OutputHandler, default);

        Assert.True(result.Succeeded);
        Assert.False(result.LaunchSucceeded);
        Assert.True(result.CanRetryLaunch);
        Assert.Equal("Account login was verified, but Codex launch failed.", result.Message);
    }

    [Fact]
    public async Task Unexpected_login_error_restores_and_launches_then_rethrows_original_exception()
    {
        var fixture = new Fixture
        {
            LoginException = new InvalidOperationException("unexpected-login"),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.LoginAsync(fixture.OutputHandler, default));

        Assert.Equal("unexpected-login", exception.Message);
        Assert.Equal("prior", fixture.AuthStateMarker);
        Assert.Equal(
            ["availability", "close", "capture", "login", "restore", "launch"],
            fixture.Operations);
    }

    [Fact]
    public async Task Unexpected_verification_error_restores_and_launches_then_rethrows_original_exception()
    {
        var fixture = new Fixture
        {
            VerificationException = new NullReferenceException("unexpected-verify"),
        };

        var exception = await Assert.ThrowsAsync<NullReferenceException>(
            () => fixture.Coordinator.LoginAsync(fixture.OutputHandler, default));

        Assert.Equal("unexpected-verify", exception.Message);
        Assert.Equal("prior", fixture.AuthStateMarker);
        Assert.Equal(
            ["availability", "close", "capture", "login", "output", "reload", "restore", "launch"],
            fixture.Operations);
    }

    [Fact]
    public async Task Unexpected_login_error_is_preserved_when_restore_cannot_be_verified()
    {
        var fixture = new Fixture
        {
            LoginException = new InvalidOperationException("unexpected-login"),
        };
        fixture.Checkpoint.RestoreSucceeded = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.LoginAsync(fixture.OutputHandler, default));

        Assert.Equal("unexpected-login", exception.Message);
        Assert.Equal("restore", fixture.Operations[^1]);
        Assert.DoesNotContain("launch", fixture.Operations);
    }

    [Fact]
    public async Task Unexpected_restore_error_does_not_replace_original_login_error()
    {
        var fixture = new Fixture
        {
            LoginException = new InvalidOperationException("unexpected-login"),
        };
        fixture.Checkpoint.RestoreException = new NullReferenceException("unexpected-restore");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.LoginAsync(fixture.OutputHandler, default));

        Assert.Equal("unexpected-login", exception.Message);
        Assert.Equal("restore", fixture.Operations[^1]);
        Assert.DoesNotContain("launch", fixture.Operations);
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
            Added = Accounts.Record("added-key", "added@example.com", "added", "added-account");
            RegistryAfterLogin = new AccountRegistry(3, Added.AccountKey, [Prior, Added]);
            ProcessController = new FakeProcessController(Operations);
            Checkpoint = new FakeCheckpoint(Operations, () => AuthStateMarker = "prior");
            Coordinator = new SafeLoginCoordinator(
                _package,
                "test-codex-home",
                ProcessController,
                LoginAsync,
                LoadRegistryAsync,
                ReadAuthAccountIdAsync,
                CaptureAsync,
                CheckAvailability);
        }

        public List<string> Operations { get; } = [];

        public AccountRecord Prior { get; }

        public AccountRecord Added { get; }

        public AccountRegistry RegistryAfterLogin { get; set; }

        public CommandResult LoginResult { get; init; } = new(0, string.Empty, string.Empty);

        public Exception? LoginException { get; init; }

        public Exception? VerificationException { get; init; }

        public string AuthAccountIdAfterLogin { get; set; } = "added-account";

        public string AuthStateMarker { get; private set; } = "prior";

        public CancellationTokenSource? CancelDuringLogin { get; init; }

        public HelperAvailability Availability { get; set; } =
            new(true, @"C:\expected\tools\codex-auth.exe", string.Empty);

        public HelperAvailability? AvailabilityAfterLogin { get; init; }

        public int AvailabilityCheckCount { get; private set; }

        public CancellationToken CaptureToken { get; private set; }

        public FakeProcessController ProcessController { get; }

        public FakeCheckpoint Checkpoint { get; }

        public SafeLoginCoordinator Coordinator { get; }

        public ValueTask OutputHandler(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add("output");
            return ValueTask.CompletedTask;
        }

        private HelperAvailability CheckAvailability()
        {
            AvailabilityCheckCount++;
            if (AvailabilityCheckCount == 1)
            {
                Operations.Add("availability");
            }

            return Availability;
        }

        private async Task<CommandResult> LoginAsync(
            ProcessOutputHandler outputHandler,
            CancellationToken cancellationToken)
        {
            Operations.Add("login");
            AuthStateMarker = "login-mutated";
            if (AvailabilityAfterLogin is not null)
            {
                Availability = AvailabilityAfterLogin;
            }

            if (LoginException is not null)
            {
                throw LoginException;
            }

            await outputHandler(
                new ProcessOutputLine(ProcessOutputStream.StandardOutput, "device login"),
                cancellationToken);
            CancelDuringLogin?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return LoginResult;
        }

        private Task<AccountRegistry> LoadRegistryAsync(
            string codexHome,
            CancellationToken cancellationToken)
        {
            Operations.Add("reload");
            if (VerificationException is not null)
            {
                throw VerificationException;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RegistryAfterLogin);
        }

        private Task<string> ReadAuthAccountIdAsync(
            string codexHome,
            CancellationToken cancellationToken)
        {
            Operations.Add("verify-auth");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuthAccountIdAfterLogin);
        }

        private Task<IAuthStateCheckpoint> CaptureAsync(
            string codexHome,
            CancellationToken cancellationToken)
        {
            Operations.Add("capture");
            CaptureToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IAuthStateCheckpoint>(Checkpoint);
        }
    }

    private static HelperAvailability MissingAvailability() => new(
        false,
        @"C:\expected\tools\codex-auth.exe",
        @"The codex-auth helper is unavailable at the expected path: C:\expected\tools\codex-auth.exe");

    private sealed class FakeCheckpoint(
        ICollection<string> operations,
        Action restoreState) : IAuthStateCheckpoint
    {
        public bool RestoreSucceeded { get; set; } = true;

        public Exception? RestoreException { get; set; }

        public CancellationToken RestoreToken { get; private set; }

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
        }
    }

    private sealed class FakeProcessController(ICollection<string> operations) : ICodexProcessController
    {
        public CloseResult CloseResult { get; set; } = new(true, []);

        public IReadOnlyList<int> ForcedProcessIds { get; private set; } = [];

        public CancellationToken LaunchToken { get; private set; }

        public Exception? LaunchException { get; set; }

        public Exception? CloseException { get; set; }

        public Exception? ForceException { get; set; }

        public Task<CloseResult> CloseAsync(
            CodexPackageInfo package,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("close");
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
            if (ForceException is not null)
            {
                throw ForceException;
            }

            return Task.CompletedTask;
        }

        public Task LaunchAsync(CodexPackageInfo package, CancellationToken cancellationToken)
        {
            LaunchToken = cancellationToken;
            operations.Add("launch");
            if (LaunchException is not null)
            {
                throw LaunchException;
            }

            return Task.CompletedTask;
        }
    }
}
