using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class QuotaResponseParserTests
{
    [Theory]
    [InlineData(518400, QuotaPeriod.Weekly)]
    [InlineData(604800, QuotaPeriod.Weekly)]
    [InlineData(691200, QuotaPeriod.Weekly)]
    [InlineData(2332800, QuotaPeriod.Monthly)]
    [InlineData(2592000, QuotaPeriod.Monthly)]
    [InlineData(2764800, QuotaPeriod.Monthly)]
    public void Labels_supported_long_windows(long seconds, QuotaPeriod expected)
    {
        var json = $$"""
        {
          "rate_limit": {
            "primary_window": {
              "used_percent": 27,
              "limit_window_seconds": {{seconds}},
              "reset_at": 1785000000
            }
          }
        }
        """;

        var result = QuotaResponseParser.Parse(json);

        Assert.Equal(expected, result.Display!.Period);
        Assert.Equal(73, result.Display.RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785000000), result.Display.ResetsAt);
    }

    [Theory]
    [InlineData(18000)]
    [InlineData(518399)]
    public void Ignores_windows_shorter_than_six_days(long seconds)
    {
        var json = $$"""
        {
          "rate_limit": {
            "primary_window": {
              "used_percent": 10,
              "limit_window_seconds": {{seconds}},
              "reset_at": 1785000000
            }
          }
        }
        """;

        Assert.Null(QuotaResponseParser.Parse(json).Display);
    }

    [Theory]
    [InlineData(691201)]
    [InlineData(864000)]
    [InlineData(2332799)]
    [InlineData(2764801)]
    public void Unknown_long_window_is_not_guessed(long seconds)
    {
        var json = $$"""
        {
          "rate_limit": {
            "primary_window": {
              "used_percent": 15,
              "limit_window_seconds": {{seconds}},
              "reset_at": 1785000000
            }
          }
        }
        """;

        Assert.Equal(QuotaPeriod.Unknown, QuotaResponseParser.Parse(json).Display!.Period);
    }

    [Fact]
    public void Multiple_long_windows_choose_the_most_restrictive()
    {
        var json = """
        {"rate_limit":{
          "primary_window":{"used_percent":20,"limit_window_seconds":604800,"reset_at":1785000000},
          "secondary_window":{"used_percent":75,"limit_window_seconds":2592000,"reset_at":1787000000}
        }}
        """;

        var display = QuotaResponseParser.Parse(json).Display!;

        Assert.Equal(QuotaPeriod.Monthly, display.Period);
        Assert.Equal(25, display.RemainingPercent);
        Assert.Equal(TimeSpan.FromSeconds(2592000), display.WindowDuration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787000000), display.ResetsAt);
        Assert.Contains("Weekly", display.Tooltip);
        Assert.Contains("Monthly", display.Tooltip);
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("\"3\"", 3)]
    public void Reads_available_reset_count_from_usage_snapshot(string value, int expected)
    {
        var json =
            "{\"rate_limit\":{\"primary_window\":{\"used_percent\":20," +
            "\"limit_window_seconds\":604800}}," +
            "\"rate_limit_reset_credits\":{\"available_count\":" + value + "}}";

        var display = QuotaResponseParser.Parse(json).Display!;

        Assert.Equal(expected, display.AvailableResetCount);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"invalid\"")]
    public void Invalid_available_reset_count_remains_unknown(string value)
    {
        var json =
            "{\"rate_limit\":{\"primary_window\":{\"used_percent\":20," +
            "\"limit_window_seconds\":604800}}," +
            "\"rate_limit_reset_credits\":{\"available_count\":" + value + "}}";

        Assert.Null(QuotaResponseParser.Parse(json).Display!.AvailableResetCount);
    }

    [Fact]
    public void Reads_official_monthly_limit_without_treating_credit_balance_as_period_quota()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window": {
              "used_percent": 20,
              "limit_window_seconds": 604800
            }
          },
          "rate_limit_reset_credits": { "available_count": 2 },
          "individual_limit": 200,
          "credits": { "balance": "9000" }
        }
        """;

        var display = QuotaResponseParser.Parse(json).Display!;

        Assert.Equal(200m, display.IndividualLimitUsd);
    }

    [Fact]
    public void Credit_balance_alone_does_not_populate_official_monthly_limit()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window": {
              "used_percent": 20,
              "limit_window_seconds": 604800
            }
          },
          "credits": { "balance": "9000" }
        }
        """;

        Assert.Null(QuotaResponseParser.Parse(json).Display!.IndividualLimitUsd);
    }

    [Fact]
    public void Ignores_unrecognized_window_properties()
    {
        var json = """
        {"rate_limit":{"tertiary_window":{"used_percent":10,"limit_window_seconds":604800}}}
        """;

        Assert.Null(QuotaResponseParser.Parse(json).Display);
    }

    [Theory]
    [InlineData(-10, 100)]
    [InlineData(150, 0)]
    public void Clamps_remaining_percentage(int usedPercent, int expectedRemaining)
    {
        var json = $$"""
        {
          "rate_limit": {
            "primary_window": {
              "used_percent": {{usedPercent}},
              "limit_window_seconds": 604800,
              "reset_at": 1785000000
            }
          }
        }
        """;

        var display = QuotaResponseParser.Parse(json).Display!;

        Assert.Equal(expectedRemaining, display.RemainingPercent);
    }

    [Theory]
    [InlineData("-0.1", 100)]
    [InlineData("27.4", 73)]
    [InlineData("27.5", 73)]
    [InlineData("27.6", 72)]
    [InlineData("99.5", 1)]
    [InlineData("100.1", 0)]
    public void Accepts_fractional_used_percent_and_rounds_remaining_away_from_zero(
        string usedPercent,
        int expectedRemaining)
    {
        var json =
            "{\"rate_limit\":{\"primary_window\":{\"used_percent\":" + usedPercent +
            ",\"limit_window_seconds\":604800}}}";

        var result = QuotaResponseParser.Parse(json);

        Assert.Null(result.Error);
        Assert.Equal(expectedRemaining, result.Display!.RemainingPercent);
    }

    [Theory]
    [InlineData("1e309")]
    [InlineData("-1e309")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Non_finite_used_percent_returns_fixed_parse_error(string usedPercent)
    {
        var json =
            "{\"rate_limit\":{\"primary_window\":{\"used_percent\":" + usedPercent +
            ",\"limit_window_seconds\":604800}}}";

        var result = QuotaResponseParser.Parse(json);

        Assert.Null(result.Display);
        Assert.Equal("The quota response is invalid.", result.Error);
    }

    [Fact]
    public void Missing_reset_time_remains_null()
    {
        var json = """
        {"rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":604800}}}
        """;

        Assert.Null(QuotaResponseParser.Parse(json).Display!.ResetsAt);
    }

    [Fact]
    public void Malformed_json_returns_sanitized_parse_error()
    {
        const string malformedJson = "{\"secret\":\"raw-response-secret\"";

        var result = QuotaResponseParser.Parse(malformedJson);

        Assert.Null(result.Display);
        Assert.Equal("The quota response is invalid.", result.Error);
        Assert.DoesNotContain("raw-response-secret", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrepresentable_window_duration_returns_sanitized_parse_error()
    {
        const string duration = "9223372036854775807";
        var json = """
        {"rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":9223372036854775807}}}
        """;

        var result = QuotaResponseParser.Parse(json);

        Assert.Null(result.Display);
        Assert.Equal("The quota response is invalid.", result.Error);
        Assert.DoesNotContain(duration, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooltip_retains_each_period_and_exact_reset_time()
    {
        var weeklyReset = DateTimeOffset.FromUnixTimeSeconds(1785000000);
        var unknownReset = DateTimeOffset.FromUnixTimeSeconds(1787000000);
        const string json = """
        {"rate_limit":{
          "primary_window":{"used_percent":20.5,"limit_window_seconds":604800,"reset_at":1785000000},
          "secondary_window":{"used_percent":75.5,"limit_window_seconds":864000,"reset_at":1787000000}
        }}
        """;

        var display = QuotaResponseParser.Parse(json).Display!;

        Assert.Contains("Weekly", display.Tooltip, StringComparison.Ordinal);
        Assert.Contains("Unknown", display.Tooltip, StringComparison.Ordinal);
        Assert.Contains(weeklyReset.ToString("yyyy-MM-dd HH:mm 'UTC'"), display.Tooltip, StringComparison.Ordinal);
        Assert.Contains(unknownReset.ToString("yyyy-MM-dd HH:mm 'UTC'"), display.Tooltip, StringComparison.Ordinal);
    }
}
