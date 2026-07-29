using System.IO;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record QuotaEstimateLedgerLoadResult(
    QuotaEstimateLedgerState State,
    string? Error);

public sealed class QuotaEstimateLedgerService
{
    private const int CurrentSchemaVersion = 3;
    private const int CheckpointAggregateSchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    private const string InvalidFileError = "本地额度估算账本无效，原文件已保留。";
    private const string UnsupportedVersionError = "本地额度估算账本版本不受支持，原文件已保留。";
    private const string ReadError = "本地额度估算账本暂时无法读取，原文件已保留。";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private bool _saveBlocked;

    public QuotaEstimateLedgerService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static QuotaEstimateLedgerService CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new QuotaEstimateLedgerService(Path.Combine(
            localAppData,
            "CodexAccountSwitcher",
            "quota-estimate-ledger.json"));
    }

    public async Task<QuotaEstimateLedgerLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            _saveBlocked = false;
            return Empty();
        }

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<LedgerDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            if (document?.Accounts is null)
            {
                return Blocked(InvalidFileError);
            }

            if (document.SchemaVersion is not CurrentSchemaVersion and
                not CheckpointAggregateSchemaVersion and
                not LegacySchemaVersion)
            {
                return Blocked(UnsupportedVersionError);
            }

            if (document.SchemaVersion is CurrentSchemaVersion or
                CheckpointAggregateSchemaVersion &&
                document.FileCheckpoints is null)
            {
                return Blocked(InvalidFileError);
            }

            if (document.SchemaVersion == CheckpointAggregateSchemaVersion &&
                !AreValidLegacyCheckpoints(document.FileCheckpoints!))
            {
                return Blocked(InvalidFileError);
            }

            var accounts = new Dictionary<string, AccountQuotaEstimateLedger>(
                StringComparer.Ordinal);
            foreach (var (accountKey, ledger) in document.Accounts)
            {
                if (ledger is null || !accounts.TryAdd(accountKey, Copy(ledger)))
                {
                    return Blocked(InvalidFileError);
                }
            }

            var checkpoints = document.FileCheckpoints is null
                ? new Dictionary<string, LocalUsageFileCheckpoint>(StringComparer.Ordinal)
                : document.FileCheckpoints.ToDictionary(
                    pair => pair.Key,
                    pair => document.SchemaVersion == CheckpointAggregateSchemaVersion
                        ? CompactLegacyCheckpoint(pair.Value)
                        : pair.Value,
                    StringComparer.Ordinal);
            var state = new QuotaEstimateLedgerState(accounts)
            {
                FileCheckpoints = checkpoints,
            };
            if (!IsValid(state))
            {
                return Blocked(InvalidFileError);
            }

            _saveBlocked = false;
            return new QuotaEstimateLedgerLoadResult(state, null);
        }
        catch (JsonException)
        {
            return Blocked(InvalidFileError);
        }
        catch (IOException)
        {
            return Blocked(ReadError);
        }
        catch (UnauthorizedAccessException)
        {
            return Blocked(ReadError);
        }
    }

    public async Task SaveAsync(
        QuotaEstimateLedgerState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_saveBlocked)
        {
            throw new InvalidOperationException(
                "The existing quota estimate ledger cannot be overwritten.");
        }

        if (state.Accounts is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        if (state.Accounts.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Account keys cannot be empty.", nameof(state));
        }

        if (!IsValid(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Quota estimate ledger values are invalid.");
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new LedgerDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Accounts = state.Accounts.ToDictionary(
                    pair => pair.Key,
                    pair => (AccountQuotaEstimateLedger?)pair.Value,
                    StringComparer.Ordinal),
                FileCheckpoints = state.FileCheckpoints.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal),
            };
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

    public static QuotaEstimateLedgerState ObserveRegistry(
        QuotaEstimateLedgerState state,
        AccountRegistry registry,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(registry);
        if (!IsUtcTimestamp(observedAt))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAt),
                "Observation time must be a valid UTC timestamp.");
        }

        if (!IsValid(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Quota estimate ledger values are invalid.");
        }

        var openActivation = state.Accounts
            .SelectMany(pair => pair.Value.Activations.Select(
                (activation, index) => new ActivationLocation(pair.Key, index, activation)))
            .SingleOrDefault(item => item.Activation.EndedAt is null);
        if (registry.ActiveAccountKey is null)
        {
            return openActivation is null
                ? state
                : CloseOpenActivation(state, openActivation, observedAt);
        }

        if (string.IsNullOrWhiteSpace(registry.ActiveAccountKey) ||
            registry.Accounts.Count(account => string.Equals(
                account.AccountKey,
                registry.ActiveAccountKey,
                StringComparison.Ordinal)) != 1)
        {
            throw new ArgumentException(
                "The registry active account key is invalid.",
                nameof(registry));
        }

        var validRegistryActivatedAt = registry.ActiveAccountActivatedAt is { } registryActivatedAt &&
            IsUtcTimestamp(registryActivatedAt) &&
            registryActivatedAt <= observedAt
                ? registryActivatedAt
                : (DateTimeOffset?)null;
        if (openActivation is not null &&
            string.Equals(
                openActivation.AccountKey,
                registry.ActiveAccountKey,
                StringComparison.Ordinal) &&
            (validRegistryActivatedAt is null ||
             validRegistryActivatedAt <= openActivation.Activation.StartedAt))
        {
            return state;
        }

        var activatedAt = validRegistryActivatedAt ?? observedAt;
        var earliestAllowed = openActivation?.Activation.StartedAt ??
            state.Accounts
                .SelectMany(pair => pair.Value.Activations)
                .Where(activation => activation.EndedAt.HasValue)
                .Select(activation => activation.EndedAt!.Value)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
        if (activatedAt < earliestAllowed ||
            (openActivation is not null && activatedAt == earliestAllowed))
        {
            activatedAt = observedAt;
        }

        if (activatedAt < earliestAllowed ||
            (openActivation is not null && activatedAt == earliestAllowed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAt),
                "Registry observations must advance activation history.");
        }

        var accounts = state.Accounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        if (openActivation is not null)
        {
            var previousLedger = accounts[openActivation.AccountKey];
            var activations = previousLedger.Activations.ToArray();
            activations[openActivation.Index] =
                openActivation.Activation with { EndedAt = activatedAt };
            accounts[openActivation.AccountKey] =
                previousLedger with { Activations = activations };
        }

        accounts.TryGetValue(registry.ActiveAccountKey, out var activeLedger);
        activeLedger ??= new AccountQuotaEstimateLedger([], []);
        accounts[registry.ActiveAccountKey] = activeLedger with
        {
            Activations = activeLedger.Activations
                .Append(new AccountActivationInterval(activatedAt, null))
                .ToArray(),
        };
        return state with { Accounts = accounts };
    }

    private static QuotaEstimateLedgerState CloseOpenActivation(
        QuotaEstimateLedgerState state,
        ActivationLocation openActivation,
        DateTimeOffset endedAt)
    {
        if (endedAt <= openActivation.Activation.StartedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endedAt),
                "Registry observations must advance activation history.");
        }

        var accounts = state.Accounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        var previousLedger = accounts[openActivation.AccountKey];
        var activations = previousLedger.Activations.ToArray();
        activations[openActivation.Index] =
            openActivation.Activation with { EndedAt = endedAt };
        accounts[openActivation.AccountKey] =
            previousLedger with { Activations = activations };
        return state with { Accounts = accounts };
    }

    private static QuotaEstimateLedgerLoadResult Empty() =>
        new(
            new QuotaEstimateLedgerState(
                new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal)),
            null);

    private QuotaEstimateLedgerLoadResult Blocked(string error)
    {
        _saveBlocked = true;
        return new QuotaEstimateLedgerLoadResult(
            new QuotaEstimateLedgerState(
                new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal)),
            error);
    }

    private static AccountQuotaEstimateLedger Copy(AccountQuotaEstimateLedger ledger) =>
        new(
            ledger.Activations?.ToArray()!,
            ledger.Observations?.ToArray()!);

    private static bool IsValid(QuotaEstimateLedgerState state)
    {
        if (state.Accounts is null ||
            state.FileCheckpoints is null ||
            !AreValidCheckpoints(state.FileCheckpoints))
        {
            return false;
        }

        var allActivations = new List<AccountActivationInterval>();
        foreach (var (accountKey, ledger) in state.Accounts)
        {
            if (string.IsNullOrWhiteSpace(accountKey) ||
                ledger?.Activations is null ||
                ledger.Observations is null ||
                !AreValidActivations(ledger.Activations) ||
                !AreValidObservations(ledger.Observations))
            {
                return false;
            }

            allActivations.AddRange(ledger.Activations);
        }

        var orderedActivations = allActivations
            .OrderBy(activation => activation.StartedAt)
            .ToArray();
        for (var index = 1; index < orderedActivations.Length; index++)
        {
            var previous = orderedActivations[index - 1];
            if (previous.EndedAt is null ||
                previous.EndedAt.Value > orderedActivations[index].StartedAt)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreValidActivations(
        IReadOnlyList<AccountActivationInterval> activations)
    {
        DateTimeOffset? previousEnd = null;
        for (var index = 0; index < activations.Count; index++)
        {
            var activation = activations[index];
            if (activation is null ||
                !IsUtcTimestamp(activation.StartedAt) ||
                activation.EndedAt is { } endedAt &&
                (!IsUtcTimestamp(endedAt) || endedAt <= activation.StartedAt) ||
                index > 0 &&
                (previousEnd is null || previousEnd.Value > activation.StartedAt))
            {
                return false;
            }

            previousEnd = activation.EndedAt;
        }

        return true;
    }

    private static bool AreValidObservations(
        IReadOnlyList<QuotaUsageObservation> observations)
    {
        DateTimeOffset? previousObservedAt = null;
        foreach (var observation in observations)
        {
            if (observation?.Segment is not { } segment ||
                segment.Period is not QuotaPeriod.Weekly and not QuotaPeriod.Monthly ||
                !IsUtcTimestamp(segment.SegmentStart) ||
                !IsUtcTimestamp(segment.ResetsAt) ||
                segment.SegmentStart >= segment.ResetsAt ||
                !IsUtcTimestamp(observation.ObservedAt) ||
                previousObservedAt > observation.ObservedAt ||
                !double.IsFinite(observation.UsedPercent) ||
                observation.UsedPercent is < 0 or > 100 ||
                !double.IsFinite(observation.PercentResolution) ||
                 observation.PercentResolution <= 0 ||
                  observation.AttributedCredits < 0 ||
                  observation.AttributedCreditsUpper is { } attributedUpper &&
                  (attributedUpper < 0 ||
                   attributedUpper < observation.AttributedCredits) ||
                  observation.MalformedLineCount < 0 ||
                 observation.SkippedFileCount < 0 ||
                 observation.ActivationStartedAt is { } activationStartedAt &&
                 !IsUtcTimestamp(activationStartedAt) ||
                 !HasValidBounds(observation.LowerUsd, observation.UpperUsd) ||
                !Enum.IsDefined(observation.Source) ||
                !Enum.IsDefined(observation.Kind))
            {
                return false;
            }

            previousObservedAt = observation.ObservedAt;
        }

        return true;
    }

    private static bool AreValidCheckpoints(
        IReadOnlyDictionary<string, LocalUsageFileCheckpoint> checkpoints)
    {
        foreach (var (relativePath, checkpoint) in checkpoints)
        {
            if (checkpoint is null ||
                !string.Equals(
                    relativePath,
                    checkpoint.RelativePath,
                    StringComparison.Ordinal) ||
                !IsSafeRelativePath(relativePath) ||
                checkpoint.CompletedLineByteOffset < 0 ||
                checkpoint.LastKnownLength < checkpoint.CompletedLineByteOffset ||
                !IsUtcTimestamp(checkpoint.CreationTimeUtc) ||
                !IsUtcTimestamp(checkpoint.LastWriteTimeUtc) ||
                checkpoint.PrefixLength < 0 ||
                checkpoint.PrefixLength > checkpoint.LastKnownLength ||
                checkpoint.PrefixSha256 is null ||
                checkpoint.PrefixSha256.Length != 64 ||
                checkpoint.PrefixSha256.Any(character => !Uri.IsHexDigit(character)) ||
                checkpoint.CompletedTailLength < 0 ||
                checkpoint.CompletedTailLength > checkpoint.CompletedLineByteOffset ||
                checkpoint.CompletedTailSha256 is null ||
                checkpoint.CompletedTailSha256.Length != 64 ||
                checkpoint.CompletedTailSha256.Any(character => !Uri.IsHexDigit(character)) ||
                checkpoint.Model is null ||
                checkpoint.ServiceTier is null ||
                checkpoint.Aggregates is null ||
                checkpoint.Aggregates.Count != 0 ||
                checkpoint.Buckets is null ||
                checkpoint.InvalidLineCount < 0 ||
                string.IsNullOrWhiteSpace(checkpoint.RateCardVersion) ||
                !IsUtcTimestamp(checkpoint.RelevantThroughUtc) ||
                checkpoint.RelevantThroughUtc < checkpoint.LastWriteTimeUtc ||
                checkpoint.IsTombstone && checkpoint.HasCompleteScan ||
                checkpoint.InvalidLineCount > 0 && checkpoint.HasCompleteScan ||
                checkpoint.Buckets.Any(bucket =>
                    !IsValidBucket(bucket) ||
                    bucket.LastEventAtUtc > checkpoint.RelevantThroughUtc))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreValidLegacyCheckpoints(
        IReadOnlyDictionary<string, LocalUsageFileCheckpoint> checkpoints)
    {
        foreach (var (relativePath, checkpoint) in checkpoints)
        {
            if (checkpoint is null ||
                !string.Equals(
                    relativePath,
                    checkpoint.RelativePath,
                    StringComparison.Ordinal) ||
                !IsSafeRelativePath(relativePath) ||
                checkpoint.CompletedLineByteOffset < 0 ||
                checkpoint.LastKnownLength < checkpoint.CompletedLineByteOffset ||
                !IsUtcTimestamp(checkpoint.CreationTimeUtc) ||
                !IsUtcTimestamp(checkpoint.LastWriteTimeUtc) ||
                checkpoint.PrefixLength < 0 ||
                checkpoint.PrefixLength > checkpoint.LastKnownLength ||
                checkpoint.PrefixSha256 is null ||
                checkpoint.PrefixSha256.Length != 64 ||
                checkpoint.PrefixSha256.Any(character => !Uri.IsHexDigit(character)) ||
                checkpoint.CompletedTailLength < 0 ||
                checkpoint.CompletedTailLength > checkpoint.CompletedLineByteOffset ||
                checkpoint.CompletedTailSha256 is null ||
                checkpoint.CompletedTailSha256.Length != 64 ||
                checkpoint.CompletedTailSha256.Any(character => !Uri.IsHexDigit(character)) ||
                checkpoint.Model is null ||
                checkpoint.ServiceTier is null ||
                checkpoint.Aggregates is null ||
                checkpoint.InvalidLineCount < 0 ||
                string.IsNullOrWhiteSpace(checkpoint.RateCardVersion) ||
                checkpoint.Aggregates.Any(aggregate =>
                    aggregate is null ||
                    !IsUtcTimestamp(aggregate.Timestamp) ||
                    aggregate.Credits < 0 ||
                    !Enum.IsDefined(aggregate.FailureReason) ||
                    aggregate.FailureReason != CreditPricingFailureReason.None &&
                    aggregate.Credits != 0))
            {
                return false;
            }
        }

        return true;
    }

    private static LocalUsageFileCheckpoint CompactLegacyCheckpoint(
        LocalUsageFileCheckpoint checkpoint)
    {
        var buckets = new Dictionary<DateTimeOffset, LocalUsageBucket>();
        foreach (var aggregate in checkpoint.Aggregates)
        {
            var utc = aggregate.Timestamp.ToUniversalTime();
            var bucketStart = new DateTimeOffset(
                utc.Year,
                utc.Month,
                utc.Day,
                utc.Hour,
                minute: 0,
                second: 0,
                TimeSpan.Zero);
            var next = new LocalUsageBucket(
                bucketStart,
                utc,
                utc,
                aggregate.FailureReason == CreditPricingFailureReason.None
                    ? aggregate.Credits
                    : 0m,
                aggregate.FailureReason == CreditPricingFailureReason.None ? 1 : 0,
                aggregate.FailureReason == CreditPricingFailureReason.UnknownModel ? 1 : 0,
                aggregate.FailureReason == CreditPricingFailureReason.UnknownServiceTier ? 1 : 0,
                aggregate.FailureReason == CreditPricingFailureReason.InvalidUsage ? 1 : 0);
            buckets[bucketStart] = buckets.TryGetValue(bucketStart, out var existing)
                ? MergeBucket(existing, next)
                : next;
        }

        var compacted = buckets.Values
            .OrderBy(bucket => bucket.BucketStartUtc)
            .ToArray();
        var relevantThroughUtc = compacted
            .Select(bucket => bucket.LastEventAtUtc)
            .Append(checkpoint.LastWriteTimeUtc)
            .Max();
        return checkpoint with
        {
            Aggregates = Array.Empty<LocalUsageAggregate>(),
            Buckets = compacted,
            HasCompleteScan = checkpoint.InvalidLineCount == 0,
            IsTombstone = false,
            RelevantThroughUtc = relevantThroughUtc,
        };
    }

    private static LocalUsageBucket MergeBucket(
        LocalUsageBucket left,
        LocalUsageBucket right) =>
        left with
        {
            FirstEventAtUtc = left.FirstEventAtUtc <= right.FirstEventAtUtc
                ? left.FirstEventAtUtc
                : right.FirstEventAtUtc,
            LastEventAtUtc = left.LastEventAtUtc >= right.LastEventAtUtc
                ? left.LastEventAtUtc
                : right.LastEventAtUtc,
            PricedCredits = left.PricedCredits + right.PricedCredits,
            PricedEventCount = left.PricedEventCount + right.PricedEventCount,
            UnknownModelEventCount =
                left.UnknownModelEventCount + right.UnknownModelEventCount,
            UnknownServiceTierEventCount =
                left.UnknownServiceTierEventCount + right.UnknownServiceTierEventCount,
            InvalidUsageEventCount =
                left.InvalidUsageEventCount + right.InvalidUsageEventCount,
        };

    private static bool IsValidBucket(LocalUsageBucket? bucket)
    {
        if (bucket is null ||
            !IsUtcTimestamp(bucket.BucketStartUtc) ||
            bucket.BucketStartUtc.Minute != 0 ||
            bucket.BucketStartUtc.Second != 0 ||
            bucket.BucketStartUtc.Millisecond != 0 ||
            !IsUtcTimestamp(bucket.FirstEventAtUtc) ||
            !IsUtcTimestamp(bucket.LastEventAtUtc) ||
            bucket.FirstEventAtUtc < bucket.BucketStartUtc ||
            bucket.LastEventAtUtc >= bucket.BucketStartUtc.AddHours(1) ||
            bucket.FirstEventAtUtc > bucket.LastEventAtUtc ||
            bucket.PricedCredits < 0 ||
            bucket.PricedEventCount < 0 ||
            bucket.UnknownModelEventCount < 0 ||
            bucket.UnknownServiceTierEventCount < 0 ||
            bucket.InvalidUsageEventCount < 0 ||
            bucket.InputTokens < 0 ||
            bucket.CachedInputTokens < 0 ||
            bucket.CachedInputTokens > bucket.InputTokens ||
            bucket.OutputTokens < 0)
        {
            return false;
        }

        return bucket.PricedEventCount +
            bucket.UnknownModelEventCount +
            bucket.UnknownServiceTierEventCount +
            bucket.InvalidUsageEventCount > 0;
    }

    private static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relativePath.Split('/');
        return segments.All(segment =>
            !string.IsNullOrWhiteSpace(segment) &&
            segment is not "." and not "..");
    }

    private static bool HasValidBounds(decimal? lower, decimal? upper) =>
        lower.HasValue == upper.HasValue &&
        lower is null or >= 0 &&
        upper is null or >= 0 &&
        (lower is null || lower <= upper);

    private static bool IsUtcTimestamp(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    private sealed class LedgerDocument
    {
        public int SchemaVersion { get; set; }

        public Dictionary<string, AccountQuotaEstimateLedger?>? Accounts { get; set; }

        public Dictionary<string, LocalUsageFileCheckpoint>? FileCheckpoints { get; set; }
    }

    private sealed record ActivationLocation(
        string AccountKey,
        int Index,
        AccountActivationInterval Activation);
}
