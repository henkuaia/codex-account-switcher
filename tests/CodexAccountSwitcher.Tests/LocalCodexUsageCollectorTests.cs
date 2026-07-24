using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class LocalCodexUsageCollectorTests
{
    private static readonly DateTimeOffset EarliestUtc =
        DateTimeOffset.Parse("2026-07-24T05:00:00Z");

    [Fact]
    public async Task Collects_token_events_with_model_and_tier_state_from_the_same_file()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("session.jsonl", """
            {"timestamp":"2026-07-24T04:57:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T04:58:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}}}
            {"timestamp":"2026-07-24T05:00:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"priority"}}}
            {"timestamp":"2026-07-24T05:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":20203,"cached_input_tokens":10000,"output_tokens":397,"reasoning_output_tokens":201}}}}
            {"timestamp":"2026-07-24T05:02:00Z","type":"turn_context","payload":{"model":"gpt-5.3-codex"}}
            {"timestamp":"2026-07-24T05:03:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}}
            {"timestamp":"2026-07-24T05:04:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":999999,"cached_input_tokens":0,"output_tokens":999999}}}}
            {"timestamp":"2026-07-24T05:05:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":20,"cached_input_tokens":10,"output_tokens":3}}}}
            """);
        var collector = new LocalCodexUsageCollector(directory.Path);

        var result = await collector.CollectAsync(EarliestUtc, CancellationToken.None);

        Assert.Equal(2, result.Events.Count);
        Assert.Equal("gpt-5.4", result.Events[0].Model);
        Assert.Equal("priority", result.Events[0].ServiceTier);
        Assert.Equal(20_203, result.Events[0].InputTokens);
        Assert.Equal(10_000, result.Events[0].CachedInputTokens);
        Assert.Equal(397, result.Events[0].OutputTokens);
        Assert.Equal("gpt-5.3-codex", result.Events[1].Model);
        Assert.Equal("default", result.Events[1].ServiceTier);
        Assert.Equal(3, result.Events[1].OutputTokens);
        Assert.Equal(0, result.InvalidLineCount);
    }

    [Fact]
    public async Task Skips_files_last_written_before_the_collection_window()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "old-session.jsonl");
        await File.WriteAllTextAsync(path, """
            {"timestamp":"2026-07-24T05:01:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T05:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}}}
            """);
        File.SetLastWriteTimeUtc(path, EarliestUtc.UtcDateTime.AddTicks(-1));
        var collector = new LocalCodexUsageCollector(directory.Path);

        var result = await collector.CollectAsync(EarliestUtc, CancellationToken.None);

        Assert.Empty(result.Events);
        Assert.Equal(0, result.InvalidLineCount);
    }

    [Fact]
    public async Task Counts_a_malformed_in_progress_final_line_without_losing_valid_events()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("session.jsonl", """
            {"timestamp":"2026-07-24T05:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T05:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}}}
            {"timestamp"
            """);
        var collector = new LocalCodexUsageCollector(directory.Path);

        var result = await collector.CollectAsync(EarliestUtc, CancellationToken.None);

        Assert.Single(result.Events);
        Assert.Equal(1, result.InvalidLineCount);
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        using var directory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var collector = new LocalCodexUsageCollector(directory.Path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            collector.CollectAsync(EarliestUtc, cancellationSource.Token));
    }
}
