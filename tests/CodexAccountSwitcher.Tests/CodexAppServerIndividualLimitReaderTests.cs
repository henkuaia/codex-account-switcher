using System.Text.Json;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class CodexAppServerIndividualLimitReaderTests
{
    [Fact]
    public void Parses_authoritative_individual_limit_snapshot()
    {
        using var document = JsonDocument.Parse("""
        {
          "id": 2,
          "result": {
            "rateLimits": {
              "individualLimit": {
                "limit": "5000",
                "used": "625.5",
                "remainingPercent": 87,
                "resetsAt": 1787662667
              }
            },
            "rateLimitResetCredits": {
              "availableCount": 3
            }
          }
        }
        """);

        var result = CodexAppServerIndividualLimitReader.ParseResponse(
            document.RootElement);

        Assert.NotNull(result);
        Assert.Equal(5000m, result.LimitCredits);
        Assert.Equal(625.5m, result.UsedCredits);
        Assert.Equal(87, result.RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787662667), result.ResetsAt);
        Assert.Equal(3, result.AvailableResetCount);
    }

    [Fact]
    public void Missing_individual_limit_uses_estimator_fallback()
    {
        using var document = JsonDocument.Parse("""
        {"id":2,"result":{"rateLimits":{"individualLimit":null}}}
        """);

        Assert.Null(CodexAppServerIndividualLimitReader.ParseResponse(
            document.RootElement));
    }
}
