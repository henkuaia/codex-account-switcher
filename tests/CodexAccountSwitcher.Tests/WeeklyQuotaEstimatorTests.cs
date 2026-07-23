using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class WeeklyQuotaEstimatorTests
{
    [Fact]
    public void Estimates_range_with_and_without_the_reset_day()
    {
        var estimate = WeeklyQuotaEstimator.TryEstimate(
            """
            {
              "data": [
                {"date":"2026-07-20","totals":{"credits":100}},
                {"date":"2026-07-21","totals":{"credits":50}}
              ]
            }
            """,
            usedPercent: 25,
            resetDate: new DateOnly(2026, 7, 20));

        Assert.NotNull(estimate);
        Assert.Equal(8m, estimate.LowerUsd);
        Assert.Equal(24m, estimate.UpperUsd);
    }

    [Fact]
    public void Zero_usage_cannot_be_estimated()
    {
        var estimate = WeeklyQuotaEstimator.TryEstimate(
            """{"data":[{"date":"2026-07-20","totals":{"credits":100}}]}""",
            usedPercent: 0,
            resetDate: new DateOnly(2026, 7, 20));

        Assert.Null(estimate);
    }

    [Fact]
    public void Unsupported_response_shape_returns_no_estimate()
    {
        var estimate = WeeklyQuotaEstimator.TryEstimate(
            "[]",
            usedPercent: 25,
            resetDate: new DateOnly(2026, 7, 20));

        Assert.Null(estimate);
    }
}
