using System.IO;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public enum AnalyticsAvailability
{
    Available,
    Failed,
    NotRequested,
}

public sealed record HybridQuotaRefreshContext(
    LocalUsageCollectionResult LocalUsage,
    QuotaEstimateLedgerState Ledger)
{
    public QuotaEstimateLedgerState Ledger { get; internal set; } = Ledger;

    internal bool HasChanges { get; set; }

    internal string? PersistenceWarning { get; set; }
}

public sealed class HybridQuotaEstimateService
{
    private const double PercentResolution = 1d;
    private const string LedgerSaveError =
        "本地额度估算账本暂时无法保存。";

    private readonly Func<
        DateTimeOffset,
        IReadOnlyDictionary<string, LocalUsageFileCheckpoint>,
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
    private readonly List<PendingRegistryObservation> _pendingRegistryObservations = [];
    private QuotaEstimateLedgerState? _registryLedger;
    private string? _registryLoadError;
    private bool _registryLoadAttempted;
    private bool _hasPendingSave;

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
        ArgumentNullException.ThrowIfNull(collectAsync);
        _collectAsync = (earliestUtc, _, cancellationToken) =>
            collectAsync(earliestUtc, cancellationToken);
        _loadAsync = loadAsync ?? throw new ArgumentNullException(nameof(loadAsync));
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        _rateCard = rateCard ?? throw new ArgumentNullException(nameof(rateCard));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal HybridQuotaEstimateService(
        Func<
            DateTimeOffset,
            IReadOnlyDictionary<string, LocalUsageFileCheckpoint>,
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
        QuotaEstimateLedgerState loadedState;
        string? loadError;
        await _registryLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureRegistryLedgerLoadedAsync(cancellationToken);
            loadedState = _registryLedger!;
            loadError = _registryLoadError;
        }
        finally
        {
            _registryLock.Release();
        }

        var localUsage = await _collectAsync(
            now.AddDays(-32),
            loadedState.FileCheckpoints,
            cancellationToken);
        var ledger = loadedState with
        {
            FileCheckpoints = localUsage.FileCheckpoints,
        };
        return new HybridQuotaRefreshContext(localUsage, ledger)
        {
            HasChanges = localUsage.HasCheckpointChanges,
            PersistenceWarning = loadError is null
                ? null
                : $"{loadError} 本次本地估算结果尚未保存。",
        };
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

        if (display.ServerNow is not { } serverNow)
        {
            return display with
            {
                EstimateStatus = AppendStatus(
                    display.EstimateStatus,
                    "缺少服务器时间，已跳过额度估算"),
            };
        }

