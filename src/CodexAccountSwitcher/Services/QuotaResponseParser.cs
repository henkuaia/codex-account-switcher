using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public static class QuotaResponseParser
{
    private const long MinimumLongWindowSeconds = 518400;
    private const long MaximumWeeklyWindowSeconds = 691200;
    private const long MinimumMonthlyWindowSeconds = 2332800;
    private const long MaximumMonthlyWindowSeconds = 2764800;
    private const string InvalidResponseError = "The quota response is invalid.";

    public static QuotaParseResult Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var candidates = ReadCandidates(document.RootElement);
            if (candidates.Count == 0)
            {
                return QuotaParseResult.Success(null);
            }

            var display = candidates.MinBy(candidate => candidate.RemainingPercent)!;
            return QuotaParseResult.Success(display with { Tooltip = BuildTooltip(candidates) });
        }
        catch (JsonException)
        {
            return QuotaParseResult.Failure(InvalidResponseError);
        }
        catch (ArgumentOutOfRangeException)
        {
            return QuotaParseResult.Failure(InvalidResponseError);
        }
        catch (OverflowException)
        {
            return QuotaParseResult.Failure(InvalidResponseError);
        }
    }

    private static List<QuotaDisplay> ReadCandidates(JsonElement root)
    {
        var candidates = new List<QuotaDisplay>();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("rate_limit", out var rateLimit) ||
            rateLimit.ValueKind != JsonValueKind.Object)
        {
            return candidates;
        }

        AddCandidate(rateLimit, "primary_window", candidates);
        AddCandidate(rateLimit, "secondary_window", candidates);
        return candidates;
    }

    private static void AddCandidate(
        JsonElement rateLimit,
        string propertyName,
        ICollection<QuotaDisplay> candidates)
    {
        if (!rateLimit.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !TryReadInteger(window, "used_percent", out var usedPercent) ||
            !TryReadInteger(window, "limit_window_seconds", out var windowSeconds) ||
            windowSeconds < MinimumLongWindowSeconds)
        {
            return;
        }

        candidates.Add(new QuotaDisplay(
            ResolvePeriod(windowSeconds),
            RemainingPercent(usedPercent),
            ReadResetAt(window),
            TimeSpan.FromSeconds(windowSeconds),
            string.Empty));
    }

    private static bool TryReadInteger(JsonElement element, string propertyName, out long value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value);
    }

    private static int RemainingPercent(long usedPercent) => usedPercent switch
    {
        <= 0 => 100,
        >= 100 => 0,
        _ => 100 - (int)usedPercent,
    };

    private static QuotaPeriod ResolvePeriod(long windowSeconds) => windowSeconds switch
    {
        >= MinimumLongWindowSeconds and <= MaximumWeeklyWindowSeconds => QuotaPeriod.Weekly,
        >= MinimumMonthlyWindowSeconds and <= MaximumMonthlyWindowSeconds => QuotaPeriod.Monthly,
        _ => QuotaPeriod.Unknown,
    };

    private static DateTimeOffset? ReadResetAt(JsonElement window)
    {
        if (!TryReadInteger(window, "reset_at", out var resetAt))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(resetAt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string BuildTooltip(IEnumerable<QuotaDisplay> candidates) =>
        string.Join("; ", candidates.Select(candidate =>
            $"{candidate.Period}: {candidate.RemainingPercent}% remaining"));
}
