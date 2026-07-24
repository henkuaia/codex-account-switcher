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
        double percentResolution) =>
        RoundEstimate(TryCreateFullIntervalPrecise(
            lowerCredits,
            upperCredits,
            usedPercent,
            percentResolution));

    internal static PeriodQuotaEstimate? TryCreateFullIntervalPrecise(
        decimal lowerCredits,
        decimal upperCredits,
        double usedPercent,
        double percentResolution)
    {
        if (lowerCredits < 0 ||
            upperCredits < lowerCredits ||
            !TryGetPercentageBounds(
                usedPercent,
                percentResolution,
                out var percentLow,
                out var percentHigh))
        {
            return null;
        }

        try
        {
            if (percentLow <= 0)
            {
                return null;
            }

            var lowerUsd = lowerCredits / (percentHigh / 100m) * UsdPerCredit;
            var upperUsd = upperCredits / (percentLow / 100m) * UsdPerCredit;
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
        double laterResolution) =>
        RoundEstimate(TryCreateDeltaIntervalPrecise(
            deltaCredits,
            earlierPercent,
            earlierResolution,
            laterPercent,
            laterResolution));

    internal static PeriodQuotaEstimate? TryCreateDeltaIntervalPrecise(
        decimal deltaCredits,
        double earlierPercent,
        double earlierResolution,
        double laterPercent,
        double laterResolution)
    {
        if (deltaCredits <= 0 ||
            !TryGetPercentageBounds(
                earlierPercent,
                earlierResolution,
                out var earlierLow,
                out var earlierHigh) ||
            !TryGetPercentageBounds(
                laterPercent,
                laterResolution,
                out var laterLow,
                out var laterHigh))
        {
            return null;
        }

        try
        {
            var deltaPercentLow = laterLow - earlierHigh;
            var deltaPercentHigh = laterHigh - earlierLow;
            if (deltaPercentLow <= 0)
            {
                return null;
            }

            var lowerUsd =
                deltaCredits / (deltaPercentHigh / 100m) * UsdPerCredit;
            var upperUsd =
                deltaCredits / (deltaPercentLow / 100m) * UsdPerCredit;
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
            .GroupBy(observation => new
            {
                observation.Segment,
                observation.Source,
                observation.UsedPercent,
                observation.PercentResolution,
                observation.AttributedCredits,
                observation.LowerUsd,
                observation.UpperUsd,
            })
            .Select(group => group.OrderByDescending(
                observation => observation.ObservedAt).First())
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
            new PeriodQuotaEstimate(
                RoundUsd(lowerUsd!.Value),
                RoundUsd(upperUsd!.Value)),
            observationCount == 1
                ? QuotaEstimateQuality.Initial
                : QuotaEstimateQuality.MultiPoint,
            observationCount,
            ignoredConflictingHistory);
    }

    private static bool IsValidPercentage(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 100;

    private static bool TryGetPercentageBounds(
        double value,
        double percentResolution,
        out decimal low,
        out decimal high)
    {
        low = default;
        high = default;
        if (!IsValidPercentage(value) ||
            !TryGetResolution(percentResolution, out var resolution))
        {
            return false;
        }

        try
        {
            var percent = (decimal)value;
            var halfResolution = resolution / 2m;
            low = Math.Max(0m, percent - halfResolution);
            high = Math.Min(100m, percent + halfResolution);
            return high >= low;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryGetResolution(double value, out decimal resolution)
    {
        resolution = default;
        if (!double.IsFinite(value) ||
            value <= 0)
        {
            return false;
        }

        try
        {
            resolution = (decimal)value;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static decimal RoundUsd(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static PeriodQuotaEstimate? RoundEstimate(
        PeriodQuotaEstimate? estimate) =>
        estimate is null
            ? null
            : new PeriodQuotaEstimate(
                RoundUsd(estimate.LowerUsd),
                RoundUsd(estimate.UpperUsd));
}
