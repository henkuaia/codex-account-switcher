namespace CodexAccountSwitcher.Models;

public sealed record DailyTokenUsage(
    DateOnly Date,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;

    public string DateText => Date.ToString("M.d");
}

public sealed record TokenUsageSummary(
    DateOnly StartDate,
    DateOnly EndDate,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    IReadOnlyList<DailyTokenUsage> Days)
{
    public long TotalTokens => InputTokens + OutputTokens;

    public string RangeText => $"{StartDate:M.d}–{EndDate:M.d}";
}

public sealed record TokenUsageSnapshot(
    IReadOnlyList<LocalUsageBucket> Buckets,
    DateTimeOffset RefreshedAt,
    bool IsComplete);