        var observedAt = RequireUtc(serverNow);
        var statuses = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.PersistenceWarning))
        {
            statuses.Add(context.PersistenceWarning);
        }
        var source = QuotaEstimateSource.Local;
        QuotaUsageObservation observation;

        if (analyticsAvailability == AnalyticsAvailability.Available &&
            analytics?.State == AnalyticsUsageState.Valid)
        {
            source = QuotaEstimateSource.Analytics;
            var estimate = analytics.UpperCredits > 0
                ? QuotaEstimateMath.TryCreateFullIntervalPrecise(
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
        else if (analyticsAvailability == AnalyticsAvailability.NotRequested)
        {
            observation = CreateLocalObservation(
                context,
                account,
                display,
                segment,
                observedAt,
                statuses);
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
        var intersection =
            source == QuotaEstimateSource.Local &&
            !observation.IsLocalScanComplete
                ? null
                : QuotaEstimateMath.IntersectRecentCompatible(
                    accountLedger.Observations
                        .Where(item =>
                            item.Source == source &&
                            (source != QuotaEstimateSource.Local ||
                             string.Equals(
                                 item.RateCardVersion,
                                 observation.RateCardVersion,
                                 StringComparison.Ordinal) &&
                             item.ActivationStartedAt == observation.ActivationStartedAt))
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

    public async Task<string?> CompleteRefreshAsync(
        HybridQuotaRefreshContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.HasChanges && !_hasPendingSave)
        {
            return context.PersistenceWarning;
        }

        await _registryLock.WaitAsync(cancellationToken);
        try
        {
            var stateToSave = _registryLedger is not null
                ? MergeObservations(_registryLedger, context.Ledger)
                : context.Ledger;
            context.Ledger = stateToSave;
            _registryLedger = stateToSave;
            _registryLoadAttempted = true;
            _hasPendingSave = true;
            try
            {
                await _saveAsync(stateToSave, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                context.PersistenceWarning =
                    $"{LedgerSaveError} 本次本地估算结果未保存，将稍后重试。";
                return context.PersistenceWarning;
            }

            _registryLoadError = null;
            _hasPendingSave = false;
            _pendingRegistryObservations.Clear();
            context.HasChanges = false;
            context.PersistenceWarning = null;
            return null;
        }
        finally
        {
            _registryLock.Release();
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
            await EnsureRegistryLedgerLoadedAsync(cancellationToken);

            var previous = _registryLedger!;
            var observedAt = RequireUtc(_utcNow());
            var hadLoadError = _registryLoadError is not null;
            var updated = QuotaEstimateLedgerService.ObserveRegistry(
                previous,
                registry,
                observedAt);
            if (hadLoadError)
            {
                _pendingRegistryObservations.Add(
                    new PendingRegistryObservation(registry, observedAt));
            }

            if (!ReferenceEquals(previous, updated))
            {
                _registryLedger = updated;
                _hasPendingSave = true;
            }

            if (!_hasPendingSave)
            {
                return _registryLoadError;
            }

            try
            {
                await _saveAsync(_registryLedger!, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return AppendStatus(
                    _registryLoadError,
                    $"{LedgerSaveError} 本次本地状态未保存，将稍后重试。");
            }

            _hasPendingSave = false;
            _registryLoadError = null;
            _pendingRegistryObservations.Clear();
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

    private async Task EnsureRegistryLedgerLoadedAsync(
        CancellationToken cancellationToken)
    {
        if (_registryLoadAttempted && _registryLoadError is null)
        {
            return;
        }

        var loaded = await _loadAsync(cancellationToken);
        if (!_registryLoadAttempted || _registryLedger is null)
        {
            _registryLedger = loaded.State;
        }
        else if (loaded.Error is null)
        {
            var recovered = loaded.State;
            foreach (var pending in _pendingRegistryObservations)
            {
                recovered = QuotaEstimateLedgerService.ObserveRegistry(
                    recovered,
                    pending.Registry,
                    pending.ObservedAt);
            }

            _registryLedger = MergeObservations(recovered, _registryLedger);
            _pendingRegistryObservations.Clear();
        }

        _registryLoadError = loaded.Error;
        _registryLoadAttempted = true;
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
        var unknownModelCount = 0;
        var unknownTierCount = 0;
        var invalidUsageCount = 0;
        if (!context.Ledger.Accounts.TryGetValue(
                account.AccountKey,
                out var existingLedger) ||
            existingLedger.Activations.Count == 0)
        {
            statuses.Add("账号历史归属不明确，将从本次刷新开始记录");
        }

        foreach (var aggregate in GetAggregates(context.LocalUsage))
        {
            if (aggregate.Timestamp < segment.SegmentStart ||
                aggregate.Timestamp > observedAt ||
                !IsUnambiguouslyAttributed(
                    context.Ledger,
                    account.AccountKey,
                    aggregate.Timestamp))
            {
                continue;
            }

            switch (aggregate.FailureReason)
            {
                case CreditPricingFailureReason.None:
                    attributedCredits += aggregate.Credits;
                    pricedCount++;
                    break;
                case CreditPricingFailureReason.UnknownModel:
                    unknownModelCount++;
                    break;
                case CreditPricingFailureReason.UnknownServiceTier:
                    unknownTierCount++;
                    break;
                default:
                    invalidUsageCount++;
                    break;
            }
        }

        var activation = FindUnambiguousActivation(
            context.Ledger,
            account.AccountKey,
            observedAt);
        var hasActivationCoverage = HasFullSegmentCoverage(
            context.Ledger,
            account.AccountKey,
            segment.SegmentStart,
            observedAt);
        var hasFullCoverage =
            context.LocalUsage.IsComplete && hasActivationCoverage;
        PeriodQuotaEstimate? estimate = null;
        var kind = hasFullCoverage
            ? QuotaObservationKind.FullSegment
            : QuotaObservationKind.Delta;
        if (context.LocalUsage.IsComplete &&
            attributedCredits > 0 &&
            hasFullCoverage)
        {
            estimate = QuotaEstimateMath.TryCreateFullIntervalPrecise(
                attributedCredits,
                attributedCredits,
                display.UsedPercent,
                PercentResolution);
        }
        else if (context.LocalUsage.IsComplete &&
            attributedCredits > 0 &&
            activation is not null)
        {
            var earlier = context.Ledger.Accounts
                .GetValueOrDefault(account.AccountKey)?
                .Observations
                .Where(item =>
                    item.Segment == segment &&
                    item.Source == QuotaEstimateSource.Local &&
                    item.IsLocalScanComplete &&
                    string.Equals(
                        item.RateCardVersion,
                        CodexCreditRateCard.Version,
                        StringComparison.Ordinal) &&
                    item.ActivationStartedAt == activation?.StartedAt &&
                    item.ObservedAt < observedAt)
                .OrderByDescending(item => item.ObservedAt)
                .FirstOrDefault();
            if (earlier is not null)
            {
                estimate = QuotaEstimateMath.TryCreateDeltaIntervalPrecise(
                    attributedCredits - earlier.AttributedCredits,
                    earlier.UsedPercent,
                    earlier.PercentResolution,
                    display.UsedPercent,
                    PercentResolution);
            }
        }

        if (!context.LocalUsage.IsComplete)
        {
            var partialDetails = new List<string>();
            if (context.LocalUsage.SkippedFileCount > 0)
            {
                partialDetails.Add($"跳过 {context.LocalUsage.SkippedFileCount} 个文件");
            }

            if (context.LocalUsage.InvalidLineCount > 0)
            {
                partialDetails.Add($"忽略 {context.LocalUsage.InvalidLineCount} 行异常记录");
            }

            statuses.Add(partialDetails.Count == 0
                ? "本机用量扫描不完整"
                : $"本机用量扫描不完整（{string.Join("，", partialDetails)}）");
        }

        if (unknownTierCount > 0)
        {
            statuses.Add("速度模式未知，部分用量无法计价");
        }

        if (pricedCount == 0)
        {
            statuses.Add(unknownModelCount > 0 && unknownTierCount == 0
                ? "当前模型暂无官方费率"
                : "当前片段没有可计价的本机用量");
        }
        else if (!hasFullCoverage && estimate is null)
        {
            statuses.Add("已建立估算基线，继续使用后再次刷新");
        }

        if (pricedCount > 0 &&
            unknownModelCount + unknownTierCount + invalidUsageCount > 0)
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
            kind,
            context.LocalUsage.IsComplete,
            context.LocalUsage.InvalidLineCount,
            context.LocalUsage.SkippedFileCount,
            CodexCreditRateCard.Version,
            activation?.StartedAt);
    }

    private static QuotaUsageObservation CreateObservation(
        QuotaSegment segment,
        DateTimeOffset observedAt,
        double usedPercent,
        decimal attributedCredits,
        bool hasFullSegmentCoverage,
        PeriodQuotaEstimate? estimate,
        QuotaEstimateSource source,
        QuotaObservationKind kind,
        bool isLocalScanComplete = true,
        int malformedLineCount = 0,
        int skippedFileCount = 0,
        string? rateCardVersion = null,
        DateTimeOffset? activationStartedAt = null) =>
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
            kind)
        {
            IsLocalScanComplete = isLocalScanComplete,
            MalformedLineCount = malformedLineCount,
            SkippedFileCount = skippedFileCount,
            RateCardVersion = rateCardVersion,
            ActivationStartedAt = activationStartedAt,
        };

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
                .Distinct()
                .OrderBy(item => item.ObservedAt)
                .ToArray(),
        };
        context.Ledger = context.Ledger with { Accounts = accounts };
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

    private IEnumerable<LocalUsageAggregate> GetAggregates(
        LocalUsageCollectionResult localUsage)
    {
        if (localUsage.Aggregates.Count > 0 || localUsage.Events.Count == 0)
        {
            return localUsage.Aggregates;
        }

        return localUsage.Events.Select(usage =>
        {
            var calculation = _rateCard.CalculateCredits(usage);
            return new LocalUsageAggregate(
                usage.Timestamp,
                calculation.Credits,
                calculation.FailureReason);
        });
    }

    private static AccountActivationInterval? FindUnambiguousActivation(
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
            string.Equals(matches[0].Key, accountKey, StringComparison.Ordinal)
                ? matches[0].Activation
                : null;
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
        if (covering.EndedAt is { } endedAt && endedAt <= observedAt)
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

    private static QuotaEstimateLedgerState MergeObservations(
        QuotaEstimateLedgerState latest,
        QuotaEstimateLedgerState batch)
    {
        var accounts = latest.Accounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var (accountKey, batchLedger) in batch.Accounts)
        {
            if (!accounts.TryGetValue(accountKey, out var latestLedger))
            {
                accounts[accountKey] = batchLedger with
                {
                    Observations = batchLedger.Observations
                        .Distinct()
                        .OrderBy(item => item.ObservedAt)
                        .ToArray(),
                };
                continue;
            }

            accounts[accountKey] = latestLedger with
            {
                Observations = latestLedger.Observations
                    .Concat(batchLedger.Observations)
                    .Distinct()
                    .OrderBy(item => item.ObservedAt)
                    .ToArray(),
            };
        }

        return new QuotaEstimateLedgerState(accounts)
        {
            FileCheckpoints = batch.FileCheckpoints,
        };
    }

    private static string AppendStatus(string? existing, string status) =>
        string.IsNullOrWhiteSpace(existing)
            ? status
            : $"{existing}；{status}";

    private static bool IsPersistenceFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            OperationCanceledException;

    private sealed record PendingRegistryObservation(
        AccountRegistry Registry,
        DateTimeOffset ObservedAt);

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
