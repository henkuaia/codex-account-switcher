using System.IO;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record TokenUsageLedgerLoadResult(
    IReadOnlyDictionary<string, LocalUsageFileCheckpoint> FileCheckpoints,
    string? Error);

public sealed class TokenUsageLedgerService
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private bool _saveBlocked;

    public TokenUsageLedgerService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static TokenUsageLedgerService CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new TokenUsageLedgerService(Path.Combine(
            localAppData,
            "CodexAccountSwitcher",
            "token-usage-ledger.json"));
    }

    public async Task<TokenUsageLedgerLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            _saveBlocked = false;
            return Empty();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<LedgerDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            if (document?.SchemaVersion != CurrentSchemaVersion ||
                document.Files is null)
            {
                return Blocked("Token 统计账本无效，原文件已保留。");
            }

            var checkpoints = new Dictionary<string, LocalUsageFileCheckpoint>(
                StringComparer.Ordinal);
            foreach (var (path, file) in document.Files)
            {
                if (file?.Checkpoint is null ||
                    file.TokenBuckets is null ||
                    !string.Equals(
                        path,
                        file.Checkpoint.RelativePath,
                        StringComparison.Ordinal) ||
                    file.TokenBuckets.Count != file.Checkpoint.Buckets.Count)
                {
                    return Blocked("Token 统计账本无效，原文件已保留。");
                }

                var tokenBuckets = file.TokenBuckets.ToDictionary(
                    bucket => bucket.BucketStartUtc);
                var buckets = new LocalUsageBucket[file.Checkpoint.Buckets.Count];
                for (var index = 0; index < buckets.Length; index++)
                {
                    var bucket = file.Checkpoint.Buckets[index];
                    if (!tokenBuckets.TryGetValue(bucket.BucketStartUtc, out var tokens) ||
                        tokens.Input < 0 ||
                        tokens.Cached < 0 ||
                        tokens.Cached > tokens.Input ||
                        tokens.Output < 0)
                    {
                        return Blocked("Token 统计账本无效，原文件已保留。");
                    }

                    buckets[index] = bucket with
                    {
                        InputTokens = tokens.Input,
                        CachedInputTokens = tokens.Cached,
                        OutputTokens = tokens.Output,
                    };
                }

                checkpoints[path] = file.Checkpoint with { Buckets = buckets };
            }

            _saveBlocked = false;
            return new TokenUsageLedgerLoadResult(checkpoints, null);
        }
        catch (JsonException)
        {
            return Blocked("Token 统计账本无效，原文件已保留。");
        }
        catch (IOException)
        {
            return Blocked("Token 统计账本暂时无法读取。");
        }
        catch (UnauthorizedAccessException)
        {
            return Blocked("Token 统计账本暂时无法读取。");
        }
        catch (ArgumentException)
        {
            return Blocked("Token 统计账本无效，原文件已保留。");
        }
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, LocalUsageFileCheckpoint> checkpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (_saveBlocked)
        {
            throw new InvalidOperationException("The existing token usage ledger cannot be overwritten.");
        }

        var document = new LedgerDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Files = checkpoints.ToDictionary(
                pair => pair.Key,
                pair => new FileDocument
                {
                    Checkpoint = pair.Value,
                    TokenBuckets = pair.Value.Buckets
                        .Select(bucket => new TokenBucketDocument
                        {
                            BucketStartUtc = bucket.BucketStartUtc,
                            Input = bucket.InputTokens,
                            Cached = bucket.CachedInputTokens,
                            Output = bucket.OutputTokens,
                        })
                        .ToList(),
                },
                StringComparer.Ordinal),
        };
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
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
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static TokenUsageLedgerLoadResult Empty() =>
        new(
            new Dictionary<string, LocalUsageFileCheckpoint>(StringComparer.Ordinal),
            null);

    private TokenUsageLedgerLoadResult Blocked(string error)
    {
        _saveBlocked = true;
        return new TokenUsageLedgerLoadResult(
            new Dictionary<string, LocalUsageFileCheckpoint>(StringComparer.Ordinal),
            error);
    }

    private sealed class LedgerDocument
    {
        public int SchemaVersion { get; set; }

        public Dictionary<string, FileDocument>? Files { get; set; }
    }

    private sealed class FileDocument
    {
        public LocalUsageFileCheckpoint? Checkpoint { get; set; }

        public List<TokenBucketDocument>? TokenBuckets { get; set; }
    }

    private sealed class TokenBucketDocument
    {
        public DateTimeOffset BucketStartUtc { get; set; }

        public long Input { get; set; }

        public long Cached { get; set; }

        public long Output { get; set; }
    }
}
