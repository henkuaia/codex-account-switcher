using System.Text.Json;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class QuotaEstimateLedgerServiceTests
{
    private static readonly DateTimeOffset SegmentStart =
        DateTimeOffset.Parse("2026-07-20T00:00:00Z");
    private static readonly DateTimeOffset Reset =
        DateTimeOffset.Parse("2026-07-27T00:00:00Z");

    [Fact]
    public async Task Missing_file_loads_empty_state()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaEstimateLedgerService(
            Path.Combine(directory.Path, "quota-estimate-ledger.json"));

        var result = await service.LoadAsync(default);

        Assert.Empty(result.State.Accounts);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Current_version_round_trips_complete_state_and_checkpoints_without_temp_residue()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quota-estimate-ledger.json");
        var service = new QuotaEstimateLedgerService(path);
        var expected = CreateState();

        await service.SaveAsync(expected, default);
        var result = await service.LoadAsync(default);

        Assert.Null(result.Error);
        var account = Assert.Single(result.State.Accounts);
        Assert.Equal("account-a", account.Key);
        Assert.Collection(
            account.Value.Activations,
            activation =>
            {
                Assert.Equal(DateTimeOffset.Parse("2026-07-20T01:00:00Z"), activation.StartedAt);
                Assert.Equal(DateTimeOffset.Parse("2026-07-20T02:00:00Z"), activation.EndedAt);
            },
            activation =>
            {
                Assert.Equal(DateTimeOffset.Parse("2026-07-20T03:00:00Z"), activation.StartedAt);
                Assert.Null(activation.EndedAt);
            });
        var observation = Assert.Single(account.Value.Observations);
        Assert.Equal(QuotaPeriod.Weekly, observation.Segment.Period);
        Assert.Equal(SegmentStart, observation.Segment.SegmentStart);
        Assert.Equal(Reset, observation.Segment.ResetsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T04:00:00Z"), observation.ObservedAt);
        Assert.Equal(31.25, observation.UsedPercent);
        Assert.Equal(0.5, observation.PercentResolution);
        Assert.Equal(125m, observation.AttributedCredits);
        Assert.True(observation.HasFullSegmentCoverage);
        Assert.Equal(4m, observation.LowerUsd);
        Assert.Equal(6m, observation.UpperUsd);
        Assert.Equal(QuotaEstimateSource.Local, observation.Source);
        Assert.Equal(QuotaObservationKind.FullSegment, observation.Kind);
        var checkpoint = Assert.Single(result.State.FileCheckpoints).Value;
        Assert.Equal("2026/07/session.jsonl", checkpoint.RelativePath);
        Assert.Equal(123, checkpoint.CompletedLineByteOffset);
        Assert.Equal("gpt-5.4", checkpoint.Model);
        Assert.Equal("priority", checkpoint.ServiceTier);
        Assert.Equal(CodexCreditRateCard.Version, checkpoint.RateCardVersion);
        Assert.Single(checkpoint.Aggregates);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Serialized_document_excludes_sensitive_property_names()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quota-estimate-ledger.json");
        var service = new QuotaEstimateLedgerService(path);

        await service.SaveAsync(CreateState(), default);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var propertyNames = EnumeratePropertyNames(document.RootElement).ToArray();
        var forbidden = new[] { "email", "token", "prompt", "response", "header", "raw", "json" };
        var forbiddenNames = propertyNames.Where(name =>
            !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
            forbidden.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        Assert.True(
            forbiddenNames.Length == 0,
            $"Forbidden serialized properties: {string.Join(", ", forbiddenNames)}");
    }

    [Fact]
    public async Task Unsupported_schema_blocks_overwrite_and_preserves_original_file()
    {
        using var directory = new TemporaryDirectory();
        const string unsupported = """{"schemaVersion":99,"accounts":{}}""";
        var path = Path.Combine(directory.Path, "quota-estimate-ledger.json");
        await File.WriteAllTextAsync(path, unsupported);
        var service = new QuotaEstimateLedgerService(path);

        var loaded = await service.LoadAsync(default);

        Assert.NotNull(loaded.Error);
        Assert.Empty(loaded.State.Accounts);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(QuotaEstimateLedgerState.Empty, default));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Malformed_file_blocks_overwrite_and_preserves_original_file()
    {
        using var directory = new TemporaryDirectory();
        const string malformed = """{"schemaVersion":1,"accounts":""";
        var path = Path.Combine(directory.Path, "quota-estimate-ledger.json");
        await File.WriteAllTextAsync(path, malformed);
        var service = new QuotaEstimateLedgerService(path);

        var loaded = await service.LoadAsync(default);

        Assert.NotNull(loaded.Error);
        Assert.Empty(loaded.State.Accounts);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(QuotaEstimateLedgerState.Empty, default));
        Assert.Equal(malformed, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_rejects_blank_account_keys()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaEstimateLedgerService(
            Path.Combine(directory.Path, "quota-estimate-ledger.json"));
        var invalid = new QuotaEstimateLedgerState(
            new Dictionary<string, AccountQuotaEstimateLedger>
            {
                [" "] = EmptyAccountLedger(),
            });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(invalid, default));
    }

    [Fact]
    public async Task Save_rejects_absolute_or_parent_traversal_checkpoint_paths()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaEstimateLedgerService(
            Path.Combine(directory.Path, "quota-estimate-ledger.json"));
        var checkpoint = CreateCheckpoint();

        foreach (var relativePath in new[] { @"C:\secret\session.jsonl", "../session.jsonl" })
        {
            var invalid = QuotaEstimateLedgerState.Empty with
            {
                FileCheckpoints = new Dictionary<string, LocalUsageFileCheckpoint>(
                    StringComparer.Ordinal)
                {
                    [relativePath] = checkpoint with { RelativePath = relativePath },
                },
            };

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.SaveAsync(invalid, default));
        }
    }

    [Fact]
    public async Task Save_rejects_invalid_activation_intervals()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaEstimateLedgerService(
            Path.Combine(directory.Path, "quota-estimate-ledger.json"));
        var invalidActivationLists = new[]
        {
            new[]
            {
                new AccountActivationInterval(
                    DateTimeOffset.Parse("2026-07-24T01:00:00+01:00"),
                    DateTimeOffset.Parse("2026-07-24T02:00:00+01:00")),
            },
            new[]
            {
                new AccountActivationInterval(
                    DateTimeOffset.Parse("2026-07-24T02:00:00Z"),
                    DateTimeOffset.Parse("2026-07-24T02:00:00Z")),
            },
            new[]
            {
                new AccountActivationInterval(
                    DateTimeOffset.Parse("2026-07-24T03:00:00Z"),
                    DateTimeOffset.Parse("2026-07-24T04:00:00Z")),
                new AccountActivationInterval(
                    DateTimeOffset.Parse("2026-07-24T02:00:00Z"),
                    DateTimeOffset.Parse("2026-07-24T03:00:00Z")),
            },
        };

        foreach (var activations in invalidActivationLists)
        {
            var invalid = StateWithAccount("account-a", new(activations, []));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.SaveAsync(invalid, default));
        }
    }

    [Fact]
    public async Task Save_rejects_invalid_observation_values()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaEstimateLedgerService(
            Path.Combine(directory.Path, "quota-estimate-ledger.json"));
        var valid = CreateObservation();
        var invalidObservations = new[]
        {
            valid with { Segment = valid.Segment with { Period = (QuotaPeriod)99 } },
            valid with { Segment = valid.Segment with { ResetsAt = SegmentStart } },
            valid with { Segment = valid.Segment with { SegmentStart = SegmentStart.ToOffset(TimeSpan.FromHours(1)) } },
            valid with { ObservedAt = valid.ObservedAt.ToOffset(TimeSpan.FromHours(1)) },
            valid with { UsedPercent = double.NaN },
            valid with { UsedPercent = 101 },
            valid with { PercentResolution = 0 },
            valid with { PercentResolution = double.PositiveInfinity },
            valid with { AttributedCredits = -1 },
            valid with { LowerUsd = -1 },
            valid with { LowerUsd = 1, UpperUsd = null },
            valid with { LowerUsd = 2, UpperUsd = 1 },
            valid with { Source = (QuotaEstimateSource)99 },
            valid with { Kind = (QuotaObservationKind)99 },
        };

        foreach (var observation in invalidObservations)
        {
            var invalid = StateWithAccount(
                "account-a",
                new AccountQuotaEstimateLedger([], [observation]));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.SaveAsync(invalid, default));
        }
    }

    [Fact]
    public async Task Save_rejects_unknown_period()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaEstimateLedgerService(
            Path.Combine(directory.Path, "quota-estimate-ledger.json"));
        var unknown = CreateObservation() with
        {
            Segment = CreateObservation().Segment with { Period = QuotaPeriod.Unknown },
        };
        var invalid = StateWithAccount(
            "account-a",
            new AccountQuotaEstimateLedger([], [unknown]));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveAsync(invalid, default));
    }

    [Fact]
    public async Task Load_rejects_unknown_period_preserves_file_and_blocks_overwrite()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quota-estimate-ledger.json");
        var unknown = CreateObservation() with
        {
            Segment = CreateObservation().Segment with { Period = QuotaPeriod.Unknown },
        };
        var invalid = StateWithAccount(
            "account-a",
            new AccountQuotaEstimateLedger([], [unknown]));
        await WriteDocumentAsync(path, invalid);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var service = new QuotaEstimateLedgerService(path);

        var loaded = await service.LoadAsync(default);

        Assert.NotNull(loaded.Error);
        Assert.Empty(loaded.State.Accounts);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(QuotaEstimateLedgerState.Empty, default));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Save_rejects_observations_out_of_order()
    {
        using var directory = new TemporaryDirectory();
        var service = new QuotaEstimateLedgerService(
            Path.Combine(directory.Path, "quota-estimate-ledger.json"));
        var later = CreateObservation();
        var earlier = later with { ObservedAt = later.ObservedAt.AddMinutes(-1) };
        var invalid = StateWithAccount(
            "account-a",
            new AccountQuotaEstimateLedger([], [later, earlier]));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveAsync(invalid, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_rejects_overlapping_activation_intervals(bool acrossAccounts)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quota-estimate-ledger.json");
        var first = new AccountQuotaEstimateLedger(
            [new AccountActivationInterval(
                DateTimeOffset.Parse("2026-07-24T01:00:00Z"),
                DateTimeOffset.Parse("2026-07-24T03:00:00Z"))],
            []);
        var secondKey = acrossAccounts ? "account-b" : "account-a";
        var accounts = new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal)
        {
            ["account-a"] = first,
        };
        if (acrossAccounts)
        {
            accounts[secondKey] = new AccountQuotaEstimateLedger(
                [new AccountActivationInterval(
                    DateTimeOffset.Parse("2026-07-24T02:00:00Z"),
                    DateTimeOffset.Parse("2026-07-24T04:00:00Z"))],
                []);
        }
        else
        {
            accounts[secondKey] = new AccountQuotaEstimateLedger(
                [
                    first.Activations[0],
                    new AccountActivationInterval(
                        DateTimeOffset.Parse("2026-07-24T02:00:00Z"),
                        DateTimeOffset.Parse("2026-07-24T04:00:00Z")),
                ],
                []);
        }
        await WriteDocumentAsync(path, new QuotaEstimateLedgerState(accounts));
        var service = new QuotaEstimateLedgerService(path);

        var loaded = await service.LoadAsync(default);

        Assert.NotNull(loaded.Error);
        Assert.Empty(loaded.State.Accounts);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(QuotaEstimateLedgerState.Empty, default));
    }

    [Fact]
    public void First_registry_observation_uses_valid_activation_timestamp()
    {
        var registry = CreateRegistry(
            "account-a",
            DateTimeOffset.Parse("2026-07-24T04:00:00Z"));

        var result = QuotaEstimateLedgerService.ObserveRegistry(
            QuotaEstimateLedgerState.Empty,
            registry,
            DateTimeOffset.Parse("2026-07-24T05:00:00Z"));

        var activation = Assert.Single(result.Accounts["account-a"].Activations);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T04:00:00Z"), activation.StartedAt);
        Assert.Null(activation.EndedAt);
    }

    [Fact]
    public void Registry_switch_closes_previous_interval_and_opens_new_account()
    {
        var afterFirst = QuotaEstimateLedgerService.ObserveRegistry(
            QuotaEstimateLedgerState.Empty,
            CreateRegistry("account-a", DateTimeOffset.Parse("2026-07-24T04:00:00Z")),
            DateTimeOffset.Parse("2026-07-24T05:00:00Z"));

        var result = QuotaEstimateLedgerService.ObserveRegistry(
            afterFirst,
            CreateRegistry("account-b", DateTimeOffset.Parse("2026-07-24T06:00:00Z")),
            DateTimeOffset.Parse("2026-07-24T06:01:00Z"));

        var first = Assert.Single(result.Accounts["account-a"].Activations);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T04:00:00Z"), first.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T06:00:00Z"), first.EndedAt);
        var second = Assert.Single(result.Accounts["account-b"].Activations);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T06:00:00Z"), second.StartedAt);
        Assert.Null(second.EndedAt);
    }

    [Fact]
    public void Repeated_registry_observation_does_not_duplicate_open_interval()
    {
        var registry = CreateRegistry(
            "account-a",
            DateTimeOffset.Parse("2026-07-24T04:00:00Z"));
        var first = QuotaEstimateLedgerService.ObserveRegistry(
            QuotaEstimateLedgerState.Empty,
            registry,
            DateTimeOffset.Parse("2026-07-24T05:00:00Z"));

        var result = QuotaEstimateLedgerService.ObserveRegistry(
            first,
            registry,
            DateTimeOffset.Parse("2026-07-24T05:30:00Z"));

        var activation = Assert.Single(result.Accounts["account-a"].Activations);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T04:00:00Z"), activation.StartedAt);
        Assert.Null(activation.EndedAt);
    }

    [Fact]
    public void Repeated_registry_observation_without_activation_marker_is_idempotent()
    {
        var registry = CreateRegistry("account-a", activatedAt: null);
        var first = QuotaEstimateLedgerService.ObserveRegistry(
            QuotaEstimateLedgerState.Empty,
            registry,
            DateTimeOffset.Parse("2026-07-24T05:00:00Z"));

        var result = QuotaEstimateLedgerService.ObserveRegistry(
            first,
            registry,
            DateTimeOffset.Parse("2026-07-24T05:30:00Z"));

        var activation = Assert.Single(result.Accounts["account-a"].Activations);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T05:00:00Z"), activation.StartedAt);
        Assert.Null(activation.EndedAt);
    }

    [Fact]
    public void Registry_observation_without_an_active_account_closes_the_open_interval()
    {
        var first = QuotaEstimateLedgerService.ObserveRegistry(
            QuotaEstimateLedgerState.Empty,
            CreateRegistry("account-a", DateTimeOffset.Parse("2026-07-24T04:00:00Z")),
            DateTimeOffset.Parse("2026-07-24T05:00:00Z"));
        var noActive = new AccountRegistry(
            3,
            null,
            CreateRegistry("account-a", null).Accounts);
        var observedAt = DateTimeOffset.Parse("2026-07-24T05:30:00Z");

        var result = QuotaEstimateLedgerService.ObserveRegistry(
            first,
            noActive,
            observedAt);

        var activation = Assert.Single(result.Accounts["account-a"].Activations);
        Assert.Equal(observedAt, activation.EndedAt);
    }

    [Fact]
    public void Registry_observation_preserves_incremental_file_checkpoints()
    {
        var state = CreateState();
        var noActive = new AccountRegistry(
            3,
            null,
            CreateRegistry("account-a", null).Accounts);

        var result = QuotaEstimateLedgerService.ObserveRegistry(
            state,
            noActive,
            DateTimeOffset.Parse("2026-07-24T05:30:00Z"));

        Assert.Same(
            state.FileCheckpoints["2026/07/session.jsonl"],
            result.FileCheckpoints["2026/07/session.jsonl"]);
    }

    [Fact]
    public void Newer_same_account_activation_marker_closes_and_reopens_the_interval()
    {
        var first = QuotaEstimateLedgerService.ObserveRegistry(
            QuotaEstimateLedgerState.Empty,
            CreateRegistry("account-a", DateTimeOffset.Parse("2026-07-24T04:00:00Z")),
            DateTimeOffset.Parse("2026-07-24T05:00:00Z"));
        var reactivatedAt = DateTimeOffset.Parse("2026-07-24T05:30:00Z");

        var result = QuotaEstimateLedgerService.ObserveRegistry(
            first,
            CreateRegistry("account-a", reactivatedAt),
            reactivatedAt.AddMinutes(1));

        Assert.Collection(
            result.Accounts["account-a"].Activations,
            activation =>
            {
                Assert.Equal(DateTimeOffset.Parse("2026-07-24T04:00:00Z"), activation.StartedAt);
                Assert.Equal(reactivatedAt, activation.EndedAt);
            },
            activation =>
            {
                Assert.Equal(reactivatedAt, activation.StartedAt);
                Assert.Null(activation.EndedAt);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_future_activation_timestamp_uses_observation_time(bool futureTimestamp)
    {
        var observedAt = DateTimeOffset.Parse("2026-07-24T05:00:00Z");
        DateTimeOffset? activationTimestamp =
            futureTimestamp ? observedAt.AddMinutes(1) : null;
        var registry = CreateRegistry("account-a", activationTimestamp);

        var result = QuotaEstimateLedgerService.ObserveRegistry(
            QuotaEstimateLedgerState.Empty,
            registry,
            observedAt);

        var activation = Assert.Single(result.Accounts["account-a"].Activations);
        Assert.Equal(observedAt, activation.StartedAt);
        Assert.Null(activation.EndedAt);
    }

    private static QuotaEstimateLedgerState CreateState() =>
        StateWithAccount(
            "account-a",
            new AccountQuotaEstimateLedger(
                [
                    new AccountActivationInterval(
                        DateTimeOffset.Parse("2026-07-20T01:00:00Z"),
                        DateTimeOffset.Parse("2026-07-20T02:00:00Z")),
                    new AccountActivationInterval(
                        DateTimeOffset.Parse("2026-07-20T03:00:00Z"),
                        null),
                ],
                [CreateObservation()])) with
        {
            FileCheckpoints = new Dictionary<string, LocalUsageFileCheckpoint>(
                StringComparer.Ordinal)
            {
                ["2026/07/session.jsonl"] = CreateCheckpoint(),
            },
        };

    private static LocalUsageFileCheckpoint CreateCheckpoint() => new(
        RelativePath: "2026/07/session.jsonl",
        CompletedLineByteOffset: 123,
        LastKnownLength: 123,
        CreationTimeUtc: DateTimeOffset.Parse("2026-07-24T04:00:00Z"),
        LastWriteTimeUtc: DateTimeOffset.Parse("2026-07-24T05:00:00Z"),
        PrefixLength: 64,
        PrefixSha256: new string('a', 64),
        CompletedTailLength: 64,
        CompletedTailSha256: new string('b', 64),
        Model: "gpt-5.4",
        ServiceTier: "priority",
        Aggregates:
        [
            new LocalUsageAggregate(
                DateTimeOffset.Parse("2026-07-24T04:30:00Z"),
                Credits: 1.25m,
                CreditPricingFailureReason.None),
        ],
        InvalidLineCount: 0,
        RateCardVersion: CodexCreditRateCard.Version);

    private static QuotaUsageObservation CreateObservation() => new(
        new QuotaSegment(QuotaPeriod.Weekly, SegmentStart, Reset),
        DateTimeOffset.Parse("2026-07-24T04:00:00Z"),
        31.25,
        0.5,
        125m,
        true,
        4m,
        6m,
        QuotaEstimateSource.Local,
        QuotaObservationKind.FullSegment);

    private static AccountQuotaEstimateLedger EmptyAccountLedger() => new([], []);

    private static QuotaEstimateLedgerState StateWithAccount(
        string accountKey,
        AccountQuotaEstimateLedger ledger) =>
        new(new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal)
        {
            [accountKey] = ledger,
        });

    private static AccountRegistry CreateRegistry(
        string activeAccountKey,
        DateTimeOffset? activatedAt)
    {
        var accounts = new[]
        {
            new AccountRecord(
                "account-a",
                "chatgpt-a",
                "user-a",
                "first@example.com",
                string.Empty,
                null,
                "plus",
                "chatgpt"),
            new AccountRecord(
                "account-b",
                "chatgpt-b",
                "user-b",
                "second@example.com",
                string.Empty,
                null,
                "plus",
                "chatgpt"),
        };
        return new AccountRegistry(3, activeAccountKey, accounts)
        {
            ActiveAccountActivatedAt = activatedAt,
        };
    }

    private static async Task WriteDocumentAsync(string path, QuotaEstimateLedgerState state)
    {
        var document = new
        {
            SchemaVersion = 1,
            Accounts = state.Accounts,
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }
}
