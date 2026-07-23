using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public static class WeeklyQuotaEstimator
{
    public static WeeklyQuotaEstimate? TryEstimate(
        string json,
        double usedPercent,
        DateOnly resetDate)
    {
        var estimate = PeriodQuotaEstimator.TryEstimate(
            json,
            usedPercent,
            resetDate,
            includeStartDayInLower: false);
        return estimate is null
            ? null
            : new WeeklyQuotaEstimate(estimate.LowerUsd, estimate.UpperUsd);
    }
}
