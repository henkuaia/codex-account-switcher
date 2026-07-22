using System.IO;
using System.Security.Cryptography;

namespace CodexAccountSwitcher.Services;

internal sealed class RemovalStateCheckpointException()
    : InvalidOperationException("Account removal state checkpoint failed.");

internal sealed class RemovalStateTransaction : IRemovalStateCheckpoint
{
    private readonly string _authPath;
    private readonly string _registryPath;
    private readonly string _snapshotPath;
    private readonly IAuthStateFileSystem _fileSystem;
    private byte[]? _authBytes;
    private byte[]? _registryBytes;
    private byte[]? _snapshotBytes;
    private readonly bool _authExisted;
    private readonly bool _registryExisted;
    private readonly bool _snapshotExisted;
    private bool _disposed;

    private RemovalStateTransaction(
        string authPath,
        byte[]? authBytes,
        string registryPath,
        byte[]? registryBytes,
        string snapshotPath,
        byte[]? snapshotBytes,
        IAuthStateFileSystem fileSystem)
    {
        _authPath = authPath;
        _authBytes = authBytes;
        _registryPath = registryPath;
        _registryBytes = registryBytes;
        _snapshotPath = snapshotPath;
        _snapshotBytes = snapshotBytes;
        _fileSystem = fileSystem;
        _authExisted = authBytes is not null;
        _registryExisted = registryBytes is not null;
        _snapshotExisted = snapshotBytes is not null;
    }

    public static async Task<RemovalStateTransaction> CaptureAsync(
        string codexHome,
        string accountKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);

        var fileSystem = new AuthStateFileSystem();
        byte[]? authBytes = null;
        byte[]? registryBytes = null;
        byte[]? snapshotBytes = null;
        var ownershipTransferred = false;
        try
        {
            var authPath = Path.Combine(codexHome, "auth.json");
            var registryPath = Path.Combine(codexHome, "accounts", "registry.json");
            var snapshotPath = AccountSnapshotPathResolver.Resolve(codexHome, accountKey);
            authBytes = await fileSystem.ReadAsync(authPath, cancellationToken);
            registryBytes = await fileSystem.ReadAsync(registryPath, cancellationToken);
            snapshotBytes = await fileSystem.ReadAsync(snapshotPath, cancellationToken);
            var transaction = new RemovalStateTransaction(
                authPath,
                authBytes,
                registryPath,
                registryBytes,
                snapshotPath,
                snapshotBytes,
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
            throw new RemovalStateCheckpointException();
        }
        finally
        {
            if (!ownershipTransferred)
            {
                Clear(authBytes);
                Clear(registryBytes);
                Clear(snapshotBytes);
            }
        }
    }

    public async Task<bool> RestoreAndVerifyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authRestored = await TryRestoreAsync(_authPath, _authExisted, _authBytes, cancellationToken);
        var registryRestored = await TryRestoreAsync(
            _registryPath,
            _registryExisted,
            _registryBytes,
            cancellationToken);
        var snapshotRestored = await TryRestoreAsync(
            _snapshotPath,
            _snapshotExisted,
            _snapshotBytes,
            cancellationToken);
        var authMatches = await TryMatchesAsync(_authPath, _authExisted, _authBytes, cancellationToken);
        var registryMatches = await TryMatchesAsync(
            _registryPath,
            _registryExisted,
            _registryBytes,
            cancellationToken);
        var snapshotMatches = await TryMatchesAsync(
            _snapshotPath,
            _snapshotExisted,
            _snapshotBytes,
            cancellationToken);
        return authRestored && registryRestored && snapshotRestored &&
            authMatches && registryMatches && snapshotMatches;
    }

    public Task<bool> VerifyAuthUnchangedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return TryMatchesAsync(_authPath, _authExisted, _authBytes, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear(_authBytes);
        Clear(_registryBytes);
        Clear(_snapshotBytes);
        _authBytes = null;
        _registryBytes = null;
        _snapshotBytes = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

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

    private async Task<bool> TryMatchesAsync(
        string path,
        bool existed,
        byte[]? expected,
        CancellationToken cancellationToken)
    {
        byte[]? current = null;
        try
        {
            current = await _fileSystem.ReadAsync(path, cancellationToken);
            return existed
                ? current is not null && CryptographicOperations.FixedTimeEquals(expected!, current)
                : current is null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return false;
        }
        finally
        {
            Clear(current);
        }
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException;

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
