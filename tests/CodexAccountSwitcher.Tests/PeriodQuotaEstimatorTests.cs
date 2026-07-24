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
        Assert.Equal(15.69m, estimate.LowerUsd);
        Assert.Equal(24.49m, estimate.UpperUsd);
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
        Assert.Equal(23.53m, estimate.LowerUsd);
        Assert.Equal(24.49m, estimate.UpperUsd);
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

    [Fact]
    public void Parse_distinguishes_empty_invalid_and_valid_payloads()
    {
        Assert.Equal(
            AnalyticsUsageState.Empty,
            PeriodQuotaEstimator.Parse("""{"data":[]}""").State);
        Assert.Equal(
            AnalyticsUsageState.Invalid,
            PeriodQuotaEstimator.Parse("""{"data":{}}""").State);
        Assert.Equal(
            AnalyticsUsageState.Valid,
            PeriodQuotaEstimator.Parse(AnalyticsJson).State);
    }

    [Theory]
    [InlineData("""{"data":[{}]}""")]
    [InlineData("""{"data":[{"date":"2026-07-23","totals":{"credits":50}},{}]}""")]
    [InlineData("""{"data":[{"date":"not-a-date","totals":{"credits":50}}]}""")]
    [InlineData("""{"data":[{"date":"2026-07-23","totals":{"credits":-1}}]}""")]
    [InlineData("""{"data":[{"date":"2026-07-23","totals":{}}]}""")]
    public void Parse_rejects_any_malformed_or_incomplete_nonempty_row(string json)
    {
        var result = PeriodQuotaEstimator.Parse(
            json,
            segmentStartDate: new DateOnly(2026, 7, 23),
            includeStartDayInLower: true);

        Assert.Equal(AnalyticsUsageState.Invalid, result.State);
        Assert.Equal(0m, result.LowerCredits);
        Assert.Equal(0m, result.UpperCredits);
    }

    [Fact]
    public void Parse_excludes_non_midnight_start_day_only_from_lower_credits()
    {
        var result = PeriodQuotaEstimator.Parse(
            AnalyticsJson,
            segmentStartDate: new DateOnly(2026, 7, 23),
            includeStartDayInLower: false);

        Assert.Equal(AnalyticsUsageState.Valid, result.State);
        Assert.Equal(100m, result.LowerCredits);
        Assert.Equal(150m, result.UpperCredits);
    }

    [Fact]
    public void Parse_includes_midnight_start_day_in_both_credit_bounds()
    {
        var result = PeriodQuotaEstimator.Parse(
            AnalyticsJson,
            segmentStartDate: new DateOnly(2026, 7, 23),
            includeStartDayInLower: true);

        Assert.Equal(AnalyticsUsageState.Valid, result.State);
        Assert.Equal(150m, result.LowerCredits);
        Assert.Equal(150m, result.UpperCredits);
    }
}
