using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed class TokenUsageStatisticsService
{
    private readonly LocalCodexUsageCollector _collector;
    private readonly TokenUsageLedgerService _ledgerService;
    private readonly Func<DateTimeOffset> _utcNow;

    public TokenUsageStatisticsService(
        LocalCodexUsageCollector collector,
        TokenUsageLedgerService ledgerService,
        Func<DateTimeOffset>? utcNow = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _ledgerService = ledgerService ?? throw new ArgumentNullException(nameof(ledgerService));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public static TokenUsageStatisticsService CreateDefault(
        LocalCodexUsageCollector collector) =>
        new(
            collector,
            TokenUsageLedgerService.CreateDefault());

    public async Task<TokenUsageSnapshot> RefreshAsync(
        CancellationToken cancellationToken)
    {
        var now = _utcNow().ToUniversalTime();
        var loaded = await _ledgerService.LoadAsync(cancellationToken);
        if (loaded.Error is not null)
        {
            throw new InvalidOperationException(loaded.Error);
        }

        var usage = await _collector.CollectAsync(
            DateTimeOffset.MinValue,
            loaded.FileCheckpoints,
            cancellationToken);
        if (usage.HasCheckpointChanges)
        {
            await _ledgerService.SaveAsync(
                usage.FileCheckpoints,
                cancellationToken);
        }

        return new TokenUsageSnapshot(usage.Buckets, now, usage.IsComplete);
    }
}
