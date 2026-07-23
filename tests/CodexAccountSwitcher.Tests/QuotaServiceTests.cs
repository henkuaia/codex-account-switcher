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
        Assert.Equal(8m, update.Display!.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(24m, update.Display.EstimatedPeriodQuotaUpperUsd);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(
            "https://chatgpt.com/backend-api/wham/analytics/daily-workspace-usage-counts?start_date=2026-07-20&end_date=2026-07-26&group_by=day",
            requests[1].RequestUri!.ToString());
        Assert.Equal("Bearer", requests[1].Headers.Authorization!.Scheme);
        Assert.Equal("acct-1", requests[1].Headers.GetValues("ChatGPT-Account-Id").Single());
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
        Assert.Equal(16m, update.Display.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(24m, update.Display.EstimatedPeriodQuotaUpperUsd);
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
        Assert.Equal(24m, update.Display!.EstimatedPeriodQuotaLowerUsd);
        Assert.Equal(24m, update.Display.EstimatedPeriodQuotaUpperUsd);
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
        var progress = new CollectingProgress<QuotaUpdate>();

        await new QuotaService(client).RefreshAllAsync(accounts, home.Path, progress, default);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, handler.MaximumActiveRequests);
        Assert.Equal(3, progress.Values.Count);
        Assert.NotNull(progress.Values[0].Error);
        Assert.Null(progress.Values[1].Error);
        Assert.Null(progress.Values[2].Error);
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
