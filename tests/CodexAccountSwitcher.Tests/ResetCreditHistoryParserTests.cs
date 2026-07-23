using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class ResetCreditHistoryParserTests
{
    private static readonly DateTimeOffset WindowStart =
        DateTimeOffset.Parse("2026-07-23T22:06:00Z");
    private static readonly DateTimeOffset ServerNow =
        DateTimeOffset.Parse("2026-07-30T00:00:00Z");

    [Fact]
    public void Finds_latest_redeemed_credit_inside_window()
    {
        const string json = """
            {"credits":[
              {"status":"redeemed","redeemed_at":"2026-07-20T10:00:00Z"},
              {"status":"available","redeemed_at":null},
              {"status":"redeemed","redeemed_at":"2026-07-25T08:00:00Z"},
              {"status":"REDEEMED","redeemed_at":"2026-07-26T12:30:00Z"},
              {"status":"redeemed","redeemed_at":"2026-08-31T00:00:00Z"}
            ]}
            """;

        var valid = ResetCreditHistoryParser.TryFindLatestRedeemedAt(
            json,
            WindowStart,
            ServerNow,
            out var latest);

        Assert.True(valid);
        Assert.Equal(DateTimeOffset.Parse("2026-07-26T12:30:00Z"), latest);
    }

    [Fact]
    public void Valid_empty_history_returns_no_redeemed_credit()
    {
        var valid = ResetCreditHistoryParser.TryFindLatestRedeemedAt(
            """{"credits":[]}""",
            WindowStart,
            ServerNow,
            out var latest);

        Assert.True(valid);
        Assert.Null(latest);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{}""")]
    [InlineData("""{"credits":{}}""")]
    [InlineData("""{"credits":[{"status":"redeemed"}]}""")]
    [InlineData("""{"credits":[{"status":"redeemed","redeemed_at":"not-a-date"}]}""")]
    [InlineData("""not-json""")]
    public void Invalid_history_is_rejected(string json)
    {
        var valid = ResetCreditHistoryParser.TryFindLatestRedeemedAt(
            json,
            WindowStart,
            ServerNow,
            out var latest);

        Assert.False(valid);
        Assert.Null(latest);
    }

    [Fact]
    public void Invalid_time_range_is_rejected()
    {
        var valid = ResetCreditHistoryParser.TryFindLatestRedeemedAt(
            """{"credits":[]}""",
            ServerNow,
            WindowStart,
            out var latest);

        Assert.False(valid);
        Assert.Null(latest);
    }
}
