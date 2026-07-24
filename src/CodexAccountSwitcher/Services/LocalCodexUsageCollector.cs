using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record LocalUsageCollectionResult(
    IReadOnlyList<LocalUsageEvent> Events,
    int InvalidLineCount)
{
    public IReadOnlyList<LocalUsageAggregate> Aggregates { get; init; } =
        Array.Empty<LocalUsageAggregate>();

    public IReadOnlyDictionary<string, LocalUsageFileCheckpoint> FileCheckpoints { get; init; } =
        new Dictionary<string, LocalUsageFileCheckpoint>(StringComparer.Ordinal);

    public int SkippedFileCount { get; init; }

    public long ParsedByteCount { get; init; }

    public bool HasCheckpointChanges { get; init; }

    public bool IsComplete => InvalidLineCount == 0 && SkippedFileCount == 0;
}

public sealed class LocalCodexUsageCollector
{
    private const int BufferSize = 64 * 1024;
    private const int PrefixProbeLength = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _sessionRoot;
    private readonly CodexCreditRateCard _rateCard;

    public LocalCodexUsageCollector(
        string sessionRoot,
        CodexCreditRateCard? rateCard = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        _sessionRoot = Path.GetFullPath(sessionRoot);
        _rateCard = rateCard ?? new CodexCreditRateCard();
    }

    public Task<LocalUsageCollectionResult> CollectAsync(
        DateTimeOffset earliestUtc,
        CancellationToken cancellationToken) =>
        CollectAsync(
            earliestUtc,
            new Dictionary<string, LocalUsageFileCheckpoint>(StringComparer.Ordinal),
            cancellationToken);

    public async Task<LocalUsageCollectionResult> CollectAsync(
        DateTimeOffset earliestUtc,
        IReadOnlyDictionary<string, LocalUsageFileCheckpoint> checkpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        cancellationToken.ThrowIfCancellationRequested();

        var newEvents = new List<LocalUsageEvent>();
        var updatedCheckpoints = new Dictionary<string, LocalUsageFileCheckpoint>(
            StringComparer.Ordinal);
        var invalidLineCount = 0;
        var skippedFileCount = 0;
        long parsedByteCount = 0;

        if (!Directory.Exists(_sessionRoot))
        {
            return new LocalUsageCollectionResult(newEvents, invalidLineCount)
            {
                Aggregates = Array.Empty<LocalUsageAggregate>(),
                FileCheckpoints = updatedCheckpoints,
                SkippedFileCount = Math.Max(1, checkpoints.Count),
                HasCheckpointChanges = checkpoints.Count > 0,
            };
        }

        var enumeration = EnumerateSessionFiles(cancellationToken);
        skippedFileCount += enumeration.SkippedInputCount;
        var observedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in enumeration.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = ToRelativePath(path);
            observedPaths.Add(relativePath);
            checkpoints.TryGetValue(relativePath, out var checkpoint);
            try
            {
                var scan = await CollectFileAsync(
                    path,
                    relativePath,
                    earliestUtc,
                    checkpoint,
                    cancellationToken);
                if (scan is null)
                {
                    continue;
                }

                updatedCheckpoints[relativePath] = scan.Checkpoint;
                newEvents.AddRange(scan.NewEvents);
                invalidLineCount += scan.ReportedInvalidLineCount;
                parsedByteCount += scan.ParsedByteCount;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                skippedFileCount++;
                if (checkpoint is not null)
                {
                    var retained = FilterCheckpoint(checkpoint, earliestUtc);
                    if (retained.Aggregates.Count > 0)
                    {
                        updatedCheckpoints[relativePath] = retained;
                        invalidLineCount += retained.InvalidLineCount;
                    }
                }
            }
        }

        foreach (var (relativePath, checkpoint) in checkpoints)
        {
            if (observedPaths.Contains(relativePath))
            {
                continue;
            }

            var retained = FilterCheckpoint(checkpoint, earliestUtc);
            if (retained.Aggregates.Count == 0)
            {
                continue;
            }

            updatedCheckpoints[relativePath] = retained;
            invalidLineCount += retained.InvalidLineCount;
            skippedFileCount++;
        }

