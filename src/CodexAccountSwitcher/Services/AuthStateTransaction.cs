using System.IO;
using System.Security.Cryptography;

namespace CodexAccountSwitcher.Services;

internal interface IAuthStateCheckpoint : IDisposable
{
    Task<bool> RestoreAndVerifyAsync(CancellationToken cancellationToken);
}

internal interface IAuthStateFileSystem
{
    IReadOnlyList<string> EnumerateAccountSnapshotPaths(string accountsPath);

    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken);

    Task WriteAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);

    Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken);
}

internal sealed class AuthStateCheckpointException()
    : InvalidOperationException("Authentication state checkpoint failed.");

internal sealed class AuthStateTransaction : IAuthStateCheckpoint
{
    private readonly string _authPath;
    private readonly string _registryPath;
    private readonly string _accountsPath;
    private readonly IAuthStateFileSystem _fileSystem;
    private byte[]? _authBytes;
    private byte[]? _registryBytes;
    private readonly Dictionary<string, byte[]> _accountSnapshots;
    private readonly bool _authExisted;
    private readonly bool _registryExisted;
    private bool _disposed;

    private AuthStateTransaction(
        string authPath,
        byte[]? authBytes,
        string registryPath,
        byte[]? registryBytes,
        string accountsPath,
        Dictionary<string, byte[]> accountSnapshots,
        IAuthStateFileSystem fileSystem)
    {
        _authPath = authPath;
        _authBytes = authBytes;
        _registryPath = registryPath;
        _registryBytes = registryBytes;
        _accountsPath = accountsPath;
        _accountSnapshots = accountSnapshots;
        _fileSystem = fileSystem;
        _authExisted = authBytes is not null;
        _registryExisted = registryBytes is not null;
    }

    public static Task<AuthStateTransaction> CaptureAsync(
        string codexHome,
        CancellationToken cancellationToken) =>
        CaptureAsync(codexHome, new AuthStateFileSystem(), cancellationToken);

    internal static async Task<AuthStateTransaction> CaptureAsync(
        string codexHome,
        IAuthStateFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        ArgumentNullException.ThrowIfNull(fileSystem);

        byte[]? authBytes = null;
        byte[]? registryBytes = null;
        var accountSnapshots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var ownershipTransferred = false;
        try
        {
            var authPath = Path.Combine(codexHome, "auth.json");
            var accountsPath = Path.Combine(codexHome, "accounts");
            var registryPath = Path.Combine(accountsPath, "registry.json");
            authBytes = await fileSystem.ReadAsync(authPath, cancellationToken);
            registryBytes = await fileSystem.ReadAsync(registryPath, cancellationToken);
            foreach (var snapshotPath in fileSystem.EnumerateAccountSnapshotPaths(accountsPath))
            {
                var snapshotBytes = await fileSystem.ReadAsync(snapshotPath, cancellationToken);
                if (snapshotBytes is null)
                {
                    throw new IOException("An account snapshot changed during checkpoint capture.");
                }

                accountSnapshots.Add(snapshotPath, snapshotBytes);
            }

            var transaction = new AuthStateTransaction(
                authPath,
                authBytes,
                registryPath,
                registryBytes,
                accountsPath,
                accountSnapshots,
                fileSystem);
            ownershipTransferred = true;
            return transaction;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw new AuthStateCheckpointException();
        }
        finally
        {
            if (!ownershipTransferred)
            {
                Clear(authBytes);
                Clear(registryBytes);
                Clear(accountSnapshots.Values);
            }
        }
    }

    public async Task<bool> RestoreAndVerifyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authRestored = await TryRestoreAsync(
            _authPath,
            _authExisted,
            _authBytes,
            cancellationToken);
        var registryRestored = await TryRestoreAsync(
            _registryPath,
            _registryExisted,
            _registryBytes,
            cancellationToken);
        var snapshotsRestored = await TryRestoreAccountSnapshotsAsync(cancellationToken);

