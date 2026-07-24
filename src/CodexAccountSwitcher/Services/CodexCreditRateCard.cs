using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed class CodexCreditRateCard
{
    private sealed record Rates(decimal Input, decimal CachedInput, decimal Output);

    private static readonly IReadOnlyDictionary<string, Rates> StandardRates =
        new Dictionary<string, Rates>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = new(125m, 12.5m, 750m),
            ["gpt-5.6-terra"] = new(62.5m, 6.25m, 375m),
            ["gpt-5.6-luna"] = new(25m, 2.5m, 150m),
            ["gpt-5.5"] = new(125m, 12.5m, 750m),
            ["gpt-5.5-cyber"] = new(500m, 50m, 3000m),
            ["gpt-5.4"] = new(62.5m, 6.25m, 375m),
            ["gpt-5.4-mini"] = new(18.75m, 1.875m, 113m),
            ["gpt-5.3-codex"] = new(43.75m, 4.375m, 350m),
            ["gpt-5.2"] = new(43.75m, 4.375m, 350m),
        };

    public bool TryCalculateCredits(LocalUsageEvent usage, out decimal credits)
    {
        credits = 0m;
        if (string.IsNullOrWhiteSpace(usage.Model) ||
            usage.InputTokens < 0 ||
            usage.CachedInputTokens < 0 ||
            usage.OutputTokens < 0 ||
            usage.CachedInputTokens > usage.InputTokens ||
            !StandardRates.TryGetValue(usage.Model, out var rates) ||
            !TryResolveFastMultiplier(usage.Model, usage.ServiceTier, out var multiplier))
        {
            return false;
        }

        var uncachedInput = usage.InputTokens - usage.CachedInputTokens;
        credits = (
            uncachedInput * rates.Input +
            usage.CachedInputTokens * rates.CachedInput +
            usage.OutputTokens * rates.Output) / 1_000_000m;
        credits *= multiplier;
        credits = Math.Round(credits, 9, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool TryResolveFastMultiplier(
        string model,
        string serviceTier,
        out decimal multiplier)
    {
        if (string.Equals(serviceTier, "default", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1m;
            return true;
        }

        if (!string.Equals(serviceTier, "priority", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 0m;
            return false;
        }

        if (model.StartsWith("gpt-5.6-", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("gpt-5.5", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 2.5m;
            return true;
        }

        if (string.Equals(model, "gpt-5.4", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 2m;
            return true;
        }

        multiplier = 0m;
        return false;
    }
}
