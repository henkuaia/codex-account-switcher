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
            },
        };
        var transaction = await AuthStateTransaction.CaptureAsync("home", fileSystem, default);
        var buffers = typeof(AuthStateTransaction)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(byte[]))
            .Select(field => Assert.IsType<byte[]>(field.GetValue(transaction)))
            .ToArray();

        Assert.DoesNotContain(secret, transaction.ToString(), StringComparison.Ordinal);
        transaction.Dispose();

        Assert.All(buffers, buffer => Assert.All(buffer, value => Assert.Equal((byte)0, value)));
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
