using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class CodexCreditRateCardTests
{
    [Theory]
    [InlineData("gpt-5.6-sol", 125, 12.5, 750)]
    [InlineData("gpt-5.6-terra", 62.5, 6.25, 375)]
    [InlineData("gpt-5.6-luna", 25, 2.5, 150)]
    [InlineData("gpt-5.5", 125, 12.5, 750)]
    [InlineData("gpt-5.5-cyber", 500, 50, 3000)]
    [InlineData("gpt-5.4", 62.5, 6.25, 375)]
    [InlineData("gpt-5.4-mini", 18.75, 1.875, 113)]
    [InlineData("gpt-5.3-codex", 43.75, 4.375, 350)]
    [InlineData("gpt-5.2", 43.75, 4.375, 350)]
    public void Calculates_standard_rate_per_one_million_tokens(
        string model,
        double inputRate,
        double cachedInputRate,
        double outputRate)
    {
        var card = new CodexCreditRateCard();
        var usage = Usage(model, "default", inputTokens: 1_000_000, cachedInputTokens: 1, outputTokens: 1);

        var calculated = card.TryCalculateCredits(usage, out var credits);

        var expected = Math.Round(
            ((999_999m * (decimal)inputRate) +
             ((decimal)cachedInputRate) +
             ((decimal)outputRate)) / 1_000_000m,
            9,
            MidpointRounding.AwayFromZero);
        Assert.True(calculated);
        Assert.Equal(expected, credits);
    }

    [Fact]
    public void Charges_uncached_cached_and_output_tokens_exactly_once()
    {
        var card = new CodexCreditRateCard();
        var usage = Usage("gpt-5.4", "default", 20_203, 10_000, 397);

        var calculated = card.TryCalculateCredits(usage, out var credits);

        Assert.True(calculated);
        Assert.Equal(0.849_062_5m, credits);
    }

    [Theory]
    [InlineData("gpt-5.6-sol", 2.1875)]
    [InlineData("gpt-5.5", 2.1875)]
    [InlineData("gpt-5.4", 0.875)]
    public void Applies_priority_multiplier_for_supported_models(
        string model,
        double expectedCredits)
    {
        var card = new CodexCreditRateCard();
        var usage = Usage(model, "priority", 1_000, 0, 1_000);

        var calculated = card.TryCalculateCredits(usage, out var credits);

        Assert.True(calculated);
        Assert.Equal((decimal)expectedCredits, credits);
    }

    [Theory]
    [InlineData("gpt-5.4", "flex")]
    [InlineData("gpt-5.4-mini", "priority")]
    [InlineData("gpt-5.3-codex", "priority")]
    [InlineData("gpt-5.2", "priority")]
    [InlineData("gpt-5.4", "")]
    public void Rejects_unknown_or_unsupported_service_tier(string model, string serviceTier)
    {
        var card = new CodexCreditRateCard();

        var calculated = card.TryCalculateCredits(Usage(model, serviceTier, 1, 0, 1), out var credits);

        Assert.False(calculated);
        Assert.Equal(0m, credits);
    }

    [Fact]
    public void Detailed_result_distinguishes_unknown_tier_from_unknown_model()
    {
        var card = new CodexCreditRateCard();

        var unknownTier = card.CalculateCredits(
            Usage("gpt-5.4", string.Empty, 1, 0, 1));
        var unknownModel = card.CalculateCredits(
            Usage("gpt-unknown", "default", 1, 0, 1));

        Assert.False(string.IsNullOrWhiteSpace(CodexCreditRateCard.Version));
        Assert.Equal(CreditPricingFailureReason.UnknownServiceTier, unknownTier.FailureReason);
        Assert.Equal(CreditPricingFailureReason.UnknownModel, unknownModel.FailureReason);
    }

    [Theory]
    [InlineData(10, 11, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void Rejects_invalid_token_counts(long inputTokens, long cachedInputTokens, long outputTokens)
    {
        var card = new CodexCreditRateCard();

        var calculated = card.TryCalculateCredits(
            Usage("gpt-5.4", "default", inputTokens, cachedInputTokens, outputTokens),
            out var credits);

        Assert.False(calculated);
        Assert.Equal(0m, credits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gpt-unknown")]
    public void Rejects_blank_or_unknown_model(string model)
    {
        var card = new CodexCreditRateCard();

        var calculated = card.TryCalculateCredits(Usage(model, "default", 1, 0, 1), out var credits);

        Assert.False(calculated);
        Assert.Equal(0m, credits);
    }

    [Fact]
    public void Does_not_apply_a_context_window_multiplier()
    {
        var card = new CodexCreditRateCard();
        var usage = Usage("gpt-5.4", "default", 272_000, 0, 0);

        var calculated = card.TryCalculateCredits(usage, out var credits);

        Assert.True(calculated);
        Assert.Equal(17m, credits);
    }

    private static LocalUsageEvent Usage(
        string model,
        string serviceTier,
        long inputTokens,
        long cachedInputTokens,
        long outputTokens) => new(
        DateTimeOffset.Parse("2026-07-24T05:00:00Z"),
        model,
        serviceTier,
        inputTokens,
        cachedInputTokens,
        outputTokens);
}
