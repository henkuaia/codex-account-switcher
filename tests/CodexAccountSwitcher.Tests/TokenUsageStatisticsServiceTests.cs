using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class TokenUsageStatisticsServiceTests
{
    [Fact]
    public async Task Refreshes_and_persists_token_history_incrementally()
    {
        using var directory = new TemporaryDirectory();
        var sessionPath = Path.Combine(directory.Path, "sessions", "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        await File.WriteAllTextAsync(sessionPath, """
            {"timestamp":"2026-07-26T01:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-26T01:01:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}}
            {"timestamp":"2026-07-26T01:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":20}}}}
            """);
        var ledgerPath = Path.Combine(directory.Path, "token-usage-ledger.json");
        var service = new TokenUsageStatisticsService(
            new LocalCodexUsageCollector(Path.GetDirectoryName(sessionPath)!),
            new TokenUsageLedgerService(ledgerPath),
            () => DateTimeOffset.Parse("2026-07-29T00:00:00Z"));

        var first = await service.RefreshAsync(CancellationToken.None);
        await File.AppendAllTextAsync(sessionPath, Environment.NewLine + """
            {"timestamp":"2026-07-26T01:03:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":200,"cached_input_tokens":50,"output_tokens":30}}}}
            """ + Environment.NewLine);
        var second = await service.RefreshAsync(CancellationToken.None);

        Assert.True(File.Exists(ledgerPath));
        var bucket = Assert.Single(second.Buckets);
        Assert.Equal(300, bucket.InputTokens);
        Assert.Equal(90, bucket.CachedInputTokens);
        Assert.Equal(50, bucket.OutputTokens);
        Assert.Equal(350, bucket.TotalTokens);
        Assert.True(second.IsComplete);
        Assert.True(first.Buckets.Sum(item => item.TotalTokens) < second.Buckets.Sum(item => item.TotalTokens));
    }
}
