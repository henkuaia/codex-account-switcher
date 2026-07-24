using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class QuotaEstimateMathTests
{
    private static readonly QuotaSegment CurrentSegment = new(
        QuotaPeriod.Weekly,
        DateTimeOffset.Parse("2026-07-20T00:00:00Z"),
        DateTimeOffset.Parse("2026-07-27T00:00:00Z"));

    [Fact]
    public void Full_interval_propagates_percentage_rounding_uncertainty()
    {
        var result = QuotaEstimateMath.TryCreateFullInterval(
            lowerCredits: 100m,
            upperCredits: 100m,
            usedPercent: 25,
            percentResolution: 1);

        Assert.NotNull(result);
        Assert.Equal(15.69m, result.LowerUsd);
        Assert.Equal(16.33m, result.UpperUsd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    public void Full_interval_requires_a_positive_lower_percentage_bound(
        double usedPercent)
    {
        var result = QuotaEstimateMath.TryCreateFullInterval(
            lowerCredits: 100m,
            upperCredits: 100m,
            usedPercent,
            percentResolution: 1);

        Assert.Null(result);
    }

    [Fact]
    public void Full_interval_accepts_different_credit_bounds()
    {
        var result = QuotaEstimateMath.TryCreateFullInterval(
            lowerCredits: 100m,
            upperCredits: 150m,
            usedPercent: 25,
            percentResolution: 1);

        Assert.NotNull(result);
        Assert.Equal(15.69m, result.LowerUsd);
        Assert.Equal(24.49m, result.UpperUsd);
    }

    [Fact]
    public void Delta_interval_sums_both_endpoint_uncertainties()
    {
        var result = QuotaEstimateMath.TryCreateDeltaInterval(
            deltaCredits: 50m,
            earlierPercent: 20,
            earlierResolution: 2,
            laterPercent: 30,
            laterResolution: 4);

        Assert.NotNull(result);
        Assert.Equal(15.38m, result.LowerUsd);
        Assert.Equal(28.57m, result.UpperUsd);
    }

    [Theory]
    [InlineData(0, 20, 1, 30, 1)]
    [InlineData(-1, 20, 1, 30, 1)]
    [InlineData(10, 25, 1, 26, 1)]
    public void Delta_interval_requires_positive_credits_and_lower_percentage_bound(
        double deltaCredits,
        double earlierPercent,
        double earlierResolution,
        double laterPercent,
        double laterResolution)
    {
        var result = QuotaEstimateMath.TryCreateDeltaInterval(
            (decimal)deltaCredits,
            earlierPercent,
            earlierResolution,
            laterPercent,
            laterResolution);

        Assert.Null(result);
    }

    [Fact]
    public void Interval_endpoints_round_to_two_decimals_away_from_zero()
    {
        var result = QuotaEstimateMath.TryCreateFullInterval(
            lowerCredits: 12.81375m,
            upperCredits: 12.81375m,
            usedPercent: 50,
            percentResolution: 2);

        Assert.NotNull(result);
        Assert.Equal(1.01m, result.LowerUsd);
    }

    [Fact]
    public void Intersection_uses_maximum_lower_and_minimum_upper()
    {
        var observations = new[]
        {
            CreateObservation(CurrentSegment, "2026-07-24T01:00:00Z", 12m, 25m),
            CreateObservation(CurrentSegment, "2026-07-24T02:00:00Z", 10m, 20m),
        };

        var result = QuotaEstimateMath.IntersectRecentCompatible(
            observations,
            CurrentSegment);

        Assert.NotNull(result);
        Assert.Equal(new PeriodQuotaEstimate(12m, 20m), result.Estimate);
        Assert.Equal(QuotaEstimateQuality.MultiPoint, result.Quality);
        Assert.Equal(2, result.ObservationCount);
        Assert.False(result.IgnoredConflictingHistory);
    }

    [Fact]
    public void Intersection_stops_at_first_conflicting_older_interval()
    {
        var observations = new[]
        {
            CreateObservation(CurrentSegment, "2026-07-24T01:00:00Z", 19m, 25m),
            CreateObservation(CurrentSegment, "2026-07-24T02:00:00Z", 12m, 18m),
            CreateObservation(CurrentSegment, "2026-07-24T03:00:00Z", 10m, 20m),
        };

        var result = QuotaEstimateMath.IntersectRecentCompatible(
            observations,
            CurrentSegment);

        Assert.NotNull(result);
        Assert.Equal(new PeriodQuotaEstimate(12m, 18m), result.Estimate);
        Assert.Equal(QuotaEstimateQuality.MultiPoint, result.Quality);
        Assert.Equal(2, result.ObservationCount);
        Assert.True(result.IgnoredConflictingHistory);
    }

    [Fact]
    public void One_compatible_interval_has_initial_quality()
    {
        var observations = new[]
        {
            CreateObservation(CurrentSegment, "2026-07-24T01:00:00Z", 10m, 20m),
        };

        var result = QuotaEstimateMath.IntersectRecentCompatible(
            observations,
            CurrentSegment);

        Assert.NotNull(result);
        Assert.Equal(QuotaEstimateQuality.Initial, result.Quality);
        Assert.Equal(1, result.ObservationCount);
    }

    [Fact]
    public void Exact_segment_filter_and_per_account_input_keep_history_isolated()
    {
        var oldSegment = CurrentSegment with
        {
            SegmentStart = CurrentSegment.SegmentStart.AddDays(-7),
            ResetsAt = CurrentSegment.ResetsAt.AddDays(-7),
        };
        var accountAObservations = new[]
        {
            CreateObservation(oldSegment, "2026-07-19T01:00:00Z", 30m, 40m),
            CreateObservation(CurrentSegment, "2026-07-24T01:00:00Z", 10m, 20m),
        };
        var accountBObservations = new[]
        {
            CreateObservation(CurrentSegment, "2026-07-24T02:00:00Z", 30m, 40m),
        };

        var accountAResult = QuotaEstimateMath.IntersectRecentCompatible(
            accountAObservations,
            CurrentSegment);
        var accountBResult = QuotaEstimateMath.IntersectRecentCompatible(
            accountBObservations,
            CurrentSegment);

        Assert.NotNull(accountAResult);
        Assert.Equal(new PeriodQuotaEstimate(10m, 20m), accountAResult.Estimate);
        Assert.Equal(1, accountAResult.ObservationCount);
        Assert.NotNull(accountBResult);
        Assert.Equal(new PeriodQuotaEstimate(30m, 40m), accountBResult.Estimate);
    }

    private static QuotaUsageObservation CreateObservation(
        QuotaSegment segment,
        string observedAt,
        decimal lowerUsd,
        decimal upperUsd) =>
        new(
            segment,
            DateTimeOffset.Parse(observedAt),
            UsedPercent: 25,
            PercentResolution: 1,
            AttributedCredits: 100m,
            HasFullSegmentCoverage: true,
            lowerUsd,
            upperUsd,
            QuotaEstimateSource.Local,
            QuotaObservationKind.FullSegment);
}
