using System.Collections;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class HybridQuotaEstimateServiceTests
{
    private static readonly DateTimeOffset SegmentStart =
        DateTimeOffset.Parse("2026-07-20T00:00:00Z");
    private static readonly DateTimeOffset Reset =
        DateTimeOffset.Parse("2026-07-27T00:00:00Z");
    private static readonly DateTimeOffset ServerNow =
        DateTimeOffset.Parse("2026-07-24T12:00:00Z");
    private static readonly QuotaSegment Segment =
        new(QuotaPeriod.Weekly, SegmentStart, Reset);
    private static readonly AccountRecord Account =
        Accounts.Record("account-a", "first@example.com");

    [Fact]
    public void Refresh_context_preserves_required_positional_record_shape()
    {
        var usage = UsageResult();
        var ledger = QuotaEstimateLedgerState.Empty;
        var context = new HybridQuotaRefreshContext(
            LocalUsage: usage,
            Ledger: ledger);

        var (actualUsage, actualLedger) = context;

        Assert.Same(usage, actualUsage);
        Assert.Same(ledger, actualLedger);
    }

    [Fact]
    public async Task Begin_refresh_scans_and_loads_once_with_32_day_lookback()
    {
        var collectCount = 0;
        var loadCount = 0;
        DateTimeOffset? earliest = null;
        var now = DateTimeOffset.Parse("2026-07-25T09:30:00Z");
        var service = CreateService(
            collectAsync: (value, _) =>
            {
                collectCount++;
                earliest = value;
                return Task.FromResult(UsageResult());
            },
            loadAsync: _ =>
            {
                loadCount++;
                return Task.FromResult(new QuotaEstimateLedgerLoadResult(
                    QuotaEstimateLedgerState.Empty,
                    null));
            },
            utcNow: () => now);

        var context = await service.BeginRefreshAsync(default);

        Assert.Equal(1, collectCount);
        Assert.Equal(1, loadCount);
        Assert.Equal(now.AddDays(-32), earliest);
        Assert.Empty(context.LocalUsage.Events);
        Assert.Empty(context.Ledger.Accounts);
    }

    [Fact]
    public async Task Begin_refresh_passes_persisted_checkpoints_and_saves_updated_scan_state()
    {
        var initialCheckpoint = Checkpoint("session.jsonl", completedOffset: 10);
        var updatedCheckpoint = initialCheckpoint with
        {
            CompletedLineByteOffset = 20,
            LastKnownLength = 20,
        };
        var initial = QuotaEstimateLedgerState.Empty with
        {
            FileCheckpoints = new Dictionary<string, LocalUsageFileCheckpoint>(
                StringComparer.Ordinal)
            {
                [initialCheckpoint.RelativePath] = initialCheckpoint,
            },
        };
        IReadOnlyDictionary<string, LocalUsageFileCheckpoint>? received = null;
        QuotaEstimateLedgerState? saved = null;
        var service = new HybridQuotaEstimateService(
            (_, checkpoints, _) =>
            {
                received = checkpoints;
                return Task.FromResult(UsageResult() with
                {
                    FileCheckpoints = new Dictionary<string, LocalUsageFileCheckpoint>(
                        StringComparer.Ordinal)
                    {
                        [updatedCheckpoint.RelativePath] = updatedCheckpoint,
                    },
                    HasCheckpointChanges = true,
                });
            },
            _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(initial, null)),
            (state, _) =>
            {
                saved = state;
                return Task.CompletedTask;
            },
            new CodexCreditRateCard());

        var context = await service.BeginRefreshAsync(default);
        var warning = await service.CompleteRefreshAsync(context, default);

        Assert.Same(initial.FileCheckpoints, received);
        Assert.Null(warning);
        Assert.Equal(20, saved!.FileCheckpoints["session.jsonl"].CompletedLineByteOffset);
    }

    [Fact]
    public void Applying_observation_preserves_incremental_file_checkpoints()
    {
        var checkpoint = Checkpoint("session.jsonl", completedOffset: 20);
        var ledger = StateWithAccount(
            new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)) with
        {
            FileCheckpoints = new Dictionary<string, LocalUsageFileCheckpoint>(
                StringComparer.Ordinal)
            {
                [checkpoint.RelativePath] = checkpoint,
            },
        };
        var context = new HybridQuotaRefreshContext(
            UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger);
        var service = CreateService();

        service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Same(
            checkpoint,
            context.Ledger.FileCheckpoints[checkpoint.RelativePath]);
    }

    [Fact]
    public async Task Local_events_require_one_unambiguous_account_activation_and_server_cutoff()
    {
        var localWallClock = ServerNow.AddDays(1);
        var state = State(
            (
                Account.AccountKey,
                new AccountQuotaEstimateLedger(
                    [
                        new AccountActivationInterval(
                            SegmentStart.AddHours(-1),
                            SegmentStart.AddHours(2)),
                        new AccountActivationInterval(
                            SegmentStart.AddHours(3),
                            null),
                    ],
                    [])),
            (
                "account-b",
                new AccountQuotaEstimateLedger(
                    [
                        new AccountActivationInterval(
                            SegmentStart.AddHours(4),
                            SegmentStart.AddHours(5)),
                    ],
                    [])));
        var events = new[]
        {
            Usage(SegmentStart.AddMinutes(-1)),
            Usage(SegmentStart.AddHours(1)),
            Usage(SegmentStart.AddHours(2.5)),
            Usage(SegmentStart.AddHours(3.5)),
            Usage(SegmentStart.AddHours(4.5)),
            Usage(ServerNow.AddMinutes(1)),
        };
        var service = CreateService(
            usage: UsageResult(events),
            ledger: state,
            utcNow: () => localWallClock);
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(serverNow: ServerNow),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(QuotaEstimateSource.Local, result.EstimateSource);
        Assert.Equal(QuotaEstimateQuality.None, result.EstimateQuality);
        var observation = Assert.Single(context.Ledger.Accounts[Account.AccountKey].Observations);
        Assert.Equal(200m, observation.AttributedCredits);
        Assert.Equal(ServerNow, observation.ObservedAt);
        Assert.False(observation.HasFullSegmentCoverage);
    }

    [Fact]
    public async Task Activation_ending_at_server_cutoff_is_not_full_segment_coverage()
    {
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(
                    SegmentStart.AddMinutes(-1),
                    ServerNow)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(serverNow: ServerNow),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(result.EstimatedPeriodQuotaLowerUsd);
        Assert.False(
            context.Ledger.Accounts[Account.AccountKey]
                .Observations[^1]
                .HasFullSegmentCoverage);
    }

    [Fact]
    public async Task Missing_server_now_declines_observation_without_using_local_clock()
    {
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)),
            utcNow: () => ServerNow.AddDays(1));
        var context = await service.BeginRefreshAsync(default);
        var display = Display() with { ServerNow = null };

        var result = service.ApplyObservation(
            context,
            Account,
            display,
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(result.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(QuotaEstimateSource.None, result.EstimateSource);
        Assert.Contains("缺少服务器时间", result.EstimateStatus, StringComparison.Ordinal);
        Assert.Empty(context.Ledger.Accounts[Account.AccountKey].Observations);
    }

    [Fact]
    public async Task Full_segment_activation_produces_initial_local_estimate()
    {
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(15.69m, result.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(16.33m, result.EstimatedPeriodQuotaUpperUsd);
        Assert.Equal(QuotaEstimateSource.Local, result.EstimateSource);
        Assert.Equal(QuotaEstimateQuality.Initial, result.EstimateQuality);
        Assert.Equal(1, result.EstimateObservationCount);
        Assert.Contains("Analytics 无数据", result.EstimateStatus, StringComparison.Ordinal);
        var observation = Assert.Single(context.Ledger.Accounts[Account.AccountKey].Observations);
        Assert.True(observation.HasFullSegmentCoverage);
        Assert.Equal(QuotaObservationKind.FullSegment, observation.Kind);
        Assert.True(observation.IsLocalScanComplete);
        Assert.Equal(CodexCreditRateCard.Version, observation.RateCardVersion);
    }

    [Fact]
    public async Task Partial_local_scan_never_produces_a_bounded_full_segment_estimate()
    {
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))) with
            {
                SkippedFileCount = 2,
                InvalidLineCount = 3,
            },
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(result.EstimatedPeriodQuotaLowerUsd);
        Assert.Contains("本机用量扫描不完整", result.EstimateStatus, StringComparison.Ordinal);
        Assert.Contains("跳过 2 个文件", result.EstimateStatus, StringComparison.Ordinal);
        Assert.Contains("忽略 3 行", result.EstimateStatus, StringComparison.Ordinal);
        var observation = Assert.Single(context.Ledger.Accounts[Account.AccountKey].Observations);
        Assert.False(observation.HasFullSegmentCoverage);
        Assert.False(observation.IsLocalScanComplete);
        Assert.Equal(2, observation.SkippedFileCount);
        Assert.Equal(3, observation.MalformedLineCount);
    }

    [Fact]
    public async Task Partial_current_scan_reuses_older_bounded_local_estimate()
    {
        var activationStart = SegmentStart.AddMinutes(-1);
        var older = Observation(
            Segment,
            SegmentStart.AddHours(2),
            lowerUsd: 15m,
            upperUsd: 17m) with
        {
            ActivationStartedAt = activationStart,
        };
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(3))) with
            {
                SkippedFileCount = 1,
            },
            ledger: StateWithAccount(
                new AccountActivationInterval(activationStart, null),
                older));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(serverNow: SegmentStart.AddHours(4)),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(15m, result.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(17m, result.EstimatedPeriodQuotaUpperUsd);
        Assert.Contains("本机用量扫描不完整", result.EstimateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleted_malformed_only_checkpoint_keeps_historical_local_range()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "malformed-only.jsonl");
        directory.Write(
            "malformed-only.jsonl",
            """{"timestamp":""" + Environment.NewLine);
        var collector = new LocalCodexUsageCollector(
            directory.Path,
            utcNow: () => ServerNow);
        var first = await collector.CollectAsync(
            SegmentStart,
            CancellationToken.None);
        File.Delete(path);
        var second = await collector.CollectAsync(
            SegmentStart,
            first.FileCheckpoints,
            CancellationToken.None);
        var activationStart = SegmentStart.AddMinutes(-1);
        var historical = Observation(
            Segment,
            SegmentStart.AddHours(2),
            lowerUsd: 15m,
            upperUsd: 17m) with
        {
            ActivationStartedAt = activationStart,
        };
        var service = CreateService(
            usage: second,
            ledger: StateWithAccount(
                new AccountActivationInterval(activationStart, null),
                historical));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.False(second.IsComplete);
        Assert.Equal(15m, result.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(17m, result.EstimatedPeriodQuotaUpperUsd);
        Assert.Contains("本机用量扫描不完整", result.EstimateStatus);
    }

    [Fact]
    public async Task Reset_boundary_inside_hourly_bucket_keeps_credit_interval_uncertainty()
    {
        var segment = new QuotaSegment(
            QuotaPeriod.Weekly,
            DateTimeOffset.Parse("2026-07-24T10:30:00Z"),
            DateTimeOffset.Parse("2026-07-31T10:30:00Z"));
        var usage = UsageResult() with
        {
            Buckets =
            [
                Bucket(
                    firstEventAt: DateTimeOffset.Parse("2026-07-24T10:10:00Z"),
                    lastEventAt: DateTimeOffset.Parse("2026-07-24T10:50:00Z"),
                    pricedCredits: 100m),
            ],
        };
        var service = CreateService(
            usage,
            StateWithAccount(new AccountActivationInterval(
                segment.SegmentStart.AddHours(-1),
                null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(
                resetsAt: segment.ResetsAt,
                serverNow: segment.SegmentStart.AddHours(1)),
            segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(0m, result.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(16.33m, result.EstimatedPeriodQuotaUpperUsd);
        var observation = Assert.Single(
            context.Ledger.Accounts[Account.AccountKey].Observations);
        Assert.Equal(0m, observation.AttributedCredits);
        Assert.Equal(100m, observation.AttributedCreditsUpper);
        Assert.True(observation.HasAttributionBoundaryUncertainty);
        Assert.Contains("Credits 归属按区间保守处理", result.EstimateStatus);
    }

    [Fact]
    public async Task Activation_boundary_inside_hourly_bucket_never_claims_exact_credits()
    {
        var activationStart = SegmentStart.AddHours(2).AddMinutes(30);
        var usage = UsageResult() with
        {
            Buckets =
            [
                Bucket(
                    firstEventAt: SegmentStart.AddHours(2).AddMinutes(10),
                    lastEventAt: SegmentStart.AddHours(2).AddMinutes(50),
                    pricedCredits: 100m),
            ],
        };
        var service = CreateService(
            usage,
            StateWithAccount(new AccountActivationInterval(activationStart, null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(serverNow: SegmentStart.AddHours(3)),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(result.EstimatedPeriodQuotaLowerUsd);
        var observation = Assert.Single(
            context.Ledger.Accounts[Account.AccountKey].Observations);
        Assert.Equal(0m, observation.AttributedCredits);
        Assert.Equal(100m, observation.AttributedCreditsUpper);
        Assert.True(observation.HasAttributionBoundaryUncertainty);
    }

    [Fact]
    public async Task Compact_usage_is_indexed_once_for_a_multi_account_batch()
    {
        var secondAccount = Accounts.Record(
            "account-b",
            "second@example.com",
            accountId: "acct-2");
        var buckets = new CountingReadOnlyList<LocalUsageBucket>(
        [
            Bucket(
                SegmentStart.AddHours(1),
                SegmentStart.AddHours(1).AddMinutes(10),
                10m),
            Bucket(
                SegmentStart.AddHours(3),
                SegmentStart.AddHours(3).AddMinutes(10),
                20m),
        ]);
        var service = CreateService(
            UsageResult() with { Buckets = buckets },
            State(
                (
                    Account.AccountKey,
                    new AccountQuotaEstimateLedger(
                        [new AccountActivationInterval(
                            SegmentStart.AddMinutes(-1),
                            SegmentStart.AddHours(2))],
                        [])),
                (
                    secondAccount.AccountKey,
                    new AccountQuotaEstimateLedger(
                        [new AccountActivationInterval(SegmentStart.AddHours(2), null)],
                        []))));
        var context = await service.BeginRefreshAsync(default);

        service.ApplyObservation(
            context,
            Account,
            Display(serverNow: SegmentStart.AddHours(4)),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);
        service.ApplyObservation(
            context,
            secondAccount,
            Display(serverNow: SegmentStart.AddHours(4)),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(1, buckets.EnumerationCount);
    }

    [Fact]
    public async Task Mid_segment_activation_records_baseline_then_positive_delta_estimate()
    {
        var firstCutoff = SegmentStart.AddHours(4);
        var secondCutoff = SegmentStart.AddHours(6);
        var service = CreateService(
            usage: UsageResult(
                Usage(SegmentStart.AddHours(3)),
                Usage(SegmentStart.AddHours(5))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddHours(2), null)));
        var context = await service.BeginRefreshAsync(default);

        var baseline = service.ApplyObservation(
            context,
            Account,
            Display(usedPercent: 25, serverNow: firstCutoff),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);
        var estimate = service.ApplyObservation(
            context,
            Account,
            Display(usedPercent: 50, serverNow: secondCutoff),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(baseline.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(QuotaEstimateQuality.None, baseline.EstimateQuality);
        Assert.Contains("已建立估算基线", baseline.EstimateStatus, StringComparison.Ordinal);
        Assert.Equal(15.38m, estimate.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(16.67m, estimate.EstimatedPeriodQuotaUpperUsd);
        Assert.Equal(QuotaEstimateQuality.Initial, estimate.EstimateQuality);
        Assert.Collection(
            context.Ledger.Accounts[Account.AccountKey].Observations,
            observation =>
            {
                Assert.Equal(100m, observation.AttributedCredits);
                Assert.Null(observation.LowerUsd);
                Assert.Equal(QuotaObservationKind.Delta, observation.Kind);
            },
            observation =>
            {
                Assert.Equal(200m, observation.AttributedCredits);
                Assert.Equal(QuotaObservationKind.Delta, observation.Kind);
            });
    }

    [Fact]
    public async Task Zero_percent_baseline_records_existing_credits_for_later_delta()
    {
        var firstCutoff = SegmentStart.AddHours(4);
        var secondCutoff = SegmentStart.AddHours(6);
        var service = CreateService(
            usage: UsageResult(
                Usage(SegmentStart.AddHours(3)),
                Usage(SegmentStart.AddHours(5))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddHours(2), null)));
        var context = await service.BeginRefreshAsync(default);

        var baseline = service.ApplyObservation(
            context,
            Account,
            Display(usedPercent: 0, serverNow: firstCutoff),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.NotRequested);
        var estimate = service.ApplyObservation(
            context,
            Account,
            Display(usedPercent: 25, serverNow: secondCutoff),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(baseline.EstimatedPeriodQuotaLowerUsd);
        Assert.NotNull(estimate.EstimatedPeriodQuotaLowerUsd);
        Assert.Collection(
            context.Ledger.Accounts[Account.AccountKey].Observations,
            observation => Assert.Equal(100m, observation.AttributedCredits),
            observation => Assert.Equal(200m, observation.AttributedCredits));
    }

    [Fact]
    public async Task Same_account_reactivation_does_not_delta_across_activation_gap()
    {
        var firstActivation = SegmentStart.AddHours(1);
        var secondActivation = SegmentStart.AddHours(5);
        var earlier = new QuotaUsageObservation(
            Segment,
            SegmentStart.AddHours(3),
            UsedPercent: 25,
            PercentResolution: 1,
            AttributedCredits: 100m,
            HasFullSegmentCoverage: false,
            LowerUsd: null,
            UpperUsd: null,
            QuotaEstimateSource.Local,
            QuotaObservationKind.Delta)
        {
            RateCardVersion = CodexCreditRateCard.Version,
            ActivationStartedAt = firstActivation,
        };
        var service = CreateService(
            usage: UsageResult(
                Usage(SegmentStart.AddHours(2)),
                Usage(SegmentStart.AddHours(5.5))),
            ledger: State((
                Account.AccountKey,
                new AccountQuotaEstimateLedger(
                    [
                        new AccountActivationInterval(
                            firstActivation,
                            SegmentStart.AddHours(4)),
                        new AccountActivationInterval(secondActivation, null),
                    ],
                    [earlier]))));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(usedPercent: 50, serverNow: SegmentStart.AddHours(6)),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(result.EstimatedPeriodQuotaLowerUsd);
        Assert.Contains("已建立估算基线", result.EstimateStatus, StringComparison.Ordinal);
        var current = context.Ledger.Accounts[Account.AccountKey].Observations[^1];
        Assert.Equal(200m, current.AttributedCredits);
        Assert.Equal(secondActivation, current.ActivationStartedAt);
    }

    [Fact]
    public async Task Reactivation_reuses_compatible_estimate_from_old_activation_interval()
    {
        var firstActivation = SegmentStart.AddHours(1);
        var secondActivation = SegmentStart.AddHours(5);
        var oldBounded = new QuotaUsageObservation(
            Segment,
            SegmentStart.AddHours(3),
            UsedPercent: 25,
            PercentResolution: 1,
            AttributedCredits: 100m,
            HasFullSegmentCoverage: false,
            LowerUsd: 15.5m,
            UpperUsd: 16.5m,
            QuotaEstimateSource.Local,
            QuotaObservationKind.Delta)
        {
            RateCardVersion = CodexCreditRateCard.Version,
            ActivationStartedAt = firstActivation,
        };
        var currentBaseline = oldBounded with
        {
            ObservedAt = SegmentStart.AddHours(5.5),
            AttributedCredits = 200m,
            LowerUsd = null,
            UpperUsd = null,
            ActivationStartedAt = secondActivation,
        };
        var service = CreateService(
            usage: UsageResult(
                Usage(SegmentStart.AddHours(2)),
                Usage(SegmentStart.AddHours(5.25)),
                Usage(SegmentStart.AddHours(5.75))),
            ledger: State((
                Account.AccountKey,
                new AccountQuotaEstimateLedger(
                    [
                        new AccountActivationInterval(
                            firstActivation,
                            SegmentStart.AddHours(4)),
                        new AccountActivationInterval(secondActivation, null),
                    ],
                    [oldBounded, currentBaseline]))));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(usedPercent: 50, serverNow: SegmentStart.AddHours(6)),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(15.5m, result.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(16.5m, result.EstimatedPeriodQuotaUpperUsd);
        Assert.Equal(QuotaEstimateQuality.MultiPoint, result.EstimateQuality);
        Assert.Equal(2, result.EstimateObservationCount);
    }

    [Fact]
    public async Task Changed_rate_card_version_does_not_delta_against_old_credit_total()
    {
        var activationStart = SegmentStart.AddHours(2);
        var earlier = new QuotaUsageObservation(
            Segment,
            SegmentStart.AddHours(4),
            UsedPercent: 25,
            PercentResolution: 1,
            AttributedCredits: 100m,
            HasFullSegmentCoverage: false,
            LowerUsd: null,
            UpperUsd: null,
            QuotaEstimateSource.Local,
            QuotaObservationKind.Delta)
        {
            RateCardVersion = "older-rate-card",
            ActivationStartedAt = activationStart,
        };
        var service = CreateService(
            usage: UsageResult(
                Usage(SegmentStart.AddHours(3)),
                Usage(SegmentStart.AddHours(5))),
            ledger: StateWithAccount(
                new AccountActivationInterval(activationStart, null),
                earlier));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(usedPercent: 50, serverNow: SegmentStart.AddHours(6)),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(result.EstimatedPeriodQuotaLowerUsd);
        Assert.Contains("已建立估算基线", result.EstimateStatus, StringComparison.Ordinal);
        Assert.Equal(
            CodexCreditRateCard.Version,
            context.Ledger.Accounts[Account.AccountKey].Observations[^1].RateCardVersion);
    }

    [Fact]
    public async Task Current_local_estimate_does_not_intersect_old_rate_card_bounds()
    {
        var activationStart = SegmentStart.AddHours(2);
        var oldBounded = new QuotaUsageObservation(
            Segment,
            SegmentStart.AddHours(3),
            UsedPercent: 25,
            PercentResolution: 1,
            AttributedCredits: 100m,
            HasFullSegmentCoverage: false,
            LowerUsd: 15.5m,
            UpperUsd: 16.5m,
            QuotaEstimateSource.Local,
            QuotaObservationKind.Delta)
        {
            RateCardVersion = "older-rate-card",
            ActivationStartedAt = activationStart,
        };
        var currentBaseline = oldBounded with
        {
            ObservedAt = SegmentStart.AddHours(4),
            AttributedCredits = 100m,
            LowerUsd = null,
            UpperUsd = null,
            RateCardVersion = CodexCreditRateCard.Version,
        };
        var service = CreateService(
            usage: UsageResult(
                Usage(SegmentStart.AddHours(3)),
                Usage(SegmentStart.AddHours(5))),
            ledger: StateWithAccount(
                new AccountActivationInterval(activationStart, null),
                oldBounded,
                currentBaseline));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(usedPercent: 50, serverNow: SegmentStart.AddHours(6)),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(15.38m, result.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(16.67m, result.EstimatedPeriodQuotaUpperUsd);
        Assert.Equal(QuotaEstimateQuality.Initial, result.EstimateQuality);
        Assert.Equal(1, result.EstimateObservationCount);
    }

    [Theory]
    [InlineData(QuotaPeriod.Weekly)]
    [InlineData(QuotaPeriod.Monthly)]
    public async Task New_segment_ignores_old_observations(QuotaPeriod period)
    {
        var newStart = SegmentStart.AddDays(1);
        var newReset = period == QuotaPeriod.Weekly
            ? Reset.AddDays(1)
            : newStart.AddDays(30);
        var newSegment = new QuotaSegment(period, newStart, newReset);
        var oldObservation = Observation(
            Segment,
            newStart.AddHours(-1),
            lowerUsd: 80m,
            upperUsd: 90m);
        var service = CreateService(
            usage: UsageResult(Usage(newStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(newStart.AddMinutes(-1), null),
                oldObservation));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(period, resetsAt: newReset, serverNow: newStart.AddHours(2)),
            newSegment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(QuotaEstimateQuality.Initial, result.EstimateQuality);
        Assert.Equal(1, result.EstimateObservationCount);
        Assert.Equal(2, context.Ledger.Accounts[Account.AccountKey].Observations.Count);
    }

    [Fact]
    public async Task Unknown_model_is_unpriced_and_has_no_estimate()
    {
        var service = CreateService(
            usage: UsageResult(Usage(
                SegmentStart.AddHours(1),
                model: "unknown-model")),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(result.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(QuotaEstimateQuality.None, result.EstimateQuality);
        Assert.Contains("当前模型暂无官方费率", result.EstimateStatus, StringComparison.Ordinal);
        var observation = Assert.Single(context.Ledger.Accounts[Account.AccountKey].Observations);
        Assert.Equal(0m, observation.AttributedCredits);
    }

    [Fact]
    public async Task Unknown_service_tier_has_a_specific_sanitized_status()
    {
        var service = CreateService(
            usage: UsageResult(Usage(
                SegmentStart.AddHours(1),
                serviceTier: string.Empty)),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Contains(
            "速度模式未知，部分用量无法计价",
            result.EstimateStatus,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "当前模型暂无官方费率",
            result.EstimateStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_activation_history_does_not_guess_event_ownership()
    {
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: QuotaEstimateLedgerState.Empty);
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Null(result.EstimatedPeriodQuotaLowerUsd);
        Assert.Contains(
            "账号历史归属不明确，将从本次刷新开始记录",
            result.EstimateStatus,
            StringComparison.Ordinal);
        var observation = Assert.Single(context.Ledger.Accounts[Account.AccountKey].Observations);
        Assert.Equal(0m, observation.AttributedCredits);
    }

    [Fact]
    public async Task Mixed_priced_and_unpriced_events_estimate_with_low_bias_status()
    {
        var service = CreateService(
            usage: UsageResult(
                Usage(SegmentStart.AddHours(1)),
                Usage(SegmentStart.AddHours(2), model: "unknown-model")),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.NotNull(result.EstimatedPeriodQuotaLowerUsd);
        Assert.Contains("部分用量无法计价，区间可能偏低", result.EstimateStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("enterprise", true)]
    [InlineData("Business", false)]
    [InlineData("team", false)]
    [InlineData("plus", false)]
    public async Task Enterprise_only_discloses_legacy_rate_card_eligibility_gap(
        string plan,
        bool expected)
    {
        var account = Account with { Plan = plan };
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: State((
                account.AccountKey,
                new AccountQuotaEstimateLedger(
                    [new AccountActivationInterval(
                        SegmentStart.AddMinutes(-1),
                        null)],
                    []))));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(
            expected,
            result.EstimateStatus?.Contains(
                "旧版 token 费率资格",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Compatible_history_produces_multi_point_intersection()
    {
        var existing = Observation(
            Segment,
            ServerNow.AddHours(-1),
            lowerUsd: 15m,
            upperUsd: 17m);
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null),
                existing));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(QuotaEstimateQuality.MultiPoint, result.EstimateQuality);
        Assert.Equal(2, result.EstimateObservationCount);
        Assert.Equal(15.69m, result.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(16.33m, result.EstimatedPeriodQuotaUpperUsd);
    }

    [Fact]
    public async Task Conflicting_old_history_is_ignored_and_flagged()
    {
        var conflictingOld = Observation(
            Segment,
            ServerNow.AddHours(-2),
            lowerUsd: 30m,
            upperUsd: 31m);
        var compatibleNew = Observation(
            Segment,
            ServerNow.AddHours(-1),
            lowerUsd: 15m,
            upperUsd: 17m);
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null),
                conflictingOld,
                compatibleNew));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(QuotaEstimateQuality.MultiPoint, result.EstimateQuality);
        Assert.Equal(2, result.EstimateObservationCount);
        Assert.Contains("历史观测不一致", result.EstimateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_analytics_is_preferred_over_local_usage()
    {
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            new AnalyticsUsageParseResult(AnalyticsUsageState.Valid, 50m, 50m),
            AnalyticsAvailability.Available);

        Assert.Equal(QuotaEstimateSource.Analytics, result.EstimateSource);
        Assert.Equal(7.84m, result.EstimatedPeriodQuotaLowerUsd);
        var observation = Assert.Single(context.Ledger.Accounts[Account.AccountKey].Observations);
        Assert.Equal(50m, observation.AttributedCredits);
        Assert.Equal(QuotaEstimateSource.Analytics, observation.Source);
    }

    [Fact]
    public async Task Zero_credit_analytics_uses_local_fallback()
    {
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            new AnalyticsUsageParseResult(AnalyticsUsageState.Valid, 0m, 0m),
            AnalyticsAvailability.Available);

        Assert.Equal(QuotaEstimateSource.Local, result.EstimateSource);
        Assert.Equal(15.69m, result.EstimatedPeriodQuotaLowerUsd);
        var observation = Assert.Single(context.Ledger.Accounts[Account.AccountKey].Observations);
        Assert.Equal(QuotaEstimateSource.Local, observation.Source);
    }

    [Fact]
    public async Task Invalid_analytics_uses_local_fallback_with_invalid_data_status()
    {
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            new AnalyticsUsageParseResult(AnalyticsUsageState.Invalid, 0m, 0m),
            AnalyticsAvailability.Available);

        Assert.Equal(QuotaEstimateSource.Local, result.EstimateSource);
        Assert.Equal(15.69m, result.EstimatedPeriodQuotaLowerUsd);
        Assert.Contains("Analytics 数据无效", result.EstimateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_saves_evolving_ledger_once()
    {
        var saveCount = 0;
        QuotaEstimateLedgerState? saved = null;
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)),
            saveAsync: (state, _) =>
            {
                saveCount++;
                saved = state;
                return Task.CompletedTask;
            });
        var context = await service.BeginRefreshAsync(default);
        service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        var warning = await service.CompleteRefreshAsync(context, default);

        Assert.Null(warning);
        Assert.Equal(1, saveCount);
        Assert.Same(context.Ledger, saved);
        Assert.Single(saved!.Accounts[Account.AccountKey].Observations);
    }

    [Fact]
    public async Task Failed_completion_preserves_dirty_state_and_retries_it_later()
    {
        var attempts = new List<QuotaEstimateLedgerState>();
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)),
            saveAsync: (state, _) =>
            {
                attempts.Add(state);
                return attempts.Count == 1
                    ? Task.FromException(new IOException("save failed"))
                    : Task.CompletedTask;
            });
        var first = await service.BeginRefreshAsync(default);
        service.ApplyObservation(
            first,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        var firstWarning = await service.CompleteRefreshAsync(first, default);
        var second = await service.BeginRefreshAsync(default);
        var secondWarning = await service.CompleteRefreshAsync(second, default);

        Assert.Contains("未保存", firstWarning, StringComparison.Ordinal);
        Assert.Null(secondWarning);
        Assert.Equal(2, attempts.Count);
        Assert.Single(attempts[1].Accounts[Account.AccountKey].Observations);
    }

    [Fact]
    public async Task Later_refresh_reloads_after_transient_load_error_before_dirty_retry()
    {
        const string loadWarning = "本地额度估算账本暂时无法读取，原文件已保留。";
        var loadCount = 0;
        var saveCount = 0;
        QuotaEstimateLedgerState? saved = null;
        var initial = StateWithAccount(
            new AccountActivationInterval(SegmentStart.AddMinutes(-1), null));
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            loadAsync: _ =>
            {
                loadCount++;
                return Task.FromResult(new QuotaEstimateLedgerLoadResult(
                    initial,
                    loadCount == 1 ? loadWarning : null));
            },
            saveAsync: (state, _) =>
            {
                saveCount++;
                if (loadCount < 2)
                {
                    return Task.FromException(new InvalidOperationException(
                        "load must recover before save"));
                }

                saved = state;
                return Task.CompletedTask;
            });
        var first = await service.BeginRefreshAsync(default);
        service.ApplyObservation(
            first,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        var firstWarning = await service.CompleteRefreshAsync(first, default);
        var second = await service.BeginRefreshAsync(default);
        var secondWarning = await service.CompleteRefreshAsync(second, default);

        Assert.Contains("未保存", firstWarning, StringComparison.Ordinal);
        Assert.Null(secondWarning);
        Assert.Equal(2, loadCount);
        Assert.Equal(2, saveCount);
        Assert.Single(saved!.Accounts[Account.AccountKey].Observations);
    }

    [Fact]
    public async Task Successful_reload_replays_registry_activation_recorded_during_load_error()
    {
        const string loadWarning = "本地额度估算账本暂时无法读取，原文件已保留。";
        var priorStart = SegmentStart.AddHours(-2);
        var priorEnd = SegmentStart.AddHours(-1);
        var reactivatedAt = SegmentStart.AddHours(1);
        var observedAt = SegmentStart.AddHours(2);
        var persisted = StateWithAccount(
            new AccountActivationInterval(priorStart, priorEnd));
        var loadCount = 0;
        var service = CreateService(
            loadAsync: _ =>
            {
                loadCount++;
                return Task.FromResult(loadCount == 1
                    ? new QuotaEstimateLedgerLoadResult(
                        QuotaEstimateLedgerState.Empty,
                        loadWarning)
                    : new QuotaEstimateLedgerLoadResult(persisted, null));
            },
            saveAsync: (_, _) => Task.FromException(
                new InvalidOperationException("ledger remains blocked")),
            utcNow: () => observedAt);
        var registry = new AccountRegistry(3, Account.AccountKey, [Account])
        {
            ActiveAccountActivatedAt = reactivatedAt,
        };

        var warning = await service.ObserveRegistryAsync(registry, default);
        var context = await service.BeginRefreshAsync(default);

        Assert.Contains("未保存", warning, StringComparison.Ordinal);
        Assert.Equal(2, loadCount);
        Assert.Collection(
            context.Ledger.Accounts[Account.AccountKey].Activations,
            activation =>
            {
                Assert.Equal(priorStart, activation.StartedAt);
                Assert.Equal(priorEnd, activation.EndedAt);
            },
            activation =>
            {
                Assert.Equal(reactivatedAt, activation.StartedAt);
                Assert.Null(activation.EndedAt);
            });
    }

    [Fact]
    public async Task Load_warning_is_carried_into_the_estimate_without_discarding_loaded_state()
    {
        const string loadWarning = "本地额度估算账本暂时无法读取，原文件已保留。";
        var service = CreateService(
            usage: UsageResult(Usage(SegmentStart.AddHours(1))),
            loadAsync: _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(
                StateWithAccount(
                    new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)),
                loadWarning)));
        var context = await service.BeginRefreshAsync(default);

        var result = service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            EmptyAnalytics(),
            AnalyticsAvailability.Available);

        Assert.Equal(QuotaEstimateSource.Local, result.EstimateSource);
        Assert.Contains(loadWarning, result.EstimateStatus, StringComparison.Ordinal);
        Assert.Contains("未保存", result.EstimateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observe_registry_uses_injected_utc_clock_and_persists()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-25T10:00:00Z");
        QuotaEstimateLedgerState? saved = null;
        var service = CreateService(
            utcNow: () => observedAt,
            saveAsync: (state, _) =>
            {
                saved = state;
                return Task.CompletedTask;
            });
        var registry = new AccountRegistry(3, Account.AccountKey, [Account]);

        var error = await service.ObserveRegistryAsync(registry, default);

        Assert.Null(error);
        var activation = Assert.Single(saved!.Accounts[Account.AccountKey].Activations);
        Assert.Equal(observedAt, activation.StartedAt);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("switch")]
    [InlineData("logout")]
    public async Task Non_advancing_registry_clock_returns_sanitized_warning(
        string operation)
    {
        var observedAt = DateTimeOffset.Parse("2026-07-25T10:00:00Z");
        var secondAccount = Accounts.Record(
            "account-b",
            "second@example.com",
            accountId: "acct-2");
        var ledger = operation == "login"
            ? StateWithAccount(new AccountActivationInterval(
                observedAt.AddHours(-1),
                observedAt.AddMinutes(1)))
            : StateWithAccount(new AccountActivationInterval(observedAt, null));
        var registry = operation switch
        {
            "login" => new AccountRegistry(3, Account.AccountKey, [Account]),
            "switch" => new AccountRegistry(
                3,
                secondAccount.AccountKey,
                [Account, secondAccount]),
            "logout" => new AccountRegistry(3, null, [Account]),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var saveCount = 0;
        var service = CreateService(
            ledger: ledger,
            utcNow: () => observedAt,
            saveAsync: (_, _) =>
            {
                saveCount++;
                return Task.CompletedTask;
            });

        var warning = await service.ObserveRegistryAsync(registry, default);

        Assert.Equal(
            "本地额度估算状态暂时无法更新，账号操作结果不受影响。",
            warning);
        Assert.Equal(0, saveCount);
    }

    [Fact]
    public async Task Completed_refresh_becomes_registry_observation_baseline()
    {
        var loadCount = 0;
        var saves = new List<QuotaEstimateLedgerState>();
        var service = CreateService(
            ledger: StateWithAccount(
                new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)),
            loadAsync: _ =>
            {
                loadCount++;
                return Task.FromResult(new QuotaEstimateLedgerLoadResult(
                    StateWithAccount(
                        new AccountActivationInterval(SegmentStart.AddMinutes(-1), null)),
                    null));
            },
            saveAsync: (state, _) =>
            {
                saves.Add(state);
                return Task.CompletedTask;
            },
            utcNow: () => ServerNow.AddHours(1));
        var context = await service.BeginRefreshAsync(default);
        service.ApplyObservation(
            context,
            Account,
            Display(),
            Segment,
            new AnalyticsUsageParseResult(AnalyticsUsageState.Valid, 50m, 50m),
            AnalyticsAvailability.Available);
        await service.CompleteRefreshAsync(context, default);
        var registry = new AccountRegistry(3, Account.AccountKey, [Account])
        {
            ActiveAccountActivatedAt = SegmentStart.AddMinutes(-1),
        };

        var error = await service.ObserveRegistryAsync(registry, default);

        Assert.Null(error);
        Assert.Equal(1, loadCount);
        Assert.Single(saves);
        Assert.Single(saves[^1].Accounts[Account.AccountKey].Observations);
    }

    [Fact]
    public async Task Completion_merges_observations_with_registry_switch_and_deduplicates()
    {
        var secondAccount = Accounts.Record(
            "account-b",
            "second@example.com",
            accountId: "acct-2");
        var switchAt = ServerNow.AddMinutes(30);
        var observationAt = ServerNow.AddHours(1);
        var initial = StateWithAccount(
            new AccountActivationInterval(SegmentStart.AddMinutes(-1), null));
        var saves = new List<QuotaEstimateLedgerState>();
        var service = CreateService(
            loadAsync: _ => Task.FromResult(
                new QuotaEstimateLedgerLoadResult(initial, null)),
            saveAsync: (state, _) =>
            {
                saves.Add(state);
                return Task.CompletedTask;
            },
            utcNow: () => switchAt.AddMinutes(1));
        var context = await service.BeginRefreshAsync(default);
        var registry = new AccountRegistry(
            3,
            secondAccount.AccountKey,
            [Account, secondAccount])
        {
            ActiveAccountActivatedAt = switchAt,
        };
        Assert.Null(await service.ObserveRegistryAsync(registry, default));
        var display = Display(serverNow: observationAt);
        var analytics = new AnalyticsUsageParseResult(
            AnalyticsUsageState.Valid,
            50m,
            50m);
        service.ApplyObservation(
            context,
            Account,
            display,
            Segment,
            analytics,
            AnalyticsAvailability.Available);
        service.ApplyObservation(
            context,
            Account,
            display,
            Segment,
            analytics,
            AnalyticsAvailability.Available);

        await service.CompleteRefreshAsync(context, default);

        Assert.Equal(2, saves.Count);
        var final = saves[^1];
        var firstActivation = Assert.Single(final.Accounts[Account.AccountKey].Activations);
        Assert.Equal(switchAt, firstActivation.EndedAt);
        var secondActivation = Assert.Single(
            final.Accounts[secondAccount.AccountKey].Activations);
        Assert.Equal(switchAt, secondActivation.StartedAt);
        Assert.Null(secondActivation.EndedAt);
        Assert.Single(final.Accounts[Account.AccountKey].Observations);
    }

    private static HybridQuotaEstimateService CreateService(
        LocalUsageCollectionResult? usage = null,
        QuotaEstimateLedgerState? ledger = null,
        Func<DateTimeOffset, CancellationToken, Task<LocalUsageCollectionResult>>? collectAsync = null,
        Func<CancellationToken, Task<QuotaEstimateLedgerLoadResult>>? loadAsync = null,
        Func<QuotaEstimateLedgerState, CancellationToken, Task>? saveAsync = null,
        Func<DateTimeOffset>? utcNow = null) =>
        new(
            collectAsync ?? ((_, _) => Task.FromResult(usage ?? UsageResult())),
            loadAsync ?? (_ => Task.FromResult(new QuotaEstimateLedgerLoadResult(
                ledger ?? QuotaEstimateLedgerState.Empty,
                null))),
            saveAsync ?? ((_, _) => Task.CompletedTask),
            new CodexCreditRateCard(),
            utcNow);

    private static LocalUsageCollectionResult UsageResult(
        params LocalUsageEvent[] events) =>
        new(events, InvalidLineCount: 0);

    private static LocalUsageEvent Usage(
        DateTimeOffset timestamp,
        string model = "gpt-5.6-sol",
        string serviceTier = "default") =>
        new(
            timestamp,
            model,
            serviceTier,
            InputTokens: 800_000,
            CachedInputTokens: 0,
            OutputTokens: 0);

    private static LocalUsageBucket Bucket(
        DateTimeOffset firstEventAt,
        DateTimeOffset lastEventAt,
        decimal pricedCredits)
    {
        var utc = firstEventAt.ToUniversalTime();
        return new LocalUsageBucket(
            new DateTimeOffset(
                utc.Year,
                utc.Month,
                utc.Day,
                utc.Hour,
                minute: 0,
                second: 0,
                TimeSpan.Zero),
            firstEventAt,
            lastEventAt,
            pricedCredits,
            PricedEventCount: 1,
            UnknownModelEventCount: 0,
            UnknownServiceTierEventCount: 0,
            InvalidUsageEventCount: 0);
    }

    private static AnalyticsUsageParseResult EmptyAnalytics() =>
        new(AnalyticsUsageState.Empty, LowerCredits: 0, UpperCredits: 0);

    private static QuotaDisplay Display(
        QuotaPeriod period = QuotaPeriod.Weekly,
        double usedPercent = 25,
        DateTimeOffset? resetsAt = null,
        DateTimeOffset? serverNow = null) =>
        new(
            period,
            RemainingPercent: (int)(100 - usedPercent),
            resetsAt ?? Reset,
            period == QuotaPeriod.Weekly
                ? TimeSpan.FromDays(7)
                : TimeSpan.FromDays(30),
            "server")
        {
            UsedPercent = usedPercent,
            ServerNow = serverNow ?? ServerNow,
        };

    private static QuotaUsageObservation Observation(
        QuotaSegment segment,
        DateTimeOffset observedAt,
        decimal lowerUsd,
        decimal upperUsd) =>
        new(
            segment,
            observedAt,
            UsedPercent: 25,
            PercentResolution: 1,
            AttributedCredits: 100m,
            HasFullSegmentCoverage: true,
            lowerUsd,
            upperUsd,
            QuotaEstimateSource.Local,
            QuotaObservationKind.FullSegment)
        {
            RateCardVersion = CodexCreditRateCard.Version,
            ActivationStartedAt = SegmentStart.AddMinutes(-1),
        };

    private static LocalUsageFileCheckpoint Checkpoint(
        string relativePath,
        long completedOffset) =>
        new(
            relativePath,
            completedOffset,
            completedOffset,
            DateTimeOffset.Parse("2026-07-24T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-24T01:00:00Z"),
            PrefixLength: 0,
            PrefixSha256:
                "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            CompletedTailLength: 0,
            CompletedTailSha256:
                "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            Model: "gpt-5.4",
            ServiceTier: "default",
            Aggregates: [],
            InvalidLineCount: 0,
            RateCardVersion: CodexCreditRateCard.Version);

    private static QuotaEstimateLedgerState StateWithAccount(
        AccountActivationInterval activation,
        params QuotaUsageObservation[] observations) =>
        State((
            Account.AccountKey,
            new AccountQuotaEstimateLedger([activation], observations)));

    private static QuotaEstimateLedgerState State(
        params (string Key, AccountQuotaEstimateLedger Ledger)[] accounts) =>
        new(accounts.ToDictionary(
            item => item.Key,
            item => item.Ledger,
            StringComparer.Ordinal));

    private sealed class CountingReadOnlyList<T>(
        IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int EnumerationCount { get; private set; }

        public int Count => items.Count;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
