using System.Reflection;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class AuthStateTransactionTests
{
    [Fact]
    public async Task Restore_replaces_both_files_with_exact_checkpoint_bytes()
    {
        using var home = new TemporaryDirectory();
        var originalAuth = new byte[] { 0, 1, 2, 255 };
        var originalRegistry = new byte[] { 9, 8, 7, 0 };
        WriteBytes(home.Path, "auth.json", originalAuth);
        WriteBytes(home.Path, "accounts/registry.json", originalRegistry);
        using var transaction = await AuthStateTransaction.CaptureAsync(home.Path, default);
        WriteBytes(home.Path, "auth.json", [4, 5, 6]);
        WriteBytes(home.Path, "accounts/registry.json", [3, 2, 1]);

        var restored = await transaction.RestoreAndVerifyAsync(default);

        Assert.True(restored);
        Assert.Equal(originalAuth, File.ReadAllBytes(Path.Combine(home.Path, "auth.json")));
        Assert.Equal(originalRegistry, File.ReadAllBytes(Path.Combine(home.Path, "accounts", "registry.json")));
        AssertNoTemporaryFiles(home.Path);
    }

    [Fact]
    public async Task Failed_login_rollback_removes_account_snapshot_created_after_checkpoint()
    {
        using var home = new TemporaryDirectory();
        WriteBytes(home.Path, "accounts/notes.json", [7, 8, 9]);
        using var transaction = await AuthStateTransaction.CaptureForLoginAsync(home.Path, default);
        WriteBytes(home.Path, "accounts/new-account.auth.json", [1, 2, 3]);

        var restored = await transaction.RestoreAndVerifyAsync(default);

        Assert.True(restored);
        Assert.False(File.Exists(Path.Combine(home.Path, "accounts", "new-account.auth.json")));
        Assert.Equal([7, 8, 9], File.ReadAllBytes(Path.Combine(home.Path, "accounts", "notes.json")));
    }

    [Fact]
    public async Task Failed_login_rollback_restores_overwritten_account_snapshot_bytes()
    {
        using var home = new TemporaryDirectory();
        var originalSnapshot = new byte[] { 0, 17, 34, 255 };
        WriteBytes(home.Path, "accounts/existing.auth.json", originalSnapshot);
        using var transaction = await AuthStateTransaction.CaptureForLoginAsync(home.Path, default);
        WriteBytes(home.Path, "accounts/existing.auth.json", [9, 8, 7]);

        var restored = await transaction.RestoreAndVerifyAsync(default);

        Assert.True(restored);
        Assert.Equal(
            originalSnapshot,
            File.ReadAllBytes(Path.Combine(home.Path, "accounts", "existing.auth.json")));
    }

    [Fact]
    public async Task Failed_login_rollback_restores_deleted_account_snapshot_bytes()
    {
        using var home = new TemporaryDirectory();
        var originalSnapshot = new byte[] { 255, 34, 17, 0 };
        WriteBytes(home.Path, "accounts/existing.auth.json", originalSnapshot);
        using var transaction = await AuthStateTransaction.CaptureForLoginAsync(home.Path, default);
        File.Delete(Path.Combine(home.Path, "accounts", "existing.auth.json"));

        var restored = await transaction.RestoreAndVerifyAsync(default);

        Assert.True(restored);
        Assert.Equal(
            originalSnapshot,
            File.ReadAllBytes(Path.Combine(home.Path, "accounts", "existing.auth.json")));
    }

    [Fact]
    public async Task Switch_transaction_capture_ignores_unreadable_account_snapshot()
    {
        var fileSystem = new FakeAuthStateFileSystem
        {
            EnumerationException = new IOException("simulated unreadable snapshot"),
        };

        using var transaction = await AuthStateTransaction.CaptureAsync("home", fileSystem, default);

        Assert.Empty(fileSystem.RestoreOperations);
    }

    [Fact]
    public async Task Failed_switch_rollback_does_not_touch_changed_or_new_account_snapshots()
    {
        var fileSystem = new FakeAuthStateFileSystem
        {
            Files =
            {
                ["home/auth.json"] = [1],
                ["home/accounts/registry.json"] = [2],
                ["home/accounts/existing.auth.json"] = [3],
            },
        };
        using var transaction = await AuthStateTransaction.CaptureAsync("home", fileSystem, default);
        fileSystem.Files["home/accounts/existing.auth.json"] = [4];
        fileSystem.Files["home/accounts/new.auth.json"] = [5];

        var restored = await transaction.RestoreAndVerifyAsync(default);

        Assert.True(restored);
        Assert.Equal([4], fileSystem.Files["home/accounts/existing.auth.json"]);
        Assert.Equal([5], fileSystem.Files["home/accounts/new.auth.json"]);
        Assert.DoesNotContain(
            fileSystem.RestoreOperations,
            operation => operation.EndsWith(".auth.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Restore_deletes_files_that_were_absent_in_checkpoint()
    {
        using var home = new TemporaryDirectory();
        using var transaction = await AuthStateTransaction.CaptureAsync(home.Path, default);
        WriteBytes(home.Path, "auth.json", [1]);
        WriteBytes(home.Path, "accounts/registry.json", [2]);

        var restored = await transaction.RestoreAndVerifyAsync(default);

        Assert.True(restored);
        Assert.False(File.Exists(Path.Combine(home.Path, "auth.json")));
        Assert.False(File.Exists(Path.Combine(home.Path, "accounts", "registry.json")));
    }

    [Fact]
    public async Task Restore_attempts_both_files_and_returns_false_when_pair_verification_fails()
    {
        var fileSystem = new FakeAuthStateFileSystem
        {
            Files =
            {
                ["home/auth.json"] = [1, 2],
                ["home/accounts/registry.json"] = [3, 4],
            },
        };
        using var transaction = await AuthStateTransaction.CaptureAsync("home", fileSystem, default);
        fileSystem.Files["home/auth.json"] = [9];
        fileSystem.Files["home/accounts/registry.json"] = [8];
        fileSystem.CorruptAfterRestorePath = "home/accounts/registry.json";

        var restored = await transaction.RestoreAndVerifyAsync(default);

        Assert.False(restored);
        Assert.Equal(
            ["write:home/auth.json", "write:home/accounts/registry.json"],
            fileSystem.RestoreOperations);
    }

    [Fact]
    public async Task Restore_returns_false_when_account_snapshot_verification_fails()
    {
        var fileSystem = new FakeAuthStateFileSystem
        {
            Files =
            {
                ["home/accounts/existing.auth.json"] = [1, 2, 3],
            },
        };
        using var transaction = await AuthStateTransaction.CaptureForLoginAsync(
            "home",
            fileSystem,
            default);
        fileSystem.Files["home/accounts/existing.auth.json"] = [9];
        fileSystem.CorruptAfterRestorePath = "home/accounts/existing.auth.json";

        var restored = await transaction.RestoreAndVerifyAsync(default);

        Assert.False(restored);
        Assert.Contains(
            "write:home/accounts/existing.auth.json",
            fileSystem.RestoreOperations);
    }

    [Fact]
    public async Task Login_capture_duplicate_snapshot_path_fails_and_clears_every_returned_buffer()
    {
        const string snapshotPath = "home/accounts/existing.auth.json";
        var fileSystem = new FakeAuthStateFileSystem
        {
            Files =
            {
                [snapshotPath] = [1, 2, 3],
            },
            AccountSnapshotPaths = [snapshotPath, snapshotPath],
        };

        var exception = await Assert.ThrowsAsync<AuthStateCheckpointException>(
            () => AuthStateTransaction.CaptureForLoginAsync("home", fileSystem, default));

        Assert.Equal("Authentication state checkpoint failed.", exception.Message);
        Assert.Equal(2, fileSystem.ReturnedBuffers.Count);
        Assert.All(
            fileSystem.ReturnedBuffers,
            buffer => Assert.All(buffer, value => Assert.Equal((byte)0, value)));
    }

    [Fact]
    public async Task Login_capture_case_variant_snapshot_paths_fail_and_clear_every_returned_buffer()
    {
        const string firstPath = "home/accounts/existing.auth.json";
        const string secondPath = "home/accounts/EXISTING.auth.json";
        var fileSystem = new FakeAuthStateFileSystem
        {
            Files =
            {
                [firstPath] = [1, 2, 3],
                [secondPath] = [4, 5, 6],
            },
            AccountSnapshotPaths = [firstPath, secondPath],
        };

        var exception = await Assert.ThrowsAsync<AuthStateCheckpointException>(
            () => AuthStateTransaction.CaptureForLoginAsync("home", fileSystem, default));

        Assert.Equal("Authentication state checkpoint failed.", exception.Message);
        Assert.Equal(2, fileSystem.ReturnedBuffers.Count);
        Assert.All(
            fileSystem.ReturnedBuffers,
            buffer => Assert.All(buffer, value => Assert.Equal((byte)0, value)));
    }

    [Fact]
    public async Task Capture_failure_uses_fixed_message_without_file_contents()
    {
        const string secret = "raw-auth-token-secret";
        var fileSystem = new FakeAuthStateFileSystem
        {
            ReadException = new IOException(secret),
        };

        var exception = await Assert.ThrowsAsync<AuthStateCheckpointException>(
            () => AuthStateTransaction.CaptureAsync("home", fileSystem, default));

        Assert.Equal("Authentication state checkpoint failed.", exception.Message);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_unexpected_second_read_failure_clears_first_owned_buffer_before_rethrow()
    {
        var fileSystem = new FakeAuthStateFileSystem
        {
            Files =
            {
                ["home/auth.json"] = [1, 2, 3, 4],
                ["home/accounts/registry.json"] = [5, 6, 7, 8],
            },
            SecondReadException = new UnexpectedTestException("unexpected-second-read"),
        };

        var exception = await Assert.ThrowsAsync<UnexpectedTestException>(
            () => AuthStateTransaction.CaptureAsync("home", fileSystem, default));

        Assert.Equal("unexpected-second-read", exception.Message);
        Assert.NotNull(fileSystem.FirstReturnedBuffer);
        Assert.All(fileSystem.FirstReturnedBuffer, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task Atomic_replace_failure_removes_temp_file_and_preserves_destination()
    {
        using var home = new TemporaryDirectory();
        var authPath = Path.Combine(home.Path, "auth.json");
        var registryPath = Path.Combine(home.Path, "accounts", "registry.json");
        WriteBytes(home.Path, "auth.json", [1, 2]);
        WriteBytes(home.Path, "accounts/registry.json", [3, 4]);
        var fileSystem = new AuthStateFileSystem((source, destination) =>
        {
            if (string.Equals(destination, authPath, StringComparison.Ordinal))
            {
                throw new IOException("injected-move-failure");
            }

            File.Move(source, destination, overwrite: true);
        });
        using var transaction = await AuthStateTransaction.CaptureAsync(home.Path, fileSystem, default);
        WriteBytes(home.Path, "auth.json", [9]);
        WriteBytes(home.Path, "accounts/registry.json", [8]);

        var restored = await transaction.RestoreAndVerifyAsync(default);

        Assert.False(restored);
        Assert.Equal([9], File.ReadAllBytes(authPath));
        Assert.Equal([3, 4], File.ReadAllBytes(registryPath));
        AssertNoTemporaryFiles(home.Path);
    }

    [Fact]
    public async Task Dispose_clears_owned_checkpoint_buffers_and_does_not_render_contents()
    {
        const string secret = "checkpoint-secret-value";
        var fileSystem = new FakeAuthStateFileSystem
        {
            Files =
            {
                ["home/auth.json"] = System.Text.Encoding.UTF8.GetBytes(secret),
                ["home/accounts/registry.json"] = [5, 6, 7],
                ["home/accounts/existing.auth.json"] = [8, 9, 10],
            },
        };
        var transaction = await AuthStateTransaction.CaptureForLoginAsync(
            "home",
            fileSystem,
            default);
        var buffers = typeof(AuthStateTransaction)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(byte[]))
            .Select(field => Assert.IsType<byte[]>(field.GetValue(transaction)))
            .ToArray();
        var snapshotBuffers = Assert.IsType<Dictionary<string, byte[]>>(
                typeof(AuthStateTransaction)
                    .GetField("_accountSnapshots", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(transaction))
            .Values
            .ToArray();

        Assert.DoesNotContain(secret, transaction.ToString(), StringComparison.Ordinal);
        transaction.Dispose();

        Assert.All(
            buffers.Concat(snapshotBuffers),
            buffer => Assert.All(buffer, value => Assert.Equal((byte)0, value)));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transaction.RestoreAndVerifyAsync(default));
    }

    private static void WriteBytes(string home, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(home, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void AssertNoTemporaryFiles(string home) =>
        Assert.Empty(Directory.EnumerateFiles(home, ".*.tmp", SearchOption.AllDirectories));

    private sealed class FakeAuthStateFileSystem : IAuthStateFileSystem
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<string> RestoreOperations { get; } = [];

        public Exception? ReadException { get; init; }

        public Exception? SecondReadException { get; init; }

        public byte[]? FirstReturnedBuffer { get; private set; }

        public string? CorruptAfterRestorePath { get; set; }

        public Exception? EnumerationException { get; set; }

        public IReadOnlyList<string>? AccountSnapshotPaths { get; set; }

        public List<byte[]> ReturnedBuffers { get; } = [];

        public IReadOnlyList<string> EnumerateAccountSnapshotPaths(string accountsPath)
        {
            if (EnumerationException is not null)
            {
                throw EnumerationException;
            }

            if (AccountSnapshotPaths is not null)
            {
                return AccountSnapshotPaths;
            }

            var normalizedAccountsPath = Normalize(accountsPath).TrimEnd('/') + "/";
            return Files.Keys
                .Where(path => path.StartsWith(normalizedAccountsPath, StringComparison.Ordinal) &&
                    !path[normalizedAccountsPath.Length..].Contains('/') &&
                    path.EndsWith(".auth.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _readCount++;
            if (ReadException is not null)
            {
                throw ReadException;
            }

            if (_readCount == 2 && SecondReadException is not null)
            {
                throw SecondReadException;
            }

            if (!Files.TryGetValue(Normalize(path), out var bytes))
            {
                return Task.FromResult<byte[]?>(null);
            }

            var returned = bytes.ToArray();
            ReturnedBuffers.Add(returned);
            if (_readCount == 1)
            {
                FirstReturnedBuffer = returned;
            }

            return Task.FromResult<byte[]?>(returned);
        }

        public Task WriteAtomicallyAsync(
            string path,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Normalize(path);
            RestoreOperations.Add($"write:{normalized}");
            Files[normalized] = bytes.ToArray();
            if (string.Equals(normalized, CorruptAfterRestorePath, StringComparison.Ordinal))
            {
                Files[normalized] = [0];
            }

            return Task.CompletedTask;
        }

        public Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Normalize(path);
            RestoreOperations.Add($"delete:{normalized}");
            Files.Remove(normalized);
            return Task.CompletedTask;
        }

        private static string Normalize(string path) => path.Replace('\\', '/');

        private int _readCount;
    }

    private sealed class UnexpectedTestException(string message) : Exception(message);
}
