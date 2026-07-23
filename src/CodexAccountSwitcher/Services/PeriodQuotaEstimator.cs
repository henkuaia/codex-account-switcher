using System.Globalization;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public static class PeriodQuotaEstimator
{
    private const decimal UsdPerCredit = 40m / 1000m;

    public static PeriodQuotaEstimate? TryEstimate(
        string json,
        double usedPercent,
        DateOnly segmentStartDate,
        bool includeStartDayInLower)
    {
        if (!double.IsFinite(usedPercent) || usedPercent <= 0 || usedPercent > 100)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            decimal includedCredits = 0;
            decimal startDayCredits = 0;
            foreach (var row in data.EnumerateArray())
            {
                if (!TryReadCredits(row, out var credits))
                {
                    continue;
                }

                includedCredits += credits;
                if (row.TryGetProperty("date", out var date) &&
                    date.ValueKind == JsonValueKind.String &&
                    DateOnly.TryParseExact(
                        date.GetString(),
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var rowDate) &&
                    rowDate == segmentStartDate)
                {
                    startDayCredits += credits;
                }
            }

            var usedRatio = (decimal)usedPercent / 100m;
            var lowerCredits = includeStartDayInLower
                ? includedCredits
                : Math.Max(0, includedCredits - startDayCredits);
            var lowerUsd = RoundUsd(lowerCredits / usedRatio * UsdPerCredit);
            var upperUsd = RoundUsd(includedCredits / usedRatio * UsdPerCredit);
            return new PeriodQuotaEstimate(
                Math.Min(lowerUsd, upperUsd),
                Math.Max(lowerUsd, upperUsd));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool TryReadCredits(JsonElement row, out decimal credits)
    {
        credits = default;
        return row.ValueKind == JsonValueKind.Object &&
            row.TryGetProperty("totals", out var totals) &&
            totals.ValueKind == JsonValueKind.Object &&
            totals.TryGetProperty("credits", out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out credits) &&
            credits >= 0;
    }

    private static decimal RoundUsd(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
