using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class TokenUsageAggregatorTests
{
    private static readonly TimeZoneInfo ChinaTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "China test",
        TimeSpan.FromHours(8),
        "China test",
        "China test");

    [Fact]
    public void Aggregates_selected_local_dates_without_double_counting_cached_input()
    {
        var buckets = new[]
        {
            Bucket("2026-07-25T15:00:00Z", input: 100, cached: 40, output: 20),
            Bucket("2026-07-25T16:00:00Z", input: 200, cached: 50, output: 30),
            Bucket("2026-07-28T15:00:00Z", input: 300, cached: 60, output: 40),
            Bucket("2026-07-28T16:00:00Z", input: 400, cached: 70, output: 50),
        };

        var result = TokenUsageAggregator.Aggregate(
            buckets,
            new DateOnly(2026, 7, 26),
            new DateOnly(2026, 7, 29),
            ChinaTimeZone);

        Assert.Equal(900, result.InputTokens);
        Assert.Equal(180, result.CachedInputTokens);
        Assert.Equal(120, result.OutputTokens);
        Assert.Equal(1_020, result.TotalTokens);
        Assert.Equal(0.0009m, result.InputMillions);
        Assert.Equal(0.00018m, result.CachedInputMillions);
        Assert.Equal(0.00012m, result.OutputMillions);
        Assert.Equal(0.00102m, result.TotalMillions);
        Assert.Collection(
            result.Days,
            day =>
            {
                Assert.Equal(new DateOnly(2026, 7, 26), day.Date);
                Assert.Equal(200, day.InputTokens);
                Assert.Equal(230, day.TotalTokens);
                Assert.Equal(0.0002m, day.InputMillions);
                Assert.Equal(0.00023m, day.TotalMillions);
            },
            day =>
            {
                Assert.Equal(new DateOnly(2026, 7, 28), day.Date);
                Assert.Equal(300, day.InputTokens);
                Assert.Equal(340, day.TotalTokens);
            },
            day =>
            {
                Assert.Equal(new DateOnly(2026, 7, 29), day.Date);
                Assert.Equal(400, day.InputTokens);
                Assert.Equal(450, day.TotalTokens);
            });
    }

    private static LocalUsageBucket Bucket(
        string bucketStart,
        long input,
        long cached,
        long output)
    {
        var start = DateTimeOffset.Parse(bucketStart);
        return new LocalUsageBucket(
            start,
            start,
            start,
            PricedCredits: 0,
            PricedEventCount: 1,
            UnknownModelEventCount: 0,
            UnknownServiceTierEventCount: 0,
            InvalidUsageEventCount: 0)
        {
            InputTokens = input,
            CachedInputTokens = cached,
            OutputTokens = output,
        };
    }
}
