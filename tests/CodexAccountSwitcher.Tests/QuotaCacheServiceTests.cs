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
        var display = Assert.Single(result.Accounts).Value.Display;
        Assert.Equal(QuotaEstimateSource.Local, display.EstimateSource);
        Assert.Equal(QuotaEstimateQuality.MultiPoint, display.EstimateQuality);
        Assert.Equal("部分用量无法计价，区间可能偏低", display.EstimateStatus);
        Assert.Equal(3, display.EstimateObservationCount);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Schema_one_estimate_without_source_metadata_is_migrated_in_memory()
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
                    "period": 1,
                    "remainingPercent": 84,
                    "resetsAt": "2026-08-22T22:06:00+00:00",
                    "windowDuration": "30.00:00:00",
                    "tooltip": "Monthly: 84% remaining",
                    "usedPercent": 16,
                    "estimatedPeriodQuotaLowerUsd": 160,
                    "estimatedPeriodQuotaUpperUsd": 180
                  },
                  "refreshedAt": "2026-07-24T12:00:00+00:00"
                }
              }
            }
            """);
        var service = new QuotaCacheService(path);

        var loaded = await service.LoadAsync(default);

        Assert.Null(loaded.Error);
        var display = Assert.Single(loaded.Accounts).Value.Display;
        Assert.Equal(QuotaEstimateSource.Analytics, display.EstimateSource);
        Assert.Equal(QuotaEstimateQuality.Initial, display.EstimateQuality);
        Assert.Equal(1, display.EstimateObservationCount);
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

    [Theory]
    [InlineData("source-enum")]
    [InlineData("quality-enum")]
    [InlineData("negative-observation-count")]
    [InlineData("bounds-without-source")]
    public async Task Save_rejects_invalid_estimate_metadata_without_overwriting_cache(
        string invalidCase)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quota-cache.json");
        var service = new QuotaCacheService(path);
        await service.SaveAsync(
            new Dictionary<string, QuotaCacheEntry> { ["account-a"] = CreateEntry() },
            default);
        var original = await File.ReadAllTextAsync(path);
        var display = CreateEntry().Display;
        display = invalidCase switch
        {
            "source-enum" => display with
            {
                EstimateSource = (QuotaEstimateSource)99,
            },
            "quality-enum" => display with
            {
                EstimateQuality = (QuotaEstimateQuality)99,
            },
            "negative-observation-count" => display with
            {
                EstimateObservationCount = -1,
            },
            "bounds-without-source" => display with
            {
                EstimateSource = QuotaEstimateSource.None,
            },
            _ => throw new InvalidOperationException(invalidCase),
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveAsync(
                new Dictionary<string, QuotaCacheEntry>
                {
                    ["account-a"] = CreateEntry() with { Display = display },
                },
                default));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
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

    [Fact]
    public async Task Null_loaded_display_is_reported_and_blocks_overwrite()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quota-cache.json");
        const string invalid =
            """{"schemaVersion":1,"accounts":{"account-a":{"display":null,"refreshedAt":"2026-07-24T12:00:00Z"}}}""";
        await File.WriteAllTextAsync(path, invalid);
        var service = new QuotaCacheService(path);

        var loaded = await service.LoadAsync(default);

        Assert.NotNull(loaded.Error);
        Assert.Empty(loaded.Accounts);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(new Dictionary<string, QuotaCacheEntry>(), default));
        Assert.Equal(invalid, await File.ReadAllTextAsync(path));
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
            EstimateSource = QuotaEstimateSource.Local,
            EstimateQuality = QuotaEstimateQuality.MultiPoint,
            EstimateStatus = "部分用量无法计价，区间可能偏低",
            EstimateObservationCount = 3,
        },
        DateTimeOffset.Parse("2026-07-24T12:00:00Z"));
}