        var aggregates = updatedCheckpoints.Values
            .SelectMany(checkpoint => checkpoint.Aggregates)
            .OrderBy(aggregate => aggregate.Timestamp)
            .ToArray();
        return new LocalUsageCollectionResult(newEvents, invalidLineCount)
        {
            Aggregates = aggregates,
            FileCheckpoints = updatedCheckpoints,
            SkippedFileCount = skippedFileCount,
            ParsedByteCount = parsedByteCount,
            HasCheckpointChanges = !CheckpointMapsEqual(checkpoints, updatedCheckpoints),
        };
    }

    private async Task<FileScanResult?> CollectFileAsync(
        string path,
        string relativePath,
        DateTimeOffset earliestUtc,
        LocalUsageFileCheckpoint? checkpoint,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var creationTimeUtc = AsUtc(File.GetCreationTimeUtc(path));
        var lastWriteTimeUtc = AsUtc(File.GetLastWriteTimeUtc(path));
        var initialLength = stream.Length;
        if (lastWriteTimeUtc < earliestUtc)
        {
            return null;
        }

        var canResume = checkpoint is not null &&
            string.Equals(
                checkpoint.RateCardVersion,
                CodexCreditRateCard.Version,
                StringComparison.Ordinal) &&
            checkpoint.CreationTimeUtc == creationTimeUtc &&
            initialLength >= checkpoint.CompletedLineByteOffset &&
            initialLength >= checkpoint.LastKnownLength &&
            (initialLength != checkpoint.LastKnownLength ||
             lastWriteTimeUtc == checkpoint.LastWriteTimeUtc) &&
            await PrefixMatchesAsync(stream, checkpoint, cancellationToken) &&
            await CompletedTailMatchesAsync(stream, checkpoint, cancellationToken);

        var model = canResume ? checkpoint!.Model : string.Empty;
        var serviceTier = canResume ? checkpoint!.ServiceTier : string.Empty;
        var aggregates = canResume
            ? checkpoint!.Aggregates
                .Where(aggregate => aggregate.Timestamp >= earliestUtc)
                .ToList()
            : [];
        var persistentInvalidLineCount = canResume
            ? checkpoint!.InvalidLineCount
            : 0;
        var startOffset = canResume
            ? checkpoint!.CompletedLineByteOffset
            : 0;
        var scan = await ReadCompletedLinesAsync(
            stream,
            startOffset,
            earliestUtc,
            model,
            serviceTier,
            cancellationToken);
        aggregates.AddRange(scan.NewAggregates);
        persistentInvalidLineCount += scan.CompletedInvalidLineCount;

        var finalLength = stream.Length;
        var prefixLength = (int)Math.Min(finalLength, PrefixProbeLength);
        var prefixSha256 = await HashPrefixAsync(
            stream,
            prefixLength,
            cancellationToken);
        var completedTailLength = (int)Math.Min(
            scan.CompletedLineByteOffset,
            PrefixProbeLength);
        var completedTailSha256 = await HashRangeAsync(
            stream,
            scan.CompletedLineByteOffset - completedTailLength,
            completedTailLength,
            cancellationToken);
        var updated = new LocalUsageFileCheckpoint(
            relativePath,
            scan.CompletedLineByteOffset,
            finalLength,
            creationTimeUtc,
            AsUtc(File.GetLastWriteTimeUtc(path)),
            prefixLength,
            prefixSha256,
            completedTailLength,
            completedTailSha256,
            scan.Model,
            scan.ServiceTier,
            aggregates,
            persistentInvalidLineCount,
            CodexCreditRateCard.Version);
        return new FileScanResult(
            updated,
            scan.NewEvents,
            persistentInvalidLineCount + scan.IncompleteFinalLineCount,
            scan.ParsedByteCount);
    }

    private async Task<LineScanResult> ReadCompletedLinesAsync(
        FileStream stream,
        long startOffset,
        DateTimeOffset earliestUtc,
        string initialModel,
        string initialServiceTier,
        CancellationToken cancellationToken)
    {
        stream.Seek(startOffset, SeekOrigin.Begin);
        var model = initialModel;
        var serviceTier = initialServiceTier;
        var newEvents = new List<LocalUsageEvent>();
        var newAggregates = new List<LocalUsageAggregate>();
        var completedInvalidLineCount = 0;
        var buffer = new byte[BufferSize];
        using var line = new MemoryStream();
        var absoluteOffset = startOffset;
        var completedLineByteOffset = startOffset;
        long parsedByteCount = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = buffer[index];
                absoluteOffset++;
                parsedByteCount++;
                if (value != (byte)'\n')
                {
                    line.WriteByte(value);
                    continue;
                }

                if (!TryProcessLine(
                        line,
                        earliestUtc,
                        ref model,
                        ref serviceTier,
                        out var usage))
                {
                    completedInvalidLineCount++;
                }
                else if (usage is not null)
                {
                    newEvents.Add(usage);
                    var calculation = _rateCard.CalculateCredits(usage);
                    newAggregates.Add(new LocalUsageAggregate(
                        usage.Timestamp,
                        calculation.Credits,
                        calculation.FailureReason));
                }

                line.SetLength(0);
                line.Position = 0;
                completedLineByteOffset = absoluteOffset;
            }
        }

        var incompleteFinalLineCount = 0;
        if (line.Length > 0)
        {
            if (!TryProcessLine(
                    line,
                    earliestUtc,
                    ref model,
                    ref serviceTier,
                    out var usage))
            {
                incompleteFinalLineCount = 1;
            }
            else
            {
                if (usage is not null)
                {
                    newEvents.Add(usage);
                    var calculation = _rateCard.CalculateCredits(usage);
                    newAggregates.Add(new LocalUsageAggregate(
                        usage.Timestamp,
                        calculation.Credits,
                        calculation.FailureReason));
                }

                completedLineByteOffset = absoluteOffset;
            }
        }

        return new LineScanResult(
            completedLineByteOffset,
            model,
            serviceTier,
            newEvents,
            newAggregates,
            completedInvalidLineCount,
            incompleteFinalLineCount,
            parsedByteCount);
    }

    private static bool TryProcessLine(
        MemoryStream line,
        DateTimeOffset earliestUtc,
        ref string model,
        ref string serviceTier,
        out LocalUsageEvent? usage)
    {
        usage = null;
        try
        {
            var bytes = line.ToArray();
            var length = bytes.Length;
            if (length > 0 && bytes[length - 1] == (byte)'\r')
            {
                length--;
            }

            if (length == 0)
            {
                return true;
            }

            var text = StrictUtf8.GetString(bytes, 0, length);
            using var document = JsonDocument.Parse(text);
            usage = ProcessLine(
                document.RootElement,
                earliestUtc,
                ref model,
                ref serviceTier,
                out var isInvalid);
            return !isInvalid;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static LocalUsageEvent? ProcessLine(
        JsonElement root,
        DateTimeOffset earliestUtc,
        ref string model,
        ref string serviceTier,
        out bool isInvalid)
    {
        isInvalid = false;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetString(root, "type", out var type) ||
            !root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (string.Equals(type, "turn_context", StringComparison.Ordinal) &&
            TryGetString(payload, "model", out var nextModel))
        {
            model = nextModel;
            return null;
        }

        if (!string.Equals(type, "event_msg", StringComparison.Ordinal) ||
            !TryGetString(payload, "type", out var eventType))
        {
            return null;
        }

        if (string.Equals(eventType, "thread_settings_applied", StringComparison.Ordinal) &&
            payload.TryGetProperty("thread_settings", out var threadSettings) &&
            threadSettings.ValueKind == JsonValueKind.Object &&
            TryGetString(threadSettings, "service_tier", out var nextServiceTier))
        {
            serviceTier = nextServiceTier;
            return null;
        }

        if (!string.Equals(eventType, "token_count", StringComparison.Ordinal))
        {
            return null;
        }

        if (!TryGetTimestamp(root, out var timestamp))
        {
            isInvalid = true;
            return null;
        }

        if (!payload.TryGetProperty("info", out var info) ||
            info.ValueKind != JsonValueKind.Object ||
            !info.TryGetProperty("last_token_usage", out _))
        {
            return null;
        }

        if (!TryGetLastTokenUsage(
                payload,
                out var inputTokens,
                out var cachedInputTokens,
                out var outputTokens))
        {
            isInvalid = true;
            return null;
        }

        if (timestamp < earliestUtc)
        {
            return null;
        }

        return new LocalUsageEvent(
            timestamp,
            model,
            serviceTier,
            inputTokens,
            cachedInputTokens,
            outputTokens);
    }

    private SessionFileEnumeration EnumerateSessionFiles(
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        var skippedInputCount = 0;
        var pending = new Queue<string>();
        pending.Enqueue(_sessionRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            try
            {
                paths.AddRange(Directory.GetFiles(
                    directory,
                    "*.jsonl",
                    SearchOption.TopDirectoryOnly));
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                skippedInputCount++;
            }

            try
            {
                foreach (var child in Directory.GetDirectories(directory))
                {
                    pending.Enqueue(child);
                }
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                skippedInputCount++;
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return new SessionFileEnumeration(paths, skippedInputCount);
    }

    private string ToRelativePath(string path) =>
        Path.GetRelativePath(_sessionRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static LocalUsageFileCheckpoint FilterCheckpoint(
        LocalUsageFileCheckpoint checkpoint,
        DateTimeOffset earliestUtc) =>
        checkpoint with
        {
            Aggregates = checkpoint.Aggregates
                .Where(aggregate => aggregate.Timestamp >= earliestUtc)
                .ToArray(),
        };

    private static async Task<bool> PrefixMatchesAsync(
        FileStream stream,
        LocalUsageFileCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.PrefixLength < 0 ||
            checkpoint.PrefixLength > stream.Length)
        {
            return false;
        }

        var actual = await HashPrefixAsync(
            stream,
            checkpoint.PrefixLength,
            cancellationToken);
        return string.Equals(
            actual,
            checkpoint.PrefixSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> CompletedTailMatchesAsync(
        FileStream stream,
        LocalUsageFileCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.CompletedTailLength < 0 ||
            checkpoint.CompletedTailLength > checkpoint.CompletedLineByteOffset ||
            checkpoint.CompletedLineByteOffset > stream.Length)
        {
            return false;
        }

        var actual = await HashRangeAsync(
            stream,
            checkpoint.CompletedLineByteOffset - checkpoint.CompletedTailLength,
            checkpoint.CompletedTailLength,
            cancellationToken);
        return string.Equals(
            actual,
            checkpoint.CompletedTailSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> HashPrefixAsync(
        FileStream stream,
        int length,
        CancellationToken cancellationToken) =>
        await HashRangeAsync(
            stream,
            offset: 0,
            length,
            cancellationToken);

    private static async Task<string> HashRangeAsync(
        FileStream stream,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[length];
        var readOffset = 0;
        while (readOffset < length)
        {
            var read = await stream.ReadAsync(
                bytes.AsMemory(readOffset, length - readOffset),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            readOffset += read;
        }

        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, readOffset)));
    }

    private static bool CheckpointMapsEqual(
        IReadOnlyDictionary<string, LocalUsageFileCheckpoint> left,
        IReadOnlyDictionary<string, LocalUsageFileCheckpoint> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, leftCheckpoint) in left)
        {
            if (!right.TryGetValue(key, out var rightCheckpoint) ||
                leftCheckpoint with { Aggregates = Array.Empty<LocalUsageAggregate>() } !=
                rightCheckpoint with { Aggregates = Array.Empty<LocalUsageAggregate>() } ||
                !leftCheckpoint.Aggregates.SequenceEqual(rightCheckpoint.Aggregates))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExpectedFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static bool TryGetLastTokenUsage(
        JsonElement payload,
        out long inputTokens,
        out long cachedInputTokens,
        out long outputTokens)
    {
        inputTokens = 0;
        cachedInputTokens = 0;
        outputTokens = 0;
        return payload.TryGetProperty("info", out var info) &&
               info.ValueKind == JsonValueKind.Object &&
               info.TryGetProperty("last_token_usage", out var usage) &&
               usage.ValueKind == JsonValueKind.Object &&
               TryGetInt64(usage, "input_tokens", out inputTokens) &&
               TryGetInt64(usage, "cached_input_tokens", out cachedInputTokens) &&
               TryGetInt64(usage, "output_tokens", out outputTokens);
    }

    private static bool TryGetTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var value) &&
               value.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   value.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out timestamp);
    }

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt64(
        JsonElement element,
        string propertyName,
        out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    private sealed record SessionFileEnumeration(
        IReadOnlyList<string> Paths,
        int SkippedInputCount);

    private sealed record FileScanResult(
        LocalUsageFileCheckpoint Checkpoint,
        IReadOnlyList<LocalUsageEvent> NewEvents,
        int ReportedInvalidLineCount,
        long ParsedByteCount);

    private sealed record LineScanResult(
        long CompletedLineByteOffset,
        string Model,
        string ServiceTier,
        IReadOnlyList<LocalUsageEvent> NewEvents,
        IReadOnlyList<LocalUsageAggregate> NewAggregates,
        int CompletedInvalidLineCount,
        int IncompleteFinalLineCount,
        long ParsedByteCount);
}
