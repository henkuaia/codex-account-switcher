using System.Globalization;
using System.Text.Json;

namespace CodexAccountSwitcher.Services;

public static class ResetCreditHistoryParser
{
    public static bool TryFindLatestRedeemedAt(
        string json,
        DateTimeOffset windowStart,
        DateTimeOffset serverNow,
        out DateTimeOffset? latestRedeemedAt)
    {
        latestRedeemedAt = null;
        if (serverNow < windowStart)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("credits", out var credits) ||
                credits.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var credit in credits.EnumerateArray())
            {
                if (credit.ValueKind != JsonValueKind.Object ||
                    !credit.TryGetProperty("status", out var status) ||
                    status.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                if (!string.Equals(
                        status.GetString(),
                        "redeemed",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!credit.TryGetProperty("redeemed_at", out var redeemedAt) ||
                    redeemedAt.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(
                        redeemedAt.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var parsed))
                {
                    return false;
                }

                if (parsed >= windowStart &&
                    parsed <= serverNow &&
                    (latestRedeemedAt is null || parsed > latestRedeemedAt.Value))
                {
                    latestRedeemedAt = parsed;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            latestRedeemedAt = null;
            return false;
        }
    }
}
