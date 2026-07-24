using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class QuotaCacheServiceTests
{
    [Fact]
    public async Task Missing_file_loads_empty_cache()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaCacheService(Path.Combine(directory.Path, "quota-cache.json"));

        var result = await service.LoadAsync(default);

        Assert.Empty(result.Accounts);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Cache_round_trips_complete_display_by_account_key_without_temp_residue()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quota-cache.json");
        var service = new QuotaCacheService(path);
        var expected = new Dictionary<string, QuotaCacheEntry>(StringComparer.Ordinal)
        {
            ["account-a"] = CreateEntry(),
        };

        await service.SaveAsync(expected, default);
        var result = await service.LoadAsync(default);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Accounts);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Unsupported_schema_blocks_overwrite_and_preserves_original_file()
    {
        using var directory = new TemporaryDirectory();
        const string unsupported = """{"schemaVersion":99,"accounts":{}}""";
        var path = Path.Combine(directory.Path, "quota-cache.json");
        await File.WriteAllTextAsync(path, unsupported);
        var service = new QuotaCacheService(path);

        var loaded = await service.LoadAsync(default);

        Assert.NotNull(loaded.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(new Dictionary<string, QuotaCacheEntry>(), default));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Malformed_cache_blocks_overwrite_and_preserves_original_file()
    {
        using var directory = new TemporaryDirectory();
        const string malformed = """{"schemaVersion":1,"accounts":""";
        var path = Path.Combine(directory.Path, "quota-cache.json");
        await File.WriteAllTextAsync(path, malformed);
        var service = new QuotaCacheService(path);

        var loaded = await service.LoadAsync(default);

        Assert.NotNull(loaded.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(new Dictionary<string, QuotaCacheEntry>(), default));
        Assert.Equal(malformed, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_rejects_empty_account_key()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaCacheService(Path.Combine(directory.Path, "quota-cache.json"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(
                new Dictionary<string, QuotaCacheEntry> { [""] = CreateEntry() },
                default));
    }

    [Fact]
    public async Task Save_rejects_invalid_percentages()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaCacheService(Path.Combine(directory.Path, "quota-cache.json"));
        var invalid = CreateEntry() with
        {
            Display = CreateEntry().Display with
            {
                RemainingPercent = 101,
                UsedPercent = double.NaN,
            },
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveAsync(
                new Dictionary<string, QuotaCacheEntry> { ["account-a"] = invalid },
                default));
    }

    [Fact]
    public async Task Save_rejects_negative_or_inconsistent_money_values()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaCacheService(Path.Combine(directory.Path, "quota-cache.json"));
        var invalid = CreateEntry() with
        {
            Display = CreateEntry().Display with
            {
                IndividualLimitUsd = -1,
                EstimatedPeriodQuotaLowerUsd = 200,
                EstimatedPeriodQuotaUpperUsd = 160,
            },
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveAsync(
                new Dictionary<string, QuotaCacheEntry> { ["account-a"] = invalid },
                default));
    }

    [Fact]
    public async Task Invalid_loaded_entry_is_reported_and_blocks_overwrite()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quota-cache.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "accounts": {
                "account-a": {
                  "display": {
                    "period": 0,
                    "remainingPercent": -1,
                    "resetsAt": "2026-08-22T22:06:00+00:00",
                    "windowDuration": "30.00:00:00",
                    "tooltip": "Monthly"
                  },
                  "refreshedAt": "2026-07-24T12:00:00+00:00"
                }
              }
            }
            """);
        var service = new QuotaCacheService(path);

        var loaded = await service.LoadAsync(default);

        Assert.NotNull(loaded.Error);
        Assert.Empty(loaded.Accounts);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(new Dictionary<string, QuotaCacheEntry>(), default));
    }

    private static QuotaCacheEntry CreateEntry() => new(
        new QuotaDisplay(
            QuotaPeriod.Monthly,
            84,
            DateTimeOffset.Parse("2026-08-22T22:06:00Z"),
            TimeSpan.FromDays(30),
            "Monthly: 84% remaining")
        {
            AvailableResetCount = 2,
            IndividualLimitUsd = 200m,
            UsedPercent = 16,
            ServerNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z"),
            EstimatedPeriodQuotaLowerUsd = 160m,
            EstimatedPeriodQuotaUpperUsd = 180m,
        },
        DateTimeOffset.Parse("2026-07-24T12:00:00Z"));
}
