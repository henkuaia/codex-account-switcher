using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class TargetedRemoveCoordinatorTests
{
    [Fact]
    public void Coordinator_exposes_explicit_managed_target_contract()
    {
        var coordinatorType = typeof(SafeSwitchCoordinator).Assembly.GetType(
            "CodexAccountSwitcher.Services.TargetedRemoveCoordinator");

        Assert.NotNull(coordinatorType);
        var method = coordinatorType.GetMethod(
            "RemoveAsync",
            [typeof(AccountRecord), typeof(AccountRegistry), typeof(CancellationToken)]);
        Assert.NotNull(method);
        Assert.True(method.ReturnType.IsGenericType);
        Assert.Equal(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
        Assert.Equal("RemovalResult", method.ReturnType.GenericTypeArguments[0].Name);
    }

    [Fact]
    public async Task Active_account_is_rejected_before_helper_or_checkpoint()
    {
        var fixture = new Fixture();

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Active,
            fixture.RegistryBefore,
            default);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "The active account cannot be removed. Switch to another account first.",
            result.Message);
        Assert.Empty(fixture.Operations);
    }

    [Fact]
    public async Task Successful_targeted_removal_uses_unique_selector_and_preserves_active_auth()
    {
        var fixture = new Fixture();

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Target,
            fixture.RegistryBefore,
            default);

        Assert.True(result.Succeeded);
        Assert.Equal("Account removal verified.", result.Message);
        Assert.Equal(
            [
                "availability",
                "capture",
                "remove:target",
                "reload",
                "verify-auth",
                "verify-auth-bytes",
                "verify-snapshot-absent",
            ],
            fixture.Operations);
        Assert.Equal("live-auth-prior", fixture.LiveAuthMarker);
        Assert.Equal("deleted", fixture.TargetSnapshotMarker);
    }

    [Fact]
    public async Task Missing_helper_returns_structured_availability_before_capture()
    {
        var fixture = new Fixture
        {
            Availability = new HelperAvailability(
                false,
                @"C:\expected\tools\codex-auth.exe",
                @"The codex-auth helper is unavailable at the expected path: C:\expected\tools\codex-auth.exe"),
        };

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Target,
            fixture.RegistryBefore,
            default);

        Assert.False(result.Succeeded);
        var availabilityProperty = typeof(RemovalResult).GetProperty("HelperAvailability");
        Assert.NotNull(availabilityProperty);
        Assert.Same(fixture.Availability, availabilityProperty.GetValue(result));
        Assert.Equal(["availability"], fixture.Operations);
    }

    [Fact]
    public async Task Helper_failure_restores_registry_and_target_snapshot()
    {
        var fixture = new Fixture
        {
            RemoveResult = CommandResult.Failed("simulated failure"),
        };

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Target,
            fixture.RegistryBefore,
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("present", fixture.TargetSnapshotMarker);
        Assert.Equal("prior-registry", fixture.RegistryMarker);
        Assert.Equal(
            ["availability", "capture", "remove:target", "restore"],
            fixture.Operations);
        Assert.False(fixture.Checkpoint.RestoreToken.CanBeCanceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Helper_disappearance_after_preflight_is_attached_to_failed_removal_result(
        bool operationalStartFailure)
    {
        var missingAvailability = MissingAvailability();
        var fixture = new Fixture
        {
            AvailabilityAfterRemove = missingAvailability,
            RemoveResult = CommandResult.Failed("simulated failure"),
            RemoveException = operationalStartFailure
                ? new IOException("simulated operational start failure")
                : null,
        };

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Target,
            fixture.RegistryBefore,
            default);

        Assert.False(result.Succeeded);
        Assert.Same(missingAvailability, result.HelperAvailability);
        Assert.Equal(2, fixture.AvailabilityCheckCount);
    }

    [Fact]
    public async Task False_start_rechecks_availability_and_returns_structured_removal_failure()
    {
        var runner = new ProcessRunner(new ConfiguredProcessFactory(
            new ConfiguredStartedProcess { StartResult = false }));
        var startException = await Record.ExceptionAsync(() => runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["remove"]),
            default));
        Assert.NotNull(startException);
        var missingAvailability = MissingAvailability();
        var fixture = new Fixture
        {
            AvailabilityAfterRemove = missingAvailability,
            RemoveException = startException,
        };

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Target,
            fixture.RegistryBefore,
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("Account removal failed. The prior account state was restored.", result.Message);
        Assert.Same(missingAvailability, result.HelperAvailability);
        Assert.Equal(2, fixture.AvailabilityCheckCount);
    }

    [Fact]
    public async Task Cancellation_after_helper_side_effect_restores_registry_and_snapshot()
    {
        using var cancellationSource = new CancellationTokenSource();
        var fixture = new Fixture
        {
            CancelDuringRemove = cancellationSource,
        };

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Target,
            fixture.RegistryBefore,
            cancellationSource.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("present", fixture.TargetSnapshotMarker);
        Assert.Equal("prior-registry", fixture.RegistryMarker);
        Assert.Equal("restore", fixture.Operations[^1]);
    }

    [Theory]
    [InlineData("target-remains")]
    [InlineData("active-changed")]
    [InlineData("auth-changed")]
    [InlineData("auth-bytes-changed")]
    public async Task Post_remove_verification_failure_restores_prior_state(string failure)
    {
        var fixture = new Fixture();
        switch (failure)
        {
            case "target-remains":
                fixture.RegistryAfterRemove = fixture.RegistryBefore;
                break;
            case "active-changed":
                fixture.RegistryAfterRemove = fixture.RegistryAfterRemove with { ActiveAccountKey = null };
                break;
            case "auth-changed":
                fixture.AuthAccountIdAfterRemove = "different-account";
                break;
            case "auth-bytes-changed":
                fixture.Checkpoint.AuthUnchanged = false;
                break;
        }

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Target,
            fixture.RegistryBefore,
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("present", fixture.TargetSnapshotMarker);
        Assert.Equal("prior-registry", fixture.RegistryMarker);
        Assert.Equal("restore", fixture.Operations[^1]);
    }

    [Fact]
    public async Task Unverifiable_restore_returns_fixed_failure()
    {
        var fixture = new Fixture
        {
            RemoveResult = CommandResult.Failed("simulated failure"),
        };
        fixture.Checkpoint.RestoreSucceeded = false;

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Target,
            fixture.RegistryBefore,
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("Account removal recovery could not be verified.", result.Message);
    }

    [Fact]
    public async Task Unexpected_helper_error_restores_then_rethrows_original()
    {
        var fixture = new Fixture
        {
            RemoveException = new InvalidOperationException("unexpected remove"),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.RemoveAsync(
                fixture.Target,
                fixture.RegistryBefore,
                default));

        Assert.Equal("unexpected remove", exception.Message);
        Assert.Equal("present", fixture.TargetSnapshotMarker);
        Assert.Equal("restore", fixture.Operations[^1]);
    }

    [Fact]
    public async Task Unknown_helper_exit_suppresses_restore()
    {
        var fixture = new Fixture
        {
            RemoveException = CreateUnknownHelperExitException(),
        };

        var result = await fixture.Coordinator.RemoveAsync(
            fixture.Target,
            fixture.RegistryBefore,
            default);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "Account removal state was not restored because helper process exit could not be verified.",
            result.Message);
        Assert.Equal(["availability", "capture", "remove:target"], fixture.Operations);
    }

    [Fact]
    public async Task Public_coordinator_restores_exact_registry_and_target_snapshot_bytes_on_failure()
    {
        using var directory = new TemporaryDirectory();
        const string authJson =
            "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"test-token\",\"account_id\":\"active-account\"}}";
        const string registryJson =
            "{\"schema_version\":3,\"active_account_key\":\"active-key\",\"accounts\":[" +
            "{\"account_key\":\"active-key\",\"chatgpt_account_id\":\"active-account\",\"chatgpt_user_id\":\"user-a\",\"email\":\"active@example.com\",\"alias\":\"active\"}," +
            "{\"account_key\":\"target-key\",\"chatgpt_account_id\":\"target-account\",\"chatgpt_user_id\":\"user-b\",\"email\":\"target@example.com\",\"alias\":\"target\"}]}";
        const string mutatedRegistryJson =
            "{\"schema_version\":3,\"active_account_key\":\"active-key\",\"accounts\":[]}";
        const string targetSnapshot = "target-snapshot-byte-content";
        directory.Write("auth.json", authJson);
        directory.Write("accounts/registry.json", registryJson);
        directory.Write("accounts/target-key.auth.json", targetSnapshot);
        directory.Write("codex-auth.exe", string.Empty);
        var authPath = Path.Combine(directory.Path, "auth.json");
        var registryPath = Path.Combine(directory.Path, "accounts", "registry.json");
        var snapshotPath = Path.Combine(directory.Path, "accounts", "target-key.auth.json");
        var expectedAuthBytes = File.ReadAllBytes(authPath);
        var expectedRegistryBytes = File.ReadAllBytes(registryPath);
        var expectedSnapshotBytes = File.ReadAllBytes(snapshotPath);
        var runner = new MutatingRemoveRunner(() =>
        {
            File.WriteAllText(authPath, "{\"mutated\":true}");
            File.WriteAllText(registryPath, mutatedRegistryJson);
            File.Delete(snapshotPath);
        });
        var authService = new CodexAuthService(
            Path.Combine(directory.Path, "codex-auth.exe"),
            directory.Path,
            runner);
        var active = Accounts.Record("active-key", "active@example.com", "active", "active-account");
        var target = Accounts.Record("target-key", "target@example.com", "target", "target-account");
        var before = new AccountRegistry(3, active.AccountKey, [active, target]);
        var coordinator = new TargetedRemoveCoordinator(
            directory.Path,
            authService,
            new AccountRegistryService());

        var result = await coordinator.RemoveAsync(target, before, default);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedAuthBytes, File.ReadAllBytes(authPath));
        Assert.Equal(expectedRegistryBytes, File.ReadAllBytes(registryPath));
        Assert.Equal(expectedSnapshotBytes, File.ReadAllBytes(snapshotPath));
        Assert.Equal(["remove", "target"], runner.LastRequest!.Arguments);
    }

    [Fact]
    public async Task Successful_helper_that_leaves_target_snapshot_restores_all_checkpointed_files()
    {
        using var directory = new TemporaryDirectory();
        const string authJson =
            "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"test-token\",\"account_id\":\"active-account\"}}";
        const string registryJson =
            "{\"schema_version\":3,\"active_account_key\":\"active-key\",\"accounts\":[" +
            "{\"account_key\":\"active-key\",\"chatgpt_account_id\":\"active-account\",\"chatgpt_user_id\":\"user-a\",\"email\":\"active@example.com\",\"alias\":\"active\"}," +
            "{\"account_key\":\"target-key\",\"chatgpt_account_id\":\"target-account\",\"chatgpt_user_id\":\"user-b\",\"email\":\"target@example.com\",\"alias\":\"target\"}]}";
        const string registryAfterRemove =
            "{\"schema_version\":3,\"active_account_key\":\"active-key\",\"accounts\":[" +
            "{\"account_key\":\"active-key\",\"chatgpt_account_id\":\"active-account\",\"chatgpt_user_id\":\"user-a\",\"email\":\"active@example.com\",\"alias\":\"active\"}]}";
        const string targetSnapshot = "target-snapshot-byte-content";
        directory.Write("auth.json", authJson);
        directory.Write("accounts/registry.json", registryJson);
        directory.Write("accounts/target-key.auth.json", targetSnapshot);
        directory.Write("codex-auth.exe", string.Empty);
        var authPath = Path.Combine(directory.Path, "auth.json");
        var registryPath = Path.Combine(directory.Path, "accounts", "registry.json");
        var snapshotPath = Path.Combine(directory.Path, "accounts", "target-key.auth.json");
        var expectedAuthBytes = File.ReadAllBytes(authPath);
        var expectedRegistryBytes = File.ReadAllBytes(registryPath);
        var expectedSnapshotBytes = File.ReadAllBytes(snapshotPath);
        var runner = new MutatingRemoveRunner(
            () => File.WriteAllText(registryPath, registryAfterRemove),
            new CommandResult(0, string.Empty, string.Empty));
        var authService = new CodexAuthService(
            Path.Combine(directory.Path, "codex-auth.exe"),
            directory.Path,
            runner);
        var active = Accounts.Record("active-key", "active@example.com", "active", "active-account");
        var target = Accounts.Record("target-key", "target@example.com", "target", "target-account");
        var before = new AccountRegistry(3, active.AccountKey, [active, target]);
        var coordinator = new TargetedRemoveCoordinator(
            directory.Path,
            authService,
            new AccountRegistryService());

        var result = await coordinator.RemoveAsync(target, before, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Account removal failed. The prior account state was restored.", result.Message);
        Assert.Equal(expectedAuthBytes, File.ReadAllBytes(authPath));
        Assert.Equal(expectedRegistryBytes, File.ReadAllBytes(registryPath));
        Assert.Equal(expectedSnapshotBytes, File.ReadAllBytes(snapshotPath));
    }

    [Fact]
    public async Task Snapshot_absence_operational_error_restores_all_checkpointed_state()
    {
        var codexHome = Path.Combine("test", "codex-home");
        var authPath = Path.Combine(codexHome, "auth.json");
        var registryPath = Path.Combine(codexHome, "accounts", "registry.json");
        var snapshotPath = Path.Combine(codexHome, "accounts", "target-key.auth.json");
        byte[] expectedAuthBytes = [1, 2, 3];
        byte[] expectedRegistryBytes = [4, 5, 6];
        byte[] expectedSnapshotBytes = [7, 8, 9];
        byte[] registryAfterRemoveBytes = [10, 11, 12];
        var fileSystem = new FakeRemovalFileSystem(
            (authPath, expectedAuthBytes),
            (registryPath, expectedRegistryBytes),
            (snapshotPath, expectedSnapshotBytes));
        fileSystem.ReadFailurePath = snapshotPath;
        var checkpoint = (IRemovalStateCheckpoint)Activator.CreateInstance(
            typeof(RemovalStateTransaction),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args:
            [
                authPath,
                expectedAuthBytes.ToArray(),
                registryPath,
                expectedRegistryBytes.ToArray(),
                snapshotPath,
                expectedSnapshotBytes.ToArray(),
                fileSystem,
            ],
            culture: null)!;
        var active = Accounts.Record("active-key", "active@example.com", "active", "active-account");
        var target = Accounts.Record("target-key", "target@example.com", "target", "target-account");
        var before = new AccountRegistry(3, active.AccountKey, [active, target]);
        var after = new AccountRegistry(3, active.AccountKey, [active]);
        var coordinator = new TargetedRemoveCoordinator(
            codexHome,
            (selector, cancellationToken) =>
            {
                fileSystem.Set(registryPath, registryAfterRemoveBytes);
                return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
            },
            (home, cancellationToken) => Task.FromResult(after),
            (home, cancellationToken) => Task.FromResult("active-account"),
            (home, accountKey, cancellationToken) =>
                Task.FromResult(checkpoint),
            () => new HelperAvailability(true, @"C:\tools\codex-auth.exe", string.Empty));

        var result = await coordinator.RemoveAsync(target, before, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Account removal failed. The prior account state was restored.", result.Message);
        Assert.Equal(expectedAuthBytes, fileSystem.Get(authPath));
        Assert.Equal(expectedRegistryBytes, fileSystem.Get(registryPath));
        Assert.Equal(expectedSnapshotBytes, fileSystem.Get(snapshotPath));
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Active = Accounts.Record("active-key", "active@example.com", "active", "active-account");
            Target = Accounts.Record("target-key", "target@example.com", "target", "target-account");
            RegistryBefore = new AccountRegistry(3, Active.AccountKey, [Active, Target]);
            RegistryAfterRemove = new AccountRegistry(3, Active.AccountKey, [Active]);
            Checkpoint = new FakeCheckpoint(
                Operations,
                () =>
                {
                    RegistryMarker = "prior-registry";
                    TargetSnapshotMarker = "present";
                    LiveAuthMarker = "live-auth-prior";
                });
            Coordinator = new TargetedRemoveCoordinator(
                "test-codex-home",
                RemoveAsync,
                LoadRegistryAsync,
                ReadAuthAccountIdAsync,
                CaptureAsync,
                CheckAvailability);
        }

        public List<string> Operations { get; } = [];

        public AccountRecord Active { get; }

        public AccountRecord Target { get; }

        public AccountRegistry RegistryBefore { get; }

        public AccountRegistry RegistryAfterRemove { get; set; }

        public CommandResult RemoveResult { get; init; } = new(0, string.Empty, string.Empty);

        public HelperAvailability Availability { get; set; } =
            new(true, @"C:\tools\codex-auth.exe", string.Empty);

        public HelperAvailability? AvailabilityAfterRemove { get; init; }

        public int AvailabilityCheckCount { get; private set; }

        public Exception? RemoveException { get; init; }

        public CancellationTokenSource? CancelDuringRemove { get; init; }

        public string AuthAccountIdAfterRemove { get; set; } = "active-account";

        public string RegistryMarker { get; private set; } = "prior-registry";

        public string TargetSnapshotMarker { get; private set; } = "present";

        public string LiveAuthMarker { get; private set; } = "live-auth-prior";

        public FakeCheckpoint Checkpoint { get; }

        public TargetedRemoveCoordinator Coordinator { get; }

        private HelperAvailability CheckAvailability()
        {
            AvailabilityCheckCount++;
            if (AvailabilityCheckCount == 1)
            {
                Operations.Add("availability");
            }

            return Availability;
        }

        private Task<IRemovalStateCheckpoint> CaptureAsync(
            string codexHome,
            string accountKey,
            CancellationToken cancellationToken)
        {
            Operations.Add("capture");
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Target.AccountKey, accountKey);
            return Task.FromResult<IRemovalStateCheckpoint>(Checkpoint);
        }

        private Task<CommandResult> RemoveAsync(string selector, CancellationToken cancellationToken)
        {
            Operations.Add($"remove:{selector}");
            RegistryMarker = "removed-registry";
            TargetSnapshotMarker = "deleted";
            if (AvailabilityAfterRemove is not null)
            {
                Availability = AvailabilityAfterRemove;
            }

            if (RemoveException is not null)
            {
                throw RemoveException;
            }

            CancelDuringRemove?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RemoveResult);
        }

        private Task<AccountRegistry> LoadRegistryAsync(
            string codexHome,
            CancellationToken cancellationToken)
        {
            Operations.Add("reload");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RegistryAfterRemove);
        }

        private Task<string> ReadAuthAccountIdAsync(
            string codexHome,
            CancellationToken cancellationToken)
        {
            Operations.Add("verify-auth");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuthAccountIdAfterRemove);
        }
    }

    private static Exception CreateUnknownHelperExitException()
    {
        var exceptionType = typeof(TargetedRemoveCoordinator).Assembly.GetType(
            "CodexAccountSwitcher.Services.HelperProcessExitUnverifiedException");
        Assert.NotNull(exceptionType);
        return Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType));
    }

    private static HelperAvailability MissingAvailability() => new(
        false,
        @"C:\expected\tools\codex-auth.exe",
        @"The codex-auth helper is unavailable at the expected path: C:\expected\tools\codex-auth.exe");

    private sealed class FakeCheckpoint(
        ICollection<string> operations,
        Action restore) : IRemovalStateCheckpoint
    {
        public bool RestoreSucceeded { get; set; } = true;

        public bool AuthUnchanged { get; set; } = true;

        public CancellationToken RestoreToken { get; private set; }

        public Task<bool> RestoreAndVerifyAsync(CancellationToken cancellationToken)
        {
            operations.Add("restore");
            RestoreToken = cancellationToken;
            restore();
            return Task.FromResult(RestoreSucceeded);
        }

        public Task<bool> VerifyAuthUnchangedAsync(CancellationToken cancellationToken)
        {
            operations.Add("verify-auth-bytes");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuthUnchanged);
        }

        public Task<bool> VerifyTargetSnapshotAbsentAsync(CancellationToken cancellationToken)
        {
            operations.Add("verify-snapshot-absent");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRemovalFileSystem(
        params (string Path, byte[] Bytes)[] files) : IAuthStateFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = files.ToDictionary(
            file => file.Path,
            file => file.Bytes.ToArray(),
            StringComparer.Ordinal);

        public string? ReadFailurePath { get; set; }

        public IReadOnlyList<string> EnumerateAccountSnapshotPaths(string accountsPath) =>
            throw new InvalidOperationException("Removal transactions do not enumerate account snapshots.");

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(path, ReadFailurePath, StringComparison.Ordinal))
            {
                ReadFailurePath = null;
                throw new IOException("simulated snapshot read failure");
            }

            return Task.FromResult(
                _files.TryGetValue(path, out var bytes) ? bytes.ToArray() : null);
        }

        public Task WriteAtomicallyAsync(
            string path,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files[path] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files.Remove(path);
            return Task.CompletedTask;
        }

        public byte[] Get(string path) => _files[path].ToArray();

        public void Set(string path, byte[] bytes) => _files[path] = bytes.ToArray();
    }

    private sealed class MutatingRemoveRunner(
        Action mutate,
        CommandResult? result = null) : IProcessRunner
    {
        public ProcessRequest? LastRequest { get; private set; }

        public Task<CommandResult> RunCapturedAsync(
            ProcessRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            mutate();
            return Task.FromResult(result ?? CommandResult.Failed("simulated remove failure"));
        }

        public Task<CommandResult> RunVisibleAsync(
            ProcessRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Targeted removal must not use the visible helper picker.");
    }
}
