namespace CodexAccountSwitcher.Models;

public sealed record DailyTokenUsage(
    DateOnly Date,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;

    public decimal InputMillions => InputTokens / 1_000_000m;

    public decimal CachedInputMillions => CachedInputTokens / 1_000_000m;

    public decimal OutputMillions => OutputTokens / 1_000_000m;

    public decimal TotalMillions => TotalTokens / 1_000_000m;

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

    public decimal InputMillions => InputTokens / 1_000_000m;

    public decimal CachedInputMillions => CachedInputTokens / 1_000_000m;

    public decimal OutputMillions => OutputTokens / 1_000_000m;

    public decimal TotalMillions => TotalTokens / 1_000_000m;

    public string RangeText => $"{StartDate:M.d}–{EndDate:M.d}";
}

public sealed record TokenUsageSnapshot(
    IReadOnlyList<LocalUsageBucket> Buckets,
    DateTimeOffset RefreshedAt,
    bool IsComplete);
