using System.IO;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public enum AnalyticsAvailability
{
    Available,
    Failed,
}

public sealed record HybridQuotaRefreshContext(
    LocalUsageCollectionResult LocalUsage,
    QuotaEstimateLedgerState Ledger)
{
    public QuotaEstimateLedgerState Ledger { get; internal set; } = Ledger;

    internal bool HasChanges { get; set; }
}

public sealed class HybridQuotaEstimateService
{
    private const double PercentResolution = 1d;
    private const string LedgerSaveError =
        "本地额度估算账本暂时无法保存。";

    private readonly Func<
        DateTimeOffset,
        CancellationToken,
        Task<LocalUsageCollectionResult>> _collectAsync;
    private readonly Func<
        CancellationToken,
        Task<QuotaEstimateLedgerLoadResult>> _loadAsync;
    private readonly Func<
        QuotaEstimateLedgerState,
        CancellationToken,
        Task> _saveAsync;
    private readonly CodexCreditRateCard _rateCard;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _registryLock = new(1, 1);
    private QuotaEstimateLedgerState? _registryLedger;
    private string? _registryLoadError;
    private bool _registryLoadAttempted;

    public HybridQuotaEstimateService(
        LocalCodexUsageCollector collector,
        QuotaEstimateLedgerService ledgerService,
        CodexCreditRateCard rateCard,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(ledgerService);
        _collectAsync = collector.CollectAsync;
        _loadAsync = ledgerService.LoadAsync;
        _saveAsync = ledgerService.SaveAsync;
        _rateCard = rateCard ?? throw new ArgumentNullException(nameof(rateCard));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal HybridQuotaEstimateService(
        Func<
            DateTimeOffset,
            CancellationToken,
            Task<LocalUsageCollectionResult>> collectAsync,
        Func<
            CancellationToken,
            Task<QuotaEstimateLedgerLoadResult>> loadAsync,
        Func<
            QuotaEstimateLedgerState,
            CancellationToken,
            Task> saveAsync,
        CodexCreditRateCard rateCard,
        Func<DateTimeOffset>? utcNow = null)
    {
        _collectAsync = collectAsync ?? throw new ArgumentNullException(nameof(collectAsync));
        _loadAsync = loadAsync ?? throw new ArgumentNullException(nameof(loadAsync));
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        _rateCard = rateCard ?? throw new ArgumentNullException(nameof(rateCard));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<HybridQuotaRefreshContext> BeginRefreshAsync(
        CancellationToken cancellationToken)
    {
        var now = RequireUtc(_utcNow());
        var localUsage = await _collectAsync(now.AddDays(-32), cancellationToken);
        var loaded = await _loadAsync(cancellationToken);
        return new HybridQuotaRefreshContext(localUsage, loaded.State);
    }

    public QuotaDisplay ApplyObservation(
        HybridQuotaRefreshContext context,
        AccountRecord account,
        QuotaDisplay display,
        QuotaSegment segment,
        AnalyticsUsageParseResult? analytics,
        AnalyticsAvailability analyticsAvailability)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(segment);

        var observedAt = display.ServerNow is { } serverNow
            ? RequireUtc(serverNow)
            : RequireUtc(_utcNow());
        var statuses = new List<string>();
        var source = QuotaEstimateSource.Local;
        QuotaUsageObservation observation;

        if (analyticsAvailability == AnalyticsAvailability.Available &&
            analytics?.State == AnalyticsUsageState.Valid)
        {
            source = QuotaEstimateSource.Analytics;
            var estimate = analytics.UpperCredits > 0
                ? QuotaEstimateMath.TryCreateFullInterval(
                    analytics.LowerCredits,
                    analytics.UpperCredits,
                    display.UsedPercent,
                    PercentResolution)
                : null;
            observation = CreateObservation(
                segment,
                observedAt,
                display.UsedPercent,
                analytics.UpperCredits,
                hasFullSegmentCoverage: true,
                estimate,
                source,
                QuotaObservationKind.FullSegment);
        }
        else
        {
            AddAnalyticsFallbackStatus(statuses, analytics, analyticsAvailability);
            observation = CreateLocalObservation(
                context,
                account,
                display,
                segment,
                observedAt,
                statuses);
        }

        AppendObservation(context, account.AccountKey, observation);
        var accountLedger = context.Ledger.Accounts[account.AccountKey];
        var intersection = QuotaEstimateMath.IntersectRecentCompatible(
            accountLedger.Observations
                .Where(item => item.Source == source)
                .ToArray(),
            segment);
        if (intersection?.IgnoredConflictingHistory == true)
        {
            statuses.Add("历史观测不一致，已忽略较早冲突记录");
        }

        return display with
        {
            EstimatedPeriodQuotaLowerUsd = intersection?.Estimate.LowerUsd,
            EstimatedPeriodQuotaUpperUsd = intersection?.Estimate.UpperUsd,
            EstimateSource = source,
            EstimateQuality = intersection?.Quality ?? QuotaEstimateQuality.None,
            EstimateObservationCount = intersection?.ObservationCount ?? 0,
            EstimateStatus = statuses.Count == 0
                ? null
                : string.Join("；", statuses.Distinct(StringComparer.Ordinal)),
        };
    }

    public async Task CompleteRefreshAsync(
        HybridQuotaRefreshContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HasChanges)
        {
            await _registryLock.WaitAsync(cancellationToken);
            try
            {
                await _saveAsync(context.Ledger, cancellationToken);
                _registryLedger = context.Ledger;
                _registryLoadError = null;
                _registryLoadAttempted = true;
                context.HasChanges = false;
            }
            finally
            {
                _registryLock.Release();
            }
        }
    }

