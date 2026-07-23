namespace CodexAccountSwitcher.Models;

public enum QuotaPeriod
{
    Weekly,
    Monthly,
    Unknown,
}

public sealed record QuotaDisplay(
    QuotaPeriod Period,
    int RemainingPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan WindowDuration,
    string Tooltip)
{
    public int? AvailableResetCount { get; init; }

    public decimal? IndividualLimitUsd { get; init; }

    public double UsedPercent { get; init; }

    public DateTimeOffset? ServerNow { get; init; }

    public decimal? EstimatedPeriodQuotaLowerUsd { get; init; }

    public decimal? EstimatedPeriodQuotaUpperUsd { get; init; }
}

public sealed record WeeklyQuotaEstimate(decimal LowerUsd, decimal UpperUsd);

public sealed record QuotaParseResult(QuotaDisplay? Display, string? Error)
{
    public static QuotaParseResult Success(QuotaDisplay? display) => new(display, null);

    public static QuotaParseResult Failure(string error) => new(null, error);
}

public sealed record QuotaUpdate(string AccountKey, QuotaDisplay? Display, string? Error);
