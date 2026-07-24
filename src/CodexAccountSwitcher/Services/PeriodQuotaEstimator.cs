using System.Globalization;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public enum AnalyticsUsageState { Valid, Empty, Invalid }

public sealed record AnalyticsUsageParseResult(
    AnalyticsUsageState State,
    decimal LowerCredits,
    decimal UpperCredits);

public static class PeriodQuotaEstimator
{
    public static PeriodQuotaEstimate? TryEstimate(
        string json,
        double usedPercent,
        DateOnly segmentStartDate,
        bool includeStartDayInLower)
    {
        var usage = Parse(json, segmentStartDate, includeStartDayInLower);
        return usage.State != AnalyticsUsageState.Valid
            ? null
            : QuotaEstimateMath.TryCreateFullInterval(
                usage.LowerCredits,
                usage.UpperCredits,
                usedPercent,
                percentResolution: 1);
    }

    public static AnalyticsUsageParseResult Parse(string json) =>
        Parse(json, default, includeStartDayInLower: true);

    public static AnalyticsUsageParseResult Parse(
        string json,
        DateOnly segmentStartDate,
        bool includeStartDayInLower)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return InvalidResult();
            }

            if (data.GetArrayLength() == 0)
            {
                return new AnalyticsUsageParseResult(
                    AnalyticsUsageState.Empty,
                    LowerCredits: 0,
                    UpperCredits: 0);
            }

            decimal includedCredits = 0;
            decimal startDayCredits = 0;
            foreach (var row in data.EnumerateArray())
            {
                if (!TryReadRow(row, out var rowDate, out var credits))
                {
                    return InvalidResult();
                }

                includedCredits += credits;
                if (rowDate == segmentStartDate)
                {
                    startDayCredits += credits;
                }
            }

            var lowerCredits = includeStartDayInLower
                ? includedCredits
                : Math.Max(0, includedCredits - startDayCredits);
            return new AnalyticsUsageParseResult(
                AnalyticsUsageState.Valid,
                lowerCredits,
                includedCredits);
        }
        catch (JsonException)
        {
            return InvalidResult();
        }
        catch (OverflowException)
        {
            return InvalidResult();
        }
    }

    private static bool TryReadRow(
        JsonElement row,
        out DateOnly date,
        out decimal credits)
    {
        date = default;
        credits = default;
        return row.ValueKind == JsonValueKind.Object &&
            row.TryGetProperty("date", out var dateValue) &&
            dateValue.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(
                dateValue.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date) &&
            row.TryGetProperty("totals", out var totals) &&
            totals.ValueKind == JsonValueKind.Object &&
            totals.TryGetProperty("credits", out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out credits) &&
            credits >= 0;
    }

    private static AnalyticsUsageParseResult InvalidResult() =>
        new(AnalyticsUsageState.Invalid, LowerCredits: 0, UpperCredits: 0);
}