    public async Task<string?> ObserveRegistryAsync(
        AccountRegistry registry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        await _registryLock.WaitAsync(cancellationToken);
        try
        {
            if (!_registryLoadAttempted)
            {
                var loaded = await _loadAsync(cancellationToken);
                _registryLedger = loaded.State;
                _registryLoadError = loaded.Error;
                _registryLoadAttempted = true;
            }

            if (_registryLoadError is not null)
            {
                return _registryLoadError;
            }

            var previous = _registryLedger!;
            var updated = QuotaEstimateLedgerService.ObserveRegistry(
                previous,
                registry,
                RequireUtc(_utcNow()));
            if (ReferenceEquals(previous, updated))
            {
                return null;
            }

            await _saveAsync(updated, cancellationToken);
            _registryLedger = updated;
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return LedgerSaveError;
        }
        catch (UnauthorizedAccessException)
        {
            return LedgerSaveError;
        }
        catch (InvalidOperationException)
        {
            return LedgerSaveError;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    private QuotaUsageObservation CreateLocalObservation(
        HybridQuotaRefreshContext context,
        AccountRecord account,
        QuotaDisplay display,
        QuotaSegment segment,
        DateTimeOffset observedAt,
        ICollection<string> statuses)
    {
        var attributedCredits = 0m;
        var pricedCount = 0;
        var unpricedCount = 0;
        if (!context.Ledger.Accounts.TryGetValue(
                account.AccountKey,
                out var existingLedger) ||
            existingLedger.Activations.Count == 0)
        {
            statuses.Add("账号历史归属不明确，将从本次刷新开始记录");
        }

        foreach (var usage in context.LocalUsage.Events)
        {
            if (usage.Timestamp < segment.SegmentStart ||
                usage.Timestamp > observedAt ||
                !IsUnambiguouslyAttributed(
                    context.Ledger,
                    account.AccountKey,
                    usage.Timestamp))
            {
                continue;
            }

            if (_rateCard.TryCalculateCredits(usage, out var credits))
            {
                attributedCredits += credits;
                pricedCount++;
            }
            else
            {
                unpricedCount++;
            }
        }

        var hasFullCoverage = HasFullSegmentCoverage(
            context.Ledger,
            account.AccountKey,
            segment.SegmentStart,
            observedAt);
        PeriodQuotaEstimate? estimate = null;
        var kind = hasFullCoverage
            ? QuotaObservationKind.FullSegment
            : QuotaObservationKind.Delta;
        if (attributedCredits > 0 && hasFullCoverage)
        {
            estimate = QuotaEstimateMath.TryCreateFullInterval(
                attributedCredits,
                attributedCredits,
                display.UsedPercent,
                PercentResolution);
        }
        else if (attributedCredits > 0)
        {
            var earlier = context.Ledger.Accounts
                .GetValueOrDefault(account.AccountKey)?
                .Observations
                .Where(item =>
                    item.Segment == segment &&
                    item.Source == QuotaEstimateSource.Local &&
                    item.ObservedAt < observedAt)
                .OrderByDescending(item => item.ObservedAt)
                .FirstOrDefault();
            if (earlier is not null)
            {
                estimate = QuotaEstimateMath.TryCreateDeltaInterval(
                    attributedCredits - earlier.AttributedCredits,
                    earlier.UsedPercent,
                    earlier.PercentResolution,
                    display.UsedPercent,
                    PercentResolution);
            }
        }

        if (pricedCount == 0)
        {
            statuses.Add(unpricedCount > 0
                ? "当前模型暂无官方费率"
                : "当前片段没有可计价的本机用量");
        }
        else if (!hasFullCoverage && estimate is null)
        {
            statuses.Add("已建立估算基线，继续使用后再次刷新");
        }

        if (pricedCount > 0 && unpricedCount > 0)
        {
            statuses.Add("部分用量无法计价，区间可能偏低");
        }

        return CreateObservation(
            segment,
            observedAt,
            display.UsedPercent,
            attributedCredits,
            hasFullCoverage,
            estimate,
            QuotaEstimateSource.Local,
            kind);
    }

    private static QuotaUsageObservation CreateObservation(
        QuotaSegment segment,
        DateTimeOffset observedAt,
        double usedPercent,
        decimal attributedCredits,
        bool hasFullSegmentCoverage,
        PeriodQuotaEstimate? estimate,
        QuotaEstimateSource source,
        QuotaObservationKind kind) =>
        new(
            segment,
            observedAt,
            usedPercent,
            PercentResolution,
            attributedCredits,
            hasFullSegmentCoverage,
            estimate?.LowerUsd,
            estimate?.UpperUsd,
            source,
            kind);

    private static void AppendObservation(
        HybridQuotaRefreshContext context,
        string accountKey,
        QuotaUsageObservation observation)
    {
        var accounts = context.Ledger.Accounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        accounts.TryGetValue(accountKey, out var ledger);
        ledger ??= new AccountQuotaEstimateLedger([], []);
        accounts[accountKey] = ledger with
        {
            Observations = ledger.Observations
                .Append(observation)
                .OrderBy(item => item.ObservedAt)
                .ToArray(),
        };
        context.Ledger = new QuotaEstimateLedgerState(accounts);
        context.HasChanges = true;
    }

    private static bool IsUnambiguouslyAttributed(
        QuotaEstimateLedgerState ledger,
        string accountKey,
        DateTimeOffset timestamp)
    {
        var matches = ledger.Accounts
            .SelectMany(pair => pair.Value.Activations.Select(
                activation => (pair.Key, Activation: activation)))
            .Where(item =>
                item.Activation.StartedAt <= timestamp &&
                (item.Activation.EndedAt is null ||
                 timestamp < item.Activation.EndedAt.Value))
            .Take(2)
            .ToArray();
        return matches.Length == 1 &&
            string.Equals(matches[0].Key, accountKey, StringComparison.Ordinal);
    }

    private static bool HasFullSegmentCoverage(
        QuotaEstimateLedgerState ledger,
        string accountKey,
        DateTimeOffset segmentStart,
        DateTimeOffset observedAt)
    {
        var matchesAtStart = ledger.Accounts
            .SelectMany(pair => pair.Value.Activations.Select(
                activation => (pair.Key, Activation: activation)))
            .Where(item =>
                item.Activation.StartedAt <= segmentStart &&
                (item.Activation.EndedAt is null ||
                 segmentStart < item.Activation.EndedAt.Value))
            .Take(2)
            .ToArray();
        if (matchesAtStart.Length != 1 ||
            !string.Equals(matchesAtStart[0].Key, accountKey, StringComparison.Ordinal))
        {
            return false;
        }

        var covering = matchesAtStart[0].Activation;
        if (covering.EndedAt is { } endedAt && endedAt < observedAt)
        {
            return false;
        }

        return !ledger.Accounts
            .SelectMany(pair => pair.Value.Activations.Select(
                activation => (pair.Key, Activation: activation)))
            .Any(item =>
                !ReferenceEquals(item.Activation, covering) &&
                item.Activation.StartedAt < observedAt &&
                (item.Activation.EndedAt is null ||
                 item.Activation.EndedAt.Value > segmentStart));
    }

    private static void AddAnalyticsFallbackStatus(
        ICollection<string> statuses,
        AnalyticsUsageParseResult? analytics,
        AnalyticsAvailability availability)
    {
        if (availability == AnalyticsAvailability.Failed)
        {
            statuses.Add("Analytics 请求失败，已改用本机用量估算");
        }
        else if (analytics?.State == AnalyticsUsageState.Empty)
        {
            statuses.Add("Analytics 无数据，已改用本机用量估算");
        }
        else
        {
            statuses.Add("Analytics 数据无效，已改用本机用量估算");
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Time must be a valid UTC timestamp.");
        }

        return value;
    }
}
