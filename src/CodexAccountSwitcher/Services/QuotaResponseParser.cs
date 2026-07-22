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
            !TryReadFiniteDouble(window, "used_percent", out var usedPercent) ||
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

    private static bool TryReadFiniteDouble(JsonElement element, string propertyName, out double value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (!property.TryGetDouble(out value) || !double.IsFinite(value))
        {
            throw new JsonException();
        }

        return true;
    }

    private static int RemainingPercent(double usedPercent)
    {
        // Clamp before subtraction, then round remaining halves away from zero.
        var remaining = 100d - Math.Clamp(usedPercent, 0d, 100d);
        return (int)Math.Round(remaining, MidpointRounding.AwayFromZero);
    }

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
        {
            var period = candidate.Period == QuotaPeriod.Unknown
                ? $"Unknown ({candidate.WindowDuration.TotalDays:0.##} days)"
                : candidate.Period.ToString();
            var reset = candidate.ResetsAt is { } resetsAt
                ? $", resets {resetsAt.UtcDateTime:yyyy-MM-dd HH:mm 'UTC'}"
                : string.Empty;
            return $"{period}: {candidate.RemainingPercent}% remaining{reset}";
        }));
}
