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

        await service.CompleteRefreshAsync(context, default);

        Assert.Equal(1, saveCount);
        Assert.Same(context.Ledger, saved);
        Assert.Single(saved!.Accounts[Account.AccountKey].Observations);
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
        string model = "gpt-5.6-sol") =>
        new(
            timestamp,
            model,
            "default",
            InputTokens: 800_000,
            CachedInputTokens: 0,
            OutputTokens: 0);

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
            QuotaObservationKind.FullSegment);

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
}
