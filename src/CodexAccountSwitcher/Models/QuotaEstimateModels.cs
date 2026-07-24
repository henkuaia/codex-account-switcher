namespace CodexAccountSwitcher.Models;

public enum QuotaEstimateSource { None, Analytics, Local }

public enum QuotaEstimateQuality { None, Initial, MultiPoint }

public enum QuotaObservationKind { FullSegment, Delta }

public sealed record LocalUsageEvent(
    DateTimeOffset Timestamp,
    string Model,
    string ServiceTier,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens);

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
    QuotaObservationKind Kind);

public sealed record AccountQuotaEstimateLedger(
    IReadOnlyList<AccountActivationInterval> Activations,
    IReadOnlyList<QuotaUsageObservation> Observations);

public sealed record QuotaEstimateLedgerState(
    IReadOnlyDictionary<string, AccountQuotaEstimateLedger> Accounts)
{
    public static QuotaEstimateLedgerState Empty { get; } = new(
        new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal));
}