        byte[]? currentAuth = null;
        byte[]? currentRegistry = null;
        try
        {
            var authRead = await TryReadAsync(_authPath, cancellationToken);
            currentAuth = authRead.Bytes;
            var registryRead = await TryReadAsync(_registryPath, cancellationToken);
            currentRegistry = registryRead.Bytes;
            var snapshotsMatch = await TryAccountSnapshotsMatchAsync(cancellationToken);

            return authRestored &&
                registryRestored &&
                snapshotsRestored &&
                authRead.Succeeded &&
                registryRead.Succeeded &&
                Matches(_authExisted, _authBytes, currentAuth) &&
                Matches(_registryExisted, _registryBytes, currentRegistry) &&
                snapshotsMatch;
        }
        finally
        {
            Clear(currentAuth);
            Clear(currentRegistry);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear(_authBytes);
        Clear(_registryBytes);
        Clear(_accountSnapshots.Values);
        _accountSnapshots.Clear();
        _authBytes = null;
        _registryBytes = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public override string ToString() => nameof(AuthStateTransaction);

    private async Task<bool> TryRestoreAsync(
        string path,
        bool existed,
        byte[]? bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            if (existed)
            {
                await _fileSystem.WriteAtomicallyAsync(path, bytes!, cancellationToken);
            }
            else
            {
                await _fileSystem.DeleteIfExistsAsync(path, cancellationToken);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return false;
        }
    }

    private async Task<(bool Succeeded, byte[]? Bytes)> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, await _fileSystem.ReadAsync(path, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return (false, null);
        }
    }

    private async Task<bool> TryRestoreAccountSnapshotsAsync(CancellationToken cancellationToken)
    {
        var restored = true;
        foreach (var snapshot in _accountSnapshots)
        {
            restored = await TryRestoreAsync(
                snapshot.Key,
                existed: true,
                snapshot.Value,
                cancellationToken) && restored;
        }

        IReadOnlyList<string> currentSnapshotPaths;
        try
        {
            currentSnapshotPaths = _fileSystem.EnumerateAccountSnapshotPaths(_accountsPath);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return false;
        }

        foreach (var snapshotPath in currentSnapshotPaths)
        {
            if (!_accountSnapshots.ContainsKey(snapshotPath))
            {
                restored = await TryRestoreAsync(
                    snapshotPath,
                    existed: false,
                    bytes: null,
                    cancellationToken) && restored;
            }
        }

        return restored;
    }

    private async Task<bool> TryAccountSnapshotsMatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> currentSnapshotPaths;
        try
        {
            currentSnapshotPaths = _fileSystem.EnumerateAccountSnapshotPaths(_accountsPath);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return false;
        }

        if (currentSnapshotPaths.Count != _accountSnapshots.Count ||
            currentSnapshotPaths.Any(path => !_accountSnapshots.ContainsKey(path)))
        {
            return false;
        }

        foreach (var snapshot in _accountSnapshots)
        {
            byte[]? current = null;
            try
            {
                var read = await TryReadAsync(snapshot.Key, cancellationToken);
                current = read.Bytes;
                if (!read.Succeeded || !Matches(existed: true, snapshot.Value, current))
                {
                    return false;
                }
            }
            finally
            {
                Clear(current);
            }
        }

        return true;
    }

    private static bool Matches(bool existed, byte[]? expected, byte[]? actual) =>
        existed
            ? actual is not null && CryptographicOperations.FixedTimeEquals(expected!, actual)
            : actual is null;

    private static bool IsFileFailure(Exception exception) =>
        exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException;

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void Clear(IEnumerable<byte[]> buffers)
    {
        foreach (var buffer in buffers)
        {
            Clear(buffer);
        }
    }
}

internal sealed class AuthStateFileSystem : IAuthStateFileSystem
{
    private readonly Action<string, string> _replaceFile;

    public AuthStateFileSystem()
        : this(static (source, destination) => File.Move(source, destination, overwrite: true))
    {
    }

    internal AuthStateFileSystem(Action<string, string> replaceFile) =>
        _replaceFile = replaceFile ?? throw new ArgumentNullException(nameof(replaceFile));

    public IReadOnlyList<string> EnumerateAccountSnapshotPaths(string accountsPath)
    {
        try
        {
            return Directory
                .EnumerateFiles(accountsPath, "*.auth.json", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            return bytes;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            throw;
        }
    }

    public async Task WriteAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The authentication state path is invalid.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _replaceFile(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return Task.CompletedTask;
    }
}
