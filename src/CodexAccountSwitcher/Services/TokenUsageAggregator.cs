using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public static class TokenUsageAggregator
{
    public static TokenUsageSummary Aggregate(
        IEnumerable<LocalUsageBucket> buckets,
        DateOnly startDate,
        DateOnly endDate,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (startDate > endDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startDate),
                "开始日期不能晚于结束日期。");
        }

        var days = buckets
            .Select(bucket => new
            {
                Date = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTime(bucket.BucketStartUtc, timeZone).Date),
                Bucket = bucket,
            })
            .Where(item => item.Date >= startDate && item.Date <= endDate)
            .GroupBy(item => item.Date)
            .OrderBy(group => group.Key)
            .Select(group => new DailyTokenUsage(
                group.Key,
                group.Sum(item => item.Bucket.InputTokens),
                group.Sum(item => item.Bucket.CachedInputTokens),
                group.Sum(item => item.Bucket.OutputTokens)))
            .ToArray();

        return new TokenUsageSummary(
            startDate,
            endDate,
            days.Sum(day => day.InputTokens),
            days.Sum(day => day.CachedInputTokens),
            days.Sum(day => day.OutputTokens),
            days);
    }
}
