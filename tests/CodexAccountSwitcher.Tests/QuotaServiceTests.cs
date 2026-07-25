using System.Net;
using System.Net.Http;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class QuotaServiceTests
{
    [Fact]
    public async Task Refresh_account_sends_authenticated_usage_request_and_parses_successful_response()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(JsonResponse()));
        using var client = new HttpClient(handler);
        var service = new QuotaService(client);

        var update = await service.RefreshAccountAsync(account, home.Path, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://chatgpt.com/backend-api/wham/usage", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("access-secret", request.Headers.Authorization.Parameter);
        Assert.Equal("acct-1", request.Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.Contains("CodexAccountSwitcher/1.0 codex-auth/0.2.10", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Null(update.Error);
        Assert.Equal("user-1::acct-1", update.AccountKey);
        Assert.Equal(73, update.Display!.RemainingPercent);
    }

    [Fact]
    public async Task Refresh_weekly_quota_fetches_analytics_and_applies_estimated_usd_range()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var resetAt = DateTimeOffset.Parse("2026-07-27T12:00:00Z").ToUnixTimeSeconds();
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? JsonResponse("""
                    {"rate_limit":{"secondary_window":{
                      "used_percent":25,
                      "limit_window_seconds":604800,
                      "reset_at":RESET_AT,
                      "reset_after_seconds":172800
                    }}}
                    """.Replace(
                        "RESET_AT",
                        resetAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                : JsonResponse("""
                    {"data":[
                      {"date":"2026-07-20","totals":{"credits":100}},
                      {"date":"2026-07-21","totals":{"credits":50}}
                    ]}
                    """)));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(7.84m, update.Display!.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(24.49m, update.Display.EstimatedPeriodQuotaUpperUsd);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(
            "https://chatgpt.com/backend-api/wham/analytics/daily-workspace-usage-counts?start_date=2026-07-20&end_date=2026-07-26&group_by=day",
            requests[1].RequestUri!.ToString());
        Assert.Equal("Bearer", requests[1].Headers.Authorization!.Scheme);
        Assert.Equal("acct-1", requests[1].Headers.GetValues("ChatGPT-Account-Id").Single());
    }

    [Fact]
    public async Task Weekly_utc_midnight_segment_includes_complete_start_day_in_lower_bound()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var resetAt = DateTimeOffset.Parse("2026-07-27T00:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? UsageResponse(
                    resetAt,
                    serverNow,
                    TimeSpan.FromDays(7),
                    usedPercent: 25)
                : JsonResponse("""
                    {"data":[
                      {"date":"2026-07-20","totals":{"credits":50}},
                      {"date":"2026-07-21","totals":{"credits":100}}
                    ]}
                    """)));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(
            account,
            home.Path,
            default);

        Assert.Null(update.Error);
        Assert.Equal(23.53m, update.Display!.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(24.49m, update.Display.EstimatedPeriodQuotaUpperUsd);
    }

    [Fact]
    public async Task Refresh_monthly_quota_uses_latest_redeemed_reset_for_estimate()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var resetAt = DateTimeOffset.Parse("2026-08-22T22:06:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-30T00:00:00Z");
        var resetAfter = (long)(resetAt - serverNow).TotalSeconds;
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/backend-api/wham/usage" => JsonResponse("""
                    {"rate_limit":{"secondary_window":{
                      "used_percent":25,
                      "limit_window_seconds":2592000,
                      "reset_at":RESET_AT,
                      "reset_after_seconds":RESET_AFTER
                    }}}
                    """
                    .Replace(
                        "RESET_AT",
                        resetAt.ToUnixTimeSeconds().ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    .Replace(
                        "RESET_AFTER",
                        resetAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)),
                "/backend-api/wham/rate-limit-reset-credits" => JsonResponse("""
                    {"credits":[
                      {"status":"redeemed","redeemed_at":"2026-07-25T08:00:00Z"},
                      {"status":"redeemed","redeemed_at":"2026-07-26T12:30:00Z"},
                      {"status":"available","redeemed_at":null}
                    ]}
                    """),
                _ => JsonResponse("""
                    {"data":[
                      {"date":"2026-07-26","totals":{"credits":50}},
                      {"date":"2026-07-27","totals":{"credits":100}}
                    ]}
                    """),
            }));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(75, update.Display!.RemainingPercent);
        Assert.Equal(15.69m, update.Display.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(24.49m, update.Display.EstimatedPeriodQuotaUpperUsd);
        var requests = handler.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal(
            "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits",
            requests[1].RequestUri!.ToString());
        Assert.Equal(
            "https://chatgpt.com/backend-api/wham/analytics/daily-workspace-usage-counts?start_date=2026-07-26&end_date=2026-07-31&group_by=day",
            requests[2].RequestUri!.ToString());
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("acct-1", request.Headers.GetValues("ChatGPT-Account-Id").Single());
        });
    }

    [Fact]
    public async Task Refresh_monthly_quota_without_redeemed_reset_uses_natural_window_start()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var resetAt = DateTimeOffset.Parse("2026-08-22T00:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-30T00:00:00Z");
        var resetAfter = (long)(resetAt - serverNow).TotalSeconds;
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/backend-api/wham/usage" => JsonResponse("""
                    {"rate_limit":{"secondary_window":{
                      "used_percent":25,
                      "limit_window_seconds":2592000,
                      "reset_at":RESET_AT,
                      "reset_after_seconds":RESET_AFTER
                    }}}
                    """
                    .Replace(
                        "RESET_AT",
                        resetAt.ToUnixTimeSeconds().ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    .Replace(
                        "RESET_AFTER",
                        resetAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)),
                "/backend-api/wham/rate-limit-reset-credits" => JsonResponse("""{"credits":[]}"""),
                _ => JsonResponse("""
                    {"data":[
                      {"date":"2026-07-23","totals":{"credits":50}},
                      {"date":"2026-07-24","totals":{"credits":100}}
                    ]}
                    """),
            }));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(23.53m, update.Display!.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(24.49m, update.Display.EstimatedPeriodQuotaUpperUsd);
        var requests = handler.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal(
            "https://chatgpt.com/backend-api/wham/analytics/daily-workspace-usage-counts?start_date=2026-07-23&end_date=2026-07-31&group_by=day",
            requests[2].RequestUri!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_history_failure_preserves_successful_monthly_percentage(bool invalidJson)
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var resetAt = DateTimeOffset.Parse("2026-08-22T22:06:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-30T00:00:00Z");
        var resetAfter = (long)(resetAt - serverNow).TotalSeconds;
        var requestCount = 0;
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            Interlocked.Increment(ref requestCount) == 1
                ? JsonResponse("""
                    {"rate_limit":{"secondary_window":{
                      "used_percent":25,
                      "limit_window_seconds":2592000,
                      "reset_at":RESET_AT,
                      "reset_after_seconds":RESET_AFTER
                    }}}
                    """
                    .Replace(
                        "RESET_AT",
                        resetAt.ToUnixTimeSeconds().ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    .Replace(
                        "RESET_AFTER",
                        resetAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                : invalidJson
                    ? JsonResponse("""{"credits":{}}""")
                    : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(75, update.Display!.RemainingPercent);
        Assert.Null(update.Display.EstimatedPeriodQuotaLowerUsd);
        Assert.Null(update.Display.EstimatedPeriodQuotaUpperUsd);
        Assert.Contains(
            "无法确定当前月额度片段",
            update.Display.EstimateStatus,
            StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Zero_monthly_usage_skips_reset_history_and_analytics()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("""
            {"rate_limit":{"secondary_window":{
              "used_percent":0,
              "limit_window_seconds":2592000,
              "reset_at":1787436360,
              "reset_after_seconds":1965960
            }}}
            """)));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(100, update.Display!.RemainingPercent);
        Assert.Null(update.Display.EstimatedPeriodQuotaLowerUsd);
        Assert.Null(update.Display.EstimatedPeriodQuotaUpperUsd);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Analytics_failure_preserves_successful_weekly_percentage()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var requestCount = 0;
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            Interlocked.Increment(ref requestCount) == 1
                ? JsonResponse("""
                    {"rate_limit":{"secondary_window":{
                      "used_percent":25,
                      "limit_window_seconds":604800,
                      "reset_at":1785000000,
                      "reset_after_seconds":172800
                    }}}
                    """)
                : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(75, update.Display!.RemainingPercent);
        Assert.Null(update.Display.EstimatedPeriodQuotaLowerUsd);
        Assert.Null(update.Display.EstimatedPeriodQuotaUpperUsd);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refresh_account_returns_redacted_error_for_unauthorized_response(HttpStatusCode statusCode)
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(statusCode) { Content = new StringContent("response-secret") }));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Equal(account.AccountKey, update.AccountKey);
        Assert.Null(update.Display);
        Assert.NotNull(update.Error);
        Assert.DoesNotContain("access-secret", update.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("response-secret", update.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_account_returns_redacted_error_when_request_times_out()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        using var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("response-secret")));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Equal(account.AccountKey, update.AccountKey);
        Assert.Null(update.Display);
        Assert.Equal("The quota refresh request timed out.", update.Error);
        Assert.DoesNotContain("access-secret", update.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("response-secret", update.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_account_propagates_unexpected_handler_failure()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        using var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("unexpected-handler-failure")));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new QuotaService(client).RefreshAccountAsync(account, home.Path, default));

        Assert.Equal("unexpected-handler-failure", exception.Message);
    }

    [Fact]
    public async Task Refresh_account_returns_structured_error_for_malformed_response()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"response-secret\"") }));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Equal(account.AccountKey, update.AccountKey);
        Assert.Null(update.Display);
        Assert.NotNull(update.Error);
        Assert.DoesNotContain("access-secret", update.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("response-secret", update.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_account_returns_structured_error_when_snapshot_is_missing()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(JsonResponse()));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Equal(account.AccountKey, update.AccountKey);
        Assert.Null(update.Display);
        Assert.NotNull(update.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refresh_account_returns_structured_error_when_snapshot_account_id_mismatches_registry()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-mismatch-secret");
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(JsonResponse()));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(client).RefreshAccountAsync(account, home.Path, default);

        Assert.Equal(account.AccountKey, update.AccountKey);
        Assert.Null(update.Display);
        Assert.NotNull(update.Error);
        Assert.DoesNotContain("access-secret", update.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("acct-mismatch-secret", update.Error, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refresh_all_continues_after_failure_and_never_runs_multiple_requests_concurrently()
    {
        using var home = new TemporaryDirectory();
        var accounts = new[]
        {
            Accounts.Record("user-1::acct-1", "first@example.com", accountId: "acct-1"),
            Accounts.Record("user-2::acct-2", "second@example.com", accountId: "acct-2"),
            Accounts.Record("user-3::acct-3", "third@example.com", accountId: "acct-3"),
        };
        foreach (var account in accounts)
        {
            WriteSnapshot(home, account, $"access-{account.ChatGptAccountId}", account.ChatGptAccountId);
        }

        var requestCount = 0;
        using var handler = new RecordingHttpMessageHandler(async (_, _) =>
        {
            await Task.Yield();
            return Interlocked.Increment(ref requestCount) == 1
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : JsonResponse();
        });
        using var client = new HttpClient(handler);
        var reports = new List<QuotaUpdate>();

        await new QuotaService(client).RefreshAllAsync(
            accounts,
            home.Path,
            (update, _) =>
            {
                reports.Add(update);
                return Task.CompletedTask;
            },
            default);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, handler.MaximumActiveRequests);
        Assert.Equal(3, reports.Count);
        Assert.NotNull(reports[0].Error);
        Assert.Null(reports[1].Error);
        Assert.Null(reports[2].Error);
    }

    [Fact]
    public async Task Refresh_all_awaits_each_report_before_starting_the_next_account()
    {
        using var home = new TemporaryDirectory();
        var first = Accounts.Record(
            "first-key",
            "first@example.com",
            accountId: "first-account");
        var second = Accounts.Record(
            "second-key",
            "second@example.com",
            accountId: "second-account");
        WriteSnapshot(home, first, "first-token", first.ChatGptAccountId);
        WriteSnapshot(home, second, "second-token", second.ChatGptAccountId);

        var secondRequestStarted = false;
        var firstReportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReport = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHttpMessageHandler(async (request, _) =>
        {
            var accountId = request.Headers.GetValues("ChatGPT-Account-Id").Single();
            if (accountId == second.ChatGptAccountId)
            {
                secondRequestStarted = true;
            }

            await Task.Yield();
            return JsonResponse();
        });
        using var client = new HttpClient(handler);
        var reports = new List<string>();
        var refreshTask = new QuotaService(client).RefreshAllAsync(
            [first, second],
            home.Path,
            async (update, cancellationToken) =>
            {
                reports.Add(update.AccountKey);
                if (update.AccountKey == first.AccountKey)
                {
                    firstReportStarted.SetResult();
                    await releaseFirstReport.Task.WaitAsync(cancellationToken);
                }
            },
            CancellationToken.None);

        await firstReportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondRequestStarted);

        releaseFirstReport.SetResult();
        var warning = await refreshTask;

        Assert.Null(warning);
        Assert.True(secondRequestStarted);
        Assert.Equal([first.AccountKey, second.AccountKey], reports);
    }

    [Fact]
    public async Task Analytics_empty_uses_full_window_local_weekly_estimate()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var resetAt = segmentStart.AddDays(7);
        QuotaEstimateLedgerState? saved = null;
        var hybrid = CreateHybrid(
            [LocalUsage(segmentStart.AddHours(1))],
            StateWithActivation(
                account,
                new AccountActivationInterval(segmentStart.AddMinutes(-1), null)),
            onSave: state => saved = state);
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? UsageResponse(resetAt, serverNow, TimeSpan.FromDays(7), usedPercent: 25)
                : JsonResponse("""{"data":[]}""")));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(15.69m, update.Display!.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(16.33m, update.Display.EstimatedPeriodQuotaUpperUsd);
        Assert.Equal(QuotaEstimateSource.Local, update.Display.EstimateSource);
        Assert.Equal(QuotaPeriod.Weekly, update.Display.Period);
        Assert.Single(saved!.Accounts[account.AccountKey].Observations);
    }

    [Fact]
    public async Task Analytics_empty_uses_local_monthly_estimate_after_redeemed_reset_selection()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var serverNow = DateTimeOffset.Parse("2026-07-30T00:00:00Z");
        var resetAt = DateTimeOffset.Parse("2026-08-22T00:00:00Z");
        var redeemedAt = DateTimeOffset.Parse("2026-07-26T12:30:00Z");
        var hybrid = CreateHybrid(
            [LocalUsage(redeemedAt.AddHours(1))],
            StateWithActivation(
                account,
                new AccountActivationInterval(redeemedAt.AddMinutes(-1), null)));
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/backend-api/wham/usage" =>
                    UsageResponse(resetAt, serverNow, TimeSpan.FromDays(30), usedPercent: 25),
                "/backend-api/wham/rate-limit-reset-credits" =>
                    JsonResponse("""
                        {"credits":[
                          {"status":"redeemed","redeemed_at":"2026-07-26T12:30:00Z"}
                        ]}
                        """),
                _ => JsonResponse("""{"data":[]}"""),
            }));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(QuotaPeriod.Monthly, update.Display!.Period);
        Assert.Equal(15.69m, update.Display.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(QuotaEstimateSource.Local, update.Display.EstimateSource);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Valid_analytics_remains_preferred_over_local_result()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var resetAt = segmentStart.AddDays(7);
        QuotaEstimateLedgerState? saved = null;
        var hybrid = CreateHybrid(
            [LocalUsage(segmentStart.AddHours(1), inputTokens: 8_000_000)],
            StateWithActivation(
                account,
                new AccountActivationInterval(segmentStart.AddMinutes(-1), null)),
            onSave: state => saved = state);
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? UsageResponse(resetAt, serverNow, TimeSpan.FromDays(7), usedPercent: 25)
                : JsonResponse("""
                    {"data":[
                      {"date":"2026-07-21","totals":{"credits":50}}
                    ]}
                    """)));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(QuotaEstimateSource.Analytics, update.Display!.EstimateSource);
        Assert.Equal(7.84m, update.Display.EstimatedPeriodQuotaLowerUsd);
        var observation = Assert.Single(saved!.Accounts[account.AccountKey].Observations);
        Assert.Equal(50m, observation.AttributedCredits);
        Assert.Equal(QuotaEstimateSource.Analytics, observation.Source);
    }

    [Fact]
    public async Task Analytics_http_failure_attempts_local_fallback()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var resetAt = segmentStart.AddDays(7);
        var hybrid = CreateHybrid(
            [LocalUsage(segmentStart.AddHours(1))],
            StateWithActivation(
                account,
                new AccountActivationInterval(segmentStart.AddMinutes(-1), null)));
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? UsageResponse(resetAt, serverNow, TimeSpan.FromDays(7), usedPercent: 25)
                : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(QuotaEstimateSource.Local, update.Display!.EstimateSource);
        Assert.Equal(15.69m, update.Display.EstimatedPeriodQuotaLowerUsd);
        Assert.Contains("Analytics 请求失败", update.Display.EstimateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zero_weekly_usage_records_local_credit_baseline_without_analytics()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var resetAt = segmentStart.AddDays(7);
        QuotaEstimateLedgerState? saved = null;
        var hybrid = CreateHybrid(
            [LocalUsage(segmentStart.AddHours(1))],
            StateWithActivation(
                account,
                new AccountActivationInterval(segmentStart.AddMinutes(-1), null)),
            onSave: state => saved = state);
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            UsageResponse(resetAt, serverNow, TimeSpan.FromDays(7), usedPercent: 0)));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(100, update.Display!.RemainingPercent);
        Assert.Single(handler.Requests);
        var observation = Assert.Single(saved!.Accounts[account.AccountKey].Observations);
        Assert.Equal(0, observation.UsedPercent);
        Assert.Equal(100m, observation.AttributedCredits);
        Assert.Equal(CodexCreditRateCard.Version, observation.RateCardVersion);
        Assert.Null(observation.LowerUsd);
        Assert.Null(observation.UpperUsd);
    }

    [Fact]
    public async Task Zero_monthly_usage_resolves_redeemed_segment_then_records_baseline()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var serverNow = DateTimeOffset.Parse("2026-07-30T00:00:00Z");
        var resetAt = DateTimeOffset.Parse("2026-08-22T00:00:00Z");
        var redeemedAt = DateTimeOffset.Parse("2026-07-26T12:30:00Z");
        QuotaEstimateLedgerState? saved = null;
        var hybrid = CreateHybrid(
            [LocalUsage(redeemedAt.AddHours(1))],
            StateWithActivation(
                account,
                new AccountActivationInterval(redeemedAt.AddMinutes(-1), null)),
            onSave: state => saved = state);
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/backend-api/wham/usage" =>
                    UsageResponse(resetAt, serverNow, TimeSpan.FromDays(30), usedPercent: 0),
                "/backend-api/wham/rate-limit-reset-credits" =>
                    JsonResponse("""
                        {"credits":[
                          {"status":"redeemed","redeemed_at":"2026-07-26T12:30:00Z"}
                        ]}
                        """),
                _ => throw new InvalidOperationException(
                    "Zero usage must not request Analytics."),
            }));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(100, update.Display!.RemainingPercent);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(
            "/backend-api/wham/rate-limit-reset-credits",
            requests[1].RequestUri!.AbsolutePath);
        var observation = Assert.Single(saved!.Accounts[account.AccountKey].Observations);
        Assert.Equal(redeemedAt, observation.Segment.SegmentStart);
        Assert.Equal(0, observation.UsedPercent);
        Assert.Equal(100m, observation.AttributedCredits);
        Assert.Equal(CodexCreditRateCard.Version, observation.RateCardVersion);
    }

    [Fact]
    public async Task Missing_server_now_with_hybrid_skips_analytics_and_estimate()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var resetAt = DateTimeOffset.Parse("2026-07-27T12:00:00Z");
        var segmentStart = resetAt.AddDays(-7);
        var hybrid = CreateHybrid(
            [LocalUsage(segmentStart.AddHours(1))],
            StateWithActivation(
                account,
                new AccountActivationInterval(segmentStart.AddMinutes(-1), null)));
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? JsonResponse(
                    """
                    {"rate_limit":{"secondary_window":{
                      "used_percent":25,
                      "limit_window_seconds":604800,
                      "reset_at":RESET_AT
                    }}}
                    """.Replace(
                        "RESET_AT",
                        resetAt.ToUnixTimeSeconds().ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                : throw new InvalidOperationException(
                    "Missing ServerNow must not request Analytics.")));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Null(update.Display!.ServerNow);
        Assert.Null(update.Display.EstimatedPeriodQuotaLowerUsd);
        Assert.Contains("缺少服务器时间", update.Display.EstimateStatus, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refresh_all_five_accounts_scans_once_and_saves_ledger_once()
    {
        using var home = new TemporaryDirectory();
        var accounts = Enumerable.Range(1, 5)
            .Select(index => Accounts.Record(
                $"user-{index}::acct-{index}",
                $"user{index}@example.com",
                accountId: $"acct-{index}"))
            .ToArray();
        foreach (var account in accounts)
        {
            WriteSnapshot(
                home,
                account,
                $"access-{account.ChatGptAccountId}",
                account.ChatGptAccountId);
        }

        var collectCount = 0;
        var saveCount = 0;
        var hybrid = CreateHybrid(
            [],
            QuotaEstimateLedgerState.Empty,
            onCollect: () => collectCount++,
            onSave: _ => saveCount++);
        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? UsageResponse(
                    segmentStart.AddDays(7),
                    serverNow,
                    TimeSpan.FromDays(7),
                    usedPercent: 25)
                : JsonResponse("""{"data":[]}""")));
        using var client = new HttpClient(handler);
        var reports = new List<QuotaUpdate>();

        await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAllAsync(
                accounts,
                home.Path,
                (update, _) =>
                {
                    reports.Add(update);
                    return Task.CompletedTask;
                },
                default);

        Assert.Equal(1, collectCount);
        Assert.Equal(1, saveCount);
        Assert.Equal(5, reports.Count);
        Assert.Equal(1, handler.MaximumActiveRequests);
    }

    [Fact]
    public async Task Estimator_start_failure_preserves_successful_server_display()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var hybrid = new HybridQuotaEstimateService(
            (_, _) => Task.FromException<LocalUsageCollectionResult>(
                new IOException("local-estimator-failure")),
            _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(
                QuotaEstimateLedgerState.Empty,
                null)),
            (_, _) => Task.CompletedTask,
            new CodexCreditRateCard());
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            JsonResponse("""
                {"rate_limit":{"secondary_window":{
                  "used_percent":25,
                  "limit_window_seconds":604800,
                  "reset_at":1785000000,
                  "reset_after_seconds":172800
                }}}
                """)));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(75, update.Display!.RemainingPercent);
        Assert.NotNull(update.Display.ResetsAt);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Estimator_internal_save_cancellation_preserves_successful_server_display()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var hybrid = new HybridQuotaEstimateService(
            (_, _) => Task.FromResult(new LocalUsageCollectionResult(
                [LocalUsage(segmentStart.AddHours(1))],
                0)),
            _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(
                StateWithActivation(
                    account,
                    new AccountActivationInterval(segmentStart.AddMinutes(-1), null)),
                null)),
            (_, _) => Task.FromCanceled(new CancellationToken(canceled: true)),
            new CodexCreditRateCard());
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? UsageResponse(
                    segmentStart.AddDays(7),
                    serverNow,
                    TimeSpan.FromDays(7),
                    usedPercent: 25)
                : JsonResponse("""{"data":[]}""")));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(75, update.Display!.RemainingPercent);
        Assert.Equal(QuotaEstimateSource.Local, update.Display.EstimateSource);
    }

    [Fact]
    public async Task Estimator_save_failure_returns_successful_display_with_unsaved_warning()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var hybrid = new HybridQuotaEstimateService(
            (_, _) => Task.FromResult(new LocalUsageCollectionResult(
                [LocalUsage(segmentStart.AddHours(1))],
                0)),
            _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(
                StateWithActivation(
                    account,
                    new AccountActivationInterval(segmentStart.AddMinutes(-1), null)),
                null)),
            (_, _) => Task.FromException(new IOException("ledger-save-failure")),
            new CodexCreditRateCard());
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? UsageResponse(
                    segmentStart.AddDays(7),
                    serverNow,
                    TimeSpan.FromDays(7),
                    usedPercent: 25)
                : JsonResponse("""{"data":[]}""")));
        using var client = new HttpClient(handler);

        var update = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAccountAsync(account, home.Path, default);

        Assert.Null(update.Error);
        Assert.Equal(75, update.Display!.RemainingPercent);
        Assert.Contains("未保存", update.Warning, StringComparison.Ordinal);
        Assert.Contains("未保存", update.Display.EstimateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_all_completion_warning_reports_one_update_per_account()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var hybrid = new HybridQuotaEstimateService(
            (_, _) => Task.FromResult(new LocalUsageCollectionResult(
                [LocalUsage(segmentStart.AddHours(1))],
                0)),
            _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(
                StateWithActivation(
                    account,
                    new AccountActivationInterval(segmentStart.AddMinutes(-1), null)),
                null)),
            (_, _) => Task.FromException(new IOException("ledger-save-failure")),
            new CodexCreditRateCard());
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? UsageResponse(
                    segmentStart.AddDays(7),
                    serverNow,
                    TimeSpan.FromDays(7),
                    usedPercent: 25)
                : JsonResponse("""{"data":[]}""")));
        using var client = new HttpClient(handler);
        var reports = new List<QuotaUpdate>();

        var warning = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAllAsync(
                [account],
                home.Path,
                (update, _) =>
                {
                    reports.Add(update);
                    return Task.CompletedTask;
                },
                default);

        var update = Assert.Single(reports);
        Assert.Null(update.Error);
        Assert.Equal(75, update.Display!.RemainingPercent);
        Assert.Null(update.Warning);
        Assert.Contains("未保存", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_all_save_failure_returns_shared_warning_after_reporting_each_account_in_order()
    {
        using var home = new TemporaryDirectory();
        var accounts = new[]
        {
            Accounts.Record(
                "user-1::acct-1",
                "first@example.com",
                accountId: "acct-1"),
            Accounts.Record(
                "user-2::acct-2",
                "second@example.com",
                accountId: "acct-2"),
        };
        foreach (var account in accounts)
        {
            WriteSnapshot(
                home,
                account,
                $"access-{account.ChatGptAccountId}",
                account.ChatGptAccountId);
        }

        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var hybrid = new HybridQuotaEstimateService(
            (_, _) => Task.FromResult(new LocalUsageCollectionResult([], 0)),
            _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(
                QuotaEstimateLedgerState.Empty,
                null)),
            (_, _) => Task.FromException(new IOException("ledger-save-failure")),
            new CodexCreditRateCard());
        using var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                ? UsageResponse(
                    segmentStart.AddDays(7),
                    serverNow,
                    TimeSpan.FromDays(7),
                    usedPercent: 25)
                : JsonResponse("""{"data":[]}""")));
        using var client = new HttpClient(handler);
        var reports = new List<QuotaUpdate>();

        var warning = await new QuotaService(
            client,
            hybridEstimator: hybrid).RefreshAllAsync(
                accounts,
                home.Path,
                (update, _) =>
                {
                    reports.Add(update);
                    return Task.CompletedTask;
                },
                default);

        Assert.Collection(
            reports,
            update =>
            {
                Assert.Equal(accounts[0].AccountKey, update.AccountKey);
                Assert.Null(update.Warning);
            },
            update =>
            {
                Assert.Equal(accounts[1].AccountKey, update.AccountKey);
                Assert.Null(update.Warning);
            });
        Assert.Contains("未保存", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estimator_propagates_user_cancellation()
    {
        using var home = new TemporaryDirectory();
        var account = Accounts.Record("user-1::acct-1", "first@example.com");
        WriteSnapshot(home, account, "access-secret", "acct-1");
        var hybrid = new HybridQuotaEstimateService(
            (_, cancellationToken) =>
                Task.FromCanceled<LocalUsageCollectionResult>(cancellationToken),
            _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(
                QuotaEstimateLedgerState.Empty,
                null)),
            (_, _) => Task.CompletedTask,
            new CodexCreditRateCard());
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(JsonResponse()));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QuotaService(
                client,
                hybridEstimator: hybrid).RefreshAccountAsync(
                    account,
                    home.Path,
                    cancellation.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refresh_all_cancellation_saves_completed_observations_once()
    {
        using var home = new TemporaryDirectory();
        var accounts = new[]
        {
            Accounts.Record(
                "user-1::acct-1",
                "first@example.com",
                accountId: "acct-1"),
            Accounts.Record(
                "user-2::acct-2",
                "second@example.com",
                accountId: "acct-2"),
        };
        foreach (var account in accounts)
        {
            WriteSnapshot(
                home,
                account,
                $"access-{account.ChatGptAccountId}",
                account.ChatGptAccountId);
        }

        var segmentStart = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var serverNow = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var initial = StateWithActivation(
            accounts[0],
            new AccountActivationInterval(segmentStart.AddMinutes(-1), null));
        var saveCount = 0;
        QuotaEstimateLedgerState? attemptedSave = null;
        var hybrid = new HybridQuotaEstimateService(
            (_, _) => Task.FromResult(new LocalUsageCollectionResult([], 0)),
            _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(initial, null)),
            (state, _) =>
            {
                saveCount++;
                attemptedSave = state;
                return Task.FromException(new IOException("ledger-save-failure"));
            },
            new CodexCreditRateCard());
        using var cancellation = new CancellationTokenSource();
        var requestCount = 0;
        using var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            requestCount++;
            if (requestCount == 3)
            {
                cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
            }

            return Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                    ? UsageResponse(
                        segmentStart.AddDays(7),
                        serverNow,
                        TimeSpan.FromDays(7),
                        usedPercent: 25)
                    : JsonResponse("""{"data":[]}"""));
        });
        using var client = new HttpClient(handler);
        var updates = new List<QuotaUpdate>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QuotaService(
                client,
                hybridEstimator: hybrid).RefreshAllAsync(
                    accounts,
                    home.Path,
                    (update, _) =>
                    {
                        updates.Add(update);
                        return Task.CompletedTask;
                    },
                    cancellation.Token));

        var update = Assert.Single(updates);
        Assert.Equal(accounts[0].AccountKey, update.AccountKey);
        Assert.Null(update.Warning);
        Assert.Equal(1, saveCount);
        Assert.Single(attemptedSave!.Accounts[accounts[0].AccountKey].Observations);
        Assert.Equal(3, handler.Requests.Count);
    }

    private static HybridQuotaEstimateService CreateHybrid(
        IReadOnlyList<LocalUsageEvent> events,
        QuotaEstimateLedgerState state,
        Action? onCollect = null,
        Action<QuotaEstimateLedgerState>? onSave = null) =>
        new(
            (_, _) =>
            {
                onCollect?.Invoke();
                return Task.FromResult(new LocalUsageCollectionResult(events, 0));
            },
            _ => Task.FromResult(new QuotaEstimateLedgerLoadResult(state, null)),
            (updated, _) =>
            {
                onSave?.Invoke(updated);
                return Task.CompletedTask;
            },
            new CodexCreditRateCard(),
            () => DateTimeOffset.Parse("2026-07-25T00:00:00Z"));

    private static QuotaEstimateLedgerState StateWithActivation(
        AccountRecord account,
        AccountActivationInterval activation) =>
        new(new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal)
        {
            [account.AccountKey] = new([activation], []),
        });

    private static LocalUsageEvent LocalUsage(
        DateTimeOffset timestamp,
        long inputTokens = 800_000) =>
        new(
            timestamp,
            "gpt-5.6-sol",
            "default",
            inputTokens,
            CachedInputTokens: 0,
            OutputTokens: 0);

    private static HttpResponseMessage UsageResponse(
        DateTimeOffset resetAt,
        DateTimeOffset serverNow,
        TimeSpan window,
        double usedPercent)
    {
        var resetAfter = (long)(resetAt - serverNow).TotalSeconds;
        return JsonResponse(System.Text.Json.JsonSerializer.Serialize(new
        {
            rate_limit = new
            {
                secondary_window = new
                {
                    used_percent = usedPercent,
                    limit_window_seconds = (long)window.TotalSeconds,
                    reset_at = resetAt.ToUnixTimeSeconds(),
                    reset_after_seconds = resetAfter,
                },
            },
        }));
    }

    private static void WriteSnapshot(TemporaryDirectory home, AccountRecord account, string accessToken, string accountId)
    {
        var path = AccountSnapshotPathResolver.Resolve(home.Path, account.AccountKey);
        var relativePath = Path.GetRelativePath(home.Path, path);
        home.Write(relativePath,
            $"{{\"auth_mode\":\"chatgpt\",\"tokens\":{{\"access_token\":\"{accessToken}\",\"account_id\":\"{accountId}\"}}}}");
    }

    private static HttpResponseMessage JsonResponse() => JsonResponse("""
        {"rate_limit":{"primary_window":{"used_percent":27,"limit_window_seconds":604800}}}
        """);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json),
    };

}
