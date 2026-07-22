using System.IO;
using System.Security.Cryptography;

namespace CodexAccountSwitcher.Services;

internal interface ICodexCliStager
{
    Task<string> StageAsync(string cliDirectory, CancellationToken cancellationToken);
}

internal sealed class CodexCliStager : ICodexCliStager
{
    private const string CliFileName = "codex.exe";
    private readonly string _cacheDirectory;

    internal CodexCliStager() : this(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    internal CodexCliStager(string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        _cacheDirectory = Path.Combine(
            Path.GetFullPath(localApplicationDataDirectory),
            "CodexAccountSwitcher",
            "codex-cli");
    }

    public async Task<string> StageAsync(
        string cliDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var sourcePath = Path.Combine(Path.GetFullPath(cliDirectory), CliFileName);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The Codex CLI executable was not found.", sourcePath);
        }

        var sourceHash = await HashFileAsync(sourcePath, cancellationToken);
        var cachedPath = Path.Combine(_cacheDirectory, CliFileName);
        if (File.Exists(cachedPath))
        {
            var cachedHash = await HashFileAsync(cachedPath, cancellationToken);
            if (sourceHash.AsSpan().SequenceEqual(cachedHash))
            {
                return _cacheDirectory;
            }
        }

        Directory.CreateDirectory(_cacheDirectory);
        var temporaryPath = Path.Combine(
            _cacheDirectory,
            $"{CliFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await CopyFileAsync(sourcePath, temporaryPath, cancellationToken);
            var temporaryHash = await HashFileAsync(temporaryPath, cancellationToken);
            if (!sourceHash.AsSpan().SequenceEqual(temporaryHash))
            {
                throw new InvalidDataException("The staged Codex CLI failed hash verification.");
            }

            File.Move(temporaryPath, cachedPath, overwrite: true);
            var cachedHash = await HashFileAsync(cachedPath, cancellationToken);
            if (!sourceHash.AsSpan().SequenceEqual(cachedHash))
            {
                File.Delete(cachedPath);
                throw new InvalidDataException("The cached Codex CLI failed hash verification.");
            }

            return _cacheDirectory;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }
}
