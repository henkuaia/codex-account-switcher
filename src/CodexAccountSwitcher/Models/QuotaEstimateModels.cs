using System.Text.Json.Serialization;

namespace CodexAccountSwitcher.Models;

public enum QuotaEstimateSource { None, Analytics, Local }

public enum QuotaEstimateQuality { None, Initial, MultiPoint }

public enum QuotaObservationKind { FullSegment, Delta }

public enum CreditPricingFailureReason
{
    None,
    UnknownModel,
    UnknownServiceTier,
    InvalidUsage,
}

public sealed record LocalUsageEvent(
    DateTimeOffset Timestamp,
    string Model,
    string ServiceTier,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens);

public sealed record LocalUsageAggregate(
    DateTimeOffset Timestamp,
    decimal Credits,
    CreditPricingFailureReason FailureReason);

public sealed record LocalUsageBucket(
    DateTimeOffset BucketStartUtc,
    DateTimeOffset FirstEventAtUtc,
    DateTimeOffset LastEventAtUtc,
    decimal PricedCredits,
    int PricedEventCount,
    int UnknownModelEventCount,
    int UnknownServiceTierEventCount,
    int InvalidUsageEventCount)
{
    [JsonIgnore]
    public long InputTokens { get; init; }

    [JsonIgnore]
    public long CachedInputTokens { get; init; }

    [JsonIgnore]
    public long OutputTokens { get; init; }

    [JsonIgnore]
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed record LocalUsageFileCheckpoint(
    string RelativePath,
    long CompletedLineByteOffset,
    long LastKnownLength,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    int PrefixLength,
    string PrefixSha256,
    int CompletedTailLength,
    string CompletedTailSha256,
    string Model,
    string ServiceTier,
    IReadOnlyList<LocalUsageAggregate> Aggregates,
    int InvalidLineCount,
    string RateCardVersion)
{
    public IReadOnlyList<LocalUsageBucket> Buckets { get; init; } =
        Array.Empty<LocalUsageBucket>();

    public bool HasCompleteScan { get; init; } = true;

    public bool IsTombstone { get; init; }

    public DateTimeOffset RelevantThroughUtc { get; init; } = LastWriteTimeUtc;
}

public sealed record AccountActivationInterval(
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public sealed record QuotaSegment(
    QuotaPeriod Period,
    DateTimeOffset SegmentStart,
    DateTimeOffset ResetsAt);

public sealed record QuotaUsageObservation(
    QuotaSegment Segment,
    DateTimeOffset ObservedAt,
    double UsedPercent,
    double PercentResolution,
    decimal AttributedCredits,
    bool HasFullSegmentCoverage,
    decimal? LowerUsd,
    decimal? UpperUsd,
    QuotaEstimateSource Source,
    QuotaObservationKind Kind)
{
    public bool IsLocalScanComplete { get; init; } = true;

    public decimal? AttributedCreditsUpper { get; init; }

    public bool HasAttributionBoundaryUncertainty { get; init; }

    public int MalformedLineCount { get; init; }

    public int SkippedFileCount { get; init; }

    public string? RateCardVersion { get; init; }

    public DateTimeOffset? ActivationStartedAt { get; init; }
}

public sealed record AccountQuotaEstimateLedger(
    IReadOnlyList<AccountActivationInterval> Activations,
    IReadOnlyList<QuotaUsageObservation> Observations);

public sealed record QuotaEstimateLedgerState(
    IReadOnlyDictionary<string, AccountQuotaEstimateLedger> Accounts)
{
    public IReadOnlyDictionary<string, LocalUsageFileCheckpoint> FileCheckpoints { get; init; } =
        new Dictionary<string, LocalUsageFileCheckpoint>(StringComparer.Ordinal);

    public static QuotaEstimateLedgerState Empty { get; } = new(
        new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal));
}
