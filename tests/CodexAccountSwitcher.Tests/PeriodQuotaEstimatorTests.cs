using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class PeriodQuotaEstimatorTests
{
    private const string AnalyticsJson = """
        {
          "data": [
            {"date":"2026-07-23","totals":{"credits":50}},
            {"date":"2026-07-24","totals":{"credits":100}}
          ]
        }
        """;

    [Fact]
    public void Excludes_start_day_from_lower_bound_when_segment_starts_midday()
    {
        var estimate = PeriodQuotaEstimator.TryEstimate(
            AnalyticsJson,
            usedPercent: 25,
            segmentStartDate: new DateOnly(2026, 7, 23),
            includeStartDayInLower: false);

        Assert.NotNull(estimate);
        Assert.Equal(16m, estimate.LowerUsd);
        Assert.Equal(24m, estimate.UpperUsd);
    }

    [Fact]
    public void Includes_start_day_in_both_bounds_when_segment_starts_at_midnight()
    {
        var estimate = PeriodQuotaEstimator.TryEstimate(
            AnalyticsJson,
            usedPercent: 25,
            segmentStartDate: new DateOnly(2026, 7, 23),
            includeStartDayInLower: true);

        Assert.NotNull(estimate);
        Assert.Equal(24m, estimate.LowerUsd);
        Assert.Equal(24m, estimate.UpperUsd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    public void Invalid_usage_returns_no_estimate(double usedPercent)
    {
        var estimate = PeriodQuotaEstimator.TryEstimate(
            AnalyticsJson,
            usedPercent,
            new DateOnly(2026, 7, 23),
            includeStartDayInLower: false);

        Assert.Null(estimate);
    }

    [Fact]
    public void Unsupported_response_shape_returns_no_estimate()
    {
        var estimate = PeriodQuotaEstimator.TryEstimate(
            "[]",
            usedPercent: 25,
            segmentStartDate: new DateOnly(2026, 7, 23),
            includeStartDayInLower: false);

        Assert.Null(estimate);
    }
}
