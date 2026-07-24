using CodexAccountSwitcher.Models;
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
        Assert.False(result.IsComplete);
        var checkpoint = Assert.Single(result.FileCheckpoints).Value;
        Assert.True(checkpoint.CompletedLineByteOffset < new FileInfo(
            Path.Combine(directory.Path, "session.jsonl")).Length);
    }

    [Fact]
    public async Task Structurally_incomplete_token_event_marks_scan_incomplete()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("session.jsonl", """
            {"timestamp":"2026-07-24T05:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T05:01:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}}
            {"timestamp":"2026-07-24T05:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000,"cached_input_tokens":0}}}}
            """);

        var result = await new LocalCodexUsageCollector(directory.Path)
            .CollectAsync(EarliestUtc, CancellationToken.None);

        Assert.Empty(result.Aggregates);
        Assert.Equal(1, result.InvalidLineCount);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task Incremental_restart_reads_only_appended_lines_and_preserves_model_tier_state()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "nested", "session.jsonl");
        directory.Write("nested/session.jsonl", """
            {"timestamp":"2026-07-24T05:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T05:01:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"priority"}}}
            {"timestamp":"2026-07-24T05:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000,"cached_input_tokens":0,"output_tokens":0}}}}
            """);
        var first = await new LocalCodexUsageCollector(directory.Path)
            .CollectAsync(EarliestUtc, CancellationToken.None);
        await File.AppendAllTextAsync(path, Environment.NewLine + """
            {"timestamp":"2026-07-24T05:03:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":2000,"cached_input_tokens":0,"output_tokens":0}}}}
            """ + Environment.NewLine);

        var second = await new LocalCodexUsageCollector(directory.Path)
            .CollectAsync(EarliestUtc, first.FileCheckpoints, CancellationToken.None);

        Assert.Equal(2, second.Aggregates.Count);
        Assert.All(second.Aggregates, aggregate =>
            Assert.Equal(CreditPricingFailureReason.None, aggregate.FailureReason));
        Assert.True(second.ParsedByteCount < new FileInfo(path).Length);
        var checkpoint = Assert.Single(second.FileCheckpoints).Value;
        Assert.Equal("nested/session.jsonl", checkpoint.RelativePath);
        Assert.Equal("gpt-5.4", checkpoint.Model);
        Assert.Equal("priority", checkpoint.ServiceTier);
        Assert.Equal(CodexCreditRateCard.Version, checkpoint.RateCardVersion);
        Assert.DoesNotContain(directory.Path, checkpoint.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shrunk_or_rotated_file_is_rescanned_without_retaining_old_aggregates()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.jsonl");
        directory.Write("session.jsonl", """
            {"timestamp":"2026-07-24T05:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T05:01:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}}
            {"timestamp":"2026-07-24T05:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000,"cached_input_tokens":0,"output_tokens":0}}}}
            {"timestamp":"2026-07-24T05:03:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":2000,"cached_input_tokens":0,"output_tokens":0}}}}
            """);
        var first = await new LocalCodexUsageCollector(directory.Path)
            .CollectAsync(EarliestUtc, CancellationToken.None);
        await File.WriteAllTextAsync(path, """
            {"timestamp":"2026-07-24T06:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T06:01:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}}
            {"timestamp":"2026-07-24T06:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":500,"cached_input_tokens":0,"output_tokens":0}}}}
            """);

        var second = await new LocalCodexUsageCollector(directory.Path)
            .CollectAsync(EarliestUtc, first.FileCheckpoints, CancellationToken.None);

        var aggregate = Assert.Single(second.Aggregates);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T06:02:00Z"), aggregate.Timestamp);
        Assert.True(second.IsComplete);
    }

    [Fact]
    public async Task Grown_in_place_rewrite_with_same_prefix_is_rescanned()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.jsonl");
        var sharedPrefix = new string(' ', 5000);
        await File.WriteAllTextAsync(path, sharedPrefix + """
            {"timestamp":"2026-07-24T05:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T05:01:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}}
            {"timestamp":"2026-07-24T05:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000,"cached_input_tokens":0,"output_tokens":0}}}}
            """);
        var creationTimeUtc = File.GetCreationTimeUtc(path);
        var first = await new LocalCodexUsageCollector(directory.Path)
            .CollectAsync(EarliestUtc, CancellationToken.None);
        await File.WriteAllTextAsync(path, sharedPrefix + """
            {"timestamp":"2026-07-24T06:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T06:01:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}}
            {"timestamp":"2026-07-24T06:01:30Z","type":"response_item","payload":{"padding":"this makes the rewritten file longer than the prior version"}}
            {"timestamp":"2026-07-24T06:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":2000,"cached_input_tokens":0,"output_tokens":0}}}}
            """);
        File.SetCreationTimeUtc(path, creationTimeUtc);

        var second = await new LocalCodexUsageCollector(directory.Path)
            .CollectAsync(EarliestUtc, first.FileCheckpoints, CancellationToken.None);

        var aggregate = Assert.Single(second.Aggregates);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T06:02:00Z"), aggregate.Timestamp);
    }

    [Fact]
    public async Task Locked_file_is_skipped_without_losing_other_files()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("locked.jsonl", """
            {"timestamp":"2026-07-24T05:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            """);
        directory.Write("good.jsonl", """
            {"timestamp":"2026-07-24T05:00:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
            {"timestamp":"2026-07-24T05:01:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}}
            {"timestamp":"2026-07-24T05:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000,"cached_input_tokens":0,"output_tokens":0}}}}
            """);
        await using var locked = new FileStream(
            Path.Combine(directory.Path, "locked.jsonl"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = await new LocalCodexUsageCollector(directory.Path)
            .CollectAsync(EarliestUtc, CancellationToken.None);

        Assert.Single(result.Aggregates);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task Missing_session_root_marks_scan_incomplete()
    {
        var missingRoot = Path.Combine(
            Path.GetTempPath(),
            $"codex-missing-sessions-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(missingRoot));

        var result = await new LocalCodexUsageCollector(missingRoot)
            .CollectAsync(EarliestUtc, CancellationToken.None);

        Assert.Empty(result.Aggregates);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.False(result.IsComplete);
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
