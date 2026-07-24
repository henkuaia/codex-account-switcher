using System.Text;
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

        Assert.Empty(result.Events);
        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(2, bucket.PricedEventCount);
        var rateCard = new CodexCreditRateCard();
        var expectedCredits =
            rateCard.CalculateCredits(new LocalUsageEvent(
                DateTimeOffset.Parse("2026-07-24T05:01:00Z"),
                "gpt-5.4",
                "priority",
                20_203,
                10_000,
                397)).Credits +
            rateCard.CalculateCredits(new LocalUsageEvent(
                DateTimeOffset.Parse("2026-07-24T05:05:00Z"),
                "gpt-5.3-codex",
                "default",
                20,
                10,
                3)).Credits;
        Assert.Equal(expectedCredits, bucket.PricedCredits);
        var checkpoint = Assert.Single(result.FileCheckpoints).Value;
        Assert.Equal("gpt-5.3-codex", checkpoint.Model);
        Assert.Equal("default", checkpoint.ServiceTier);
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

        Assert.Empty(result.Events);
        Assert.Equal(
            1,
            Assert.Single(result.Buckets).UnknownServiceTierEventCount);
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

        var bucket = Assert.Single(second.Buckets);
        Assert.Equal(2, bucket.PricedEventCount);
        Assert.True(second.ParsedByteCount < new FileInfo(path).Length);
        var checkpoint = Assert.Single(second.FileCheckpoints).Value;
        Assert.Equal("nested/session.jsonl", checkpoint.RelativePath);
        Assert.Equal("gpt-5.4", checkpoint.Model);
        Assert.Equal("priority", checkpoint.ServiceTier);
        Assert.Equal(CodexCreditRateCard.Version, checkpoint.RateCardVersion);
        Assert.DoesNotContain(directory.Path, checkpoint.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shrunk_or_rotated_file_is_rescanned_without_retaining_old_buckets()
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

        var bucket = Assert.Single(second.Buckets);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T06:02:00Z"), bucket.FirstEventAtUtc);
        Assert.Equal(1, bucket.PricedEventCount);
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

        var bucket = Assert.Single(second.Buckets);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T06:02:00Z"), bucket.FirstEventAtUtc);
        Assert.Equal(1, bucket.PricedEventCount);
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

        Assert.Single(result.Buckets);
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
    public async Task Deleted_malformed_only_checkpoint_remains_an_incomplete_tombstone()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "malformed-only.jsonl");
        directory.Write(
            "malformed-only.jsonl",
            """{"timestamp":""" + Environment.NewLine);
        var collector = new LocalCodexUsageCollector(
            directory.Path,
            utcNow: () => EarliestUtc.AddDays(1));
        var first = await collector.CollectAsync(EarliestUtc, CancellationToken.None);
        Assert.Empty(first.Aggregates);
        Assert.Equal(1, first.InvalidLineCount);
        var checkpoint = Assert.Single(first.FileCheckpoints).Value;
        Assert.False(checkpoint.HasCompleteScan);
        File.Delete(path);

        var second = await collector.CollectAsync(
            EarliestUtc,
            first.FileCheckpoints,
            CancellationToken.None);

        Assert.False(second.IsComplete);
        Assert.Equal(1, second.SkippedFileCount);
        Assert.Equal(1, second.InvalidLineCount);
        var tombstone = Assert.Single(second.FileCheckpoints).Value;
        Assert.Equal(checkpoint.RelativePath, tombstone.RelativePath);
        Assert.True(tombstone.IsTombstone);
        Assert.False(tombstone.HasCompleteScan);

        var expired = await collector.CollectAsync(
            tombstone.RelevantThroughUtc.AddTicks(1),
            second.FileCheckpoints,
            CancellationToken.None);

        Assert.True(expired.IsComplete);
        Assert.Empty(expired.FileCheckpoints);
    }

    [Fact]
    public async Task First_run_file_open_failure_persists_a_bounded_tombstone()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "locked.jsonl");
        directory.Write("locked.jsonl", "{}");
        var collector = new LocalCodexUsageCollector(
            directory.Path,
            utcNow: () => EarliestUtc.AddDays(1));
        LocalUsageCollectionResult first;
        await using (var locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            first = await collector.CollectAsync(EarliestUtc, CancellationToken.None);
        }

        var tombstone = Assert.Single(first.FileCheckpoints).Value;
        Assert.True(tombstone.IsTombstone);
        Assert.False(tombstone.HasCompleteScan);
        Assert.True(tombstone.RelevantThroughUtc >= EarliestUtc);
        File.Delete(path);

        var second = await collector.CollectAsync(
            EarliestUtc,
            first.FileCheckpoints,
            CancellationToken.None);

        Assert.False(second.IsComplete);
        Assert.Single(second.FileCheckpoints);
    }

    [Fact]
    public async Task Current_open_failure_renews_an_expired_checkpoint_tombstone()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "locked.jsonl");
        directory.Write("locked.jsonl", "{}");
        var observedAt = EarliestUtc.AddDays(1);
        var collector = new LocalCodexUsageCollector(
            directory.Path,
            utcNow: () => observedAt);
        var initial = await collector.CollectAsync(
            EarliestUtc,
            CancellationToken.None);
        var checkpoint = Assert.Single(initial.FileCheckpoints).Value with
        {
            LastWriteTimeUtc = EarliestUtc.AddDays(-1),
            RelevantThroughUtc = EarliestUtc.AddTicks(-1),
        };
        LocalUsageCollectionResult result;
        await using (var locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            result = await collector.CollectAsync(
                EarliestUtc,
                new Dictionary<string, LocalUsageFileCheckpoint>(
                    StringComparer.Ordinal)
                {
                    [checkpoint.RelativePath] = checkpoint,
                },
                CancellationToken.None);
        }

        var tombstone = Assert.Single(result.FileCheckpoints).Value;
        Assert.True(tombstone.IsTombstone);
        Assert.Equal(observedAt, tombstone.RelevantThroughUtc);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task Large_history_persists_hourly_buckets_instead_of_token_events()
    {
        const int eventCount = 25_000;
        using var directory = new TemporaryDirectory();
        var historyStart = DateTimeOffset.Parse("2026-07-20T00:00:00Z");
        var content = new StringBuilder(
            eventCount * 200);
        content.AppendLine(
            """{"timestamp":"2026-07-20T00:00:00Z","type":"turn_context","payload":{"model":"gpt-5.6-sol"}}""");
        content.AppendLine(
            """{"timestamp":"2026-07-20T00:00:01Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}}""");
        for (var index = 0; index < eventCount; index++)
        {
            var timestamp = historyStart.AddTicks(
                TimeSpan.FromHours(48).Ticks * index / eventCount);
            content.Append("{\"timestamp\":\"");
            content.Append(timestamp.ToString("O"));
            content.AppendLine(
                "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":0,\"output_tokens\":100}}}}");
        }

        directory.Write("large-history.jsonl", content.ToString());
        var result = await new LocalCodexUsageCollector(directory.Path)
            .CollectAsync(historyStart, CancellationToken.None);

        Assert.Empty(result.Events);
        Assert.Empty(result.Aggregates);
        Assert.InRange(result.Buckets.Count, 1, 49);
        var checkpoint = Assert.Single(result.FileCheckpoints).Value;
        Assert.Empty(checkpoint.Aggregates);
        Assert.Equal(result.Buckets.Count, checkpoint.Buckets.Count);

        var ledgerPath = Path.Combine(directory.Path, "ledger", "quota.json");
        await new QuotaEstimateLedgerService(ledgerPath).SaveAsync(
            QuotaEstimateLedgerState.Empty with
            {
                FileCheckpoints = result.FileCheckpoints,
            },
            CancellationToken.None);

        Assert.InRange(new FileInfo(ledgerPath).Length, 1, 50_000);
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
