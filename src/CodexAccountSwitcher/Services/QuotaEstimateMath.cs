using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record QuotaEstimateIntersection(
    PeriodQuotaEstimate Estimate,
    QuotaEstimateQuality Quality,
    int ObservationCount,
    bool IgnoredConflictingHistory);

public static class QuotaEstimateMath
{
    private const decimal UsdPerCredit = 40m / 1000m;

    public static PeriodQuotaEstimate? TryCreateFullInterval(
        decimal lowerCredits,
        decimal upperCredits,
        double usedPercent,
        double percentResolution)
    {
        if (lowerCredits < 0 ||
            upperCredits < lowerCredits ||
            !IsValidPercentage(usedPercent) ||
            !TryGetResolution(percentResolution, out var resolution))
        {
            return null;
        }

        var percent = (decimal)usedPercent;
        var halfResolution = resolution / 2m;
        var percentLow = percent - halfResolution;
        var percentHigh = percent + halfResolution;
        if (percentLow <= 0)
        {
            return null;
        }

        try
        {
            var lowerUsd = RoundUsd(lowerCredits / (percentHigh / 100m) * UsdPerCredit);
            var upperUsd = RoundUsd(upperCredits / (percentLow / 100m) * UsdPerCredit);
            return new PeriodQuotaEstimate(lowerUsd, upperUsd);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static PeriodQuotaEstimate? TryCreateDeltaInterval(
        decimal deltaCredits,
        double earlierPercent,
        double earlierResolution,
        double laterPercent,
        double laterResolution)
    {
        if (deltaCredits <= 0 ||
            !IsValidPercentage(earlierPercent) ||
            !IsValidPercentage(laterPercent) ||
            !TryGetResolution(earlierResolution, out var earlierResolutionValue) ||
            !TryGetResolution(laterResolution, out var laterResolutionValue))
        {
            return null;
        }

        var earlier = (decimal)earlierPercent;
        var later = (decimal)laterPercent;
        var deltaPercentLow =
            later - laterResolutionValue / 2m -
            (earlier + earlierResolutionValue / 2m);
        var deltaPercentHigh =
            later + laterResolutionValue / 2m -
            (earlier - earlierResolutionValue / 2m);
        if (deltaPercentLow <= 0)
        {
            return null;
        }

        try
        {
            var lowerUsd =
                RoundUsd(deltaCredits / (deltaPercentHigh / 100m) * UsdPerCredit);
            var upperUsd =
                RoundUsd(deltaCredits / (deltaPercentLow / 100m) * UsdPerCredit);
            return new PeriodQuotaEstimate(lowerUsd, upperUsd);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static QuotaEstimateIntersection? IntersectRecentCompatible(
        IReadOnlyList<QuotaUsageObservation> observations,
        QuotaSegment segment)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(segment);

        decimal? lowerUsd = null;
        decimal? upperUsd = null;
        var observationCount = 0;
        var ignoredConflictingHistory = false;
        foreach (var observation in observations
            .Where(observation =>
                observation.Segment == segment &&
                observation.LowerUsd is >= 0 &&
                observation.UpperUsd is >= 0 &&
                observation.LowerUsd <= observation.UpperUsd)
            .OrderByDescending(observation => observation.ObservedAt))
        {
            var observationLower = observation.LowerUsd.GetValueOrDefault();
            var observationUpper = observation.UpperUsd.GetValueOrDefault();
            var nextLower = Math.Max(lowerUsd ?? observationLower, observationLower);
            var nextUpper = Math.Min(upperUsd ?? observationUpper, observationUpper);
            if (nextLower > nextUpper)
            {
                ignoredConflictingHistory = true;
                break;
            }

            lowerUsd = nextLower;
            upperUsd = nextUpper;
            observationCount++;
        }

        if (observationCount == 0)
        {
            return null;
        }

        return new QuotaEstimateIntersection(
            new PeriodQuotaEstimate(lowerUsd!.Value, upperUsd!.Value),
            observationCount == 1
                ? QuotaEstimateQuality.Initial
                : QuotaEstimateQuality.MultiPoint,
            observationCount,
            ignoredConflictingHistory);
    }

    private static bool IsValidPercentage(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 100;

    private static bool TryGetResolution(double value, out decimal resolution)
    {
        resolution = default;
        if (!double.IsFinite(value) ||
            value <= 0 ||
            value > (double)decimal.MaxValue)
        {
            return false;
        }

        resolution = (decimal)value;
        return true;
    }

    private static decimal RoundUsd(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
