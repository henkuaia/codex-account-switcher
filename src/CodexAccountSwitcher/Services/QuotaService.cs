using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Security;

namespace CodexAccountSwitcher.Services;

public sealed class QuotaService
{
    private static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");
    private const string AnalyticsEndpoint =
        "https://chatgpt.com/backend-api/wham/analytics/daily-workspace-usage-counts";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const string UserAgent = "CodexAccountSwitcher/1.0 codex-auth/0.2.10";

    private readonly HttpClient _httpClient;
    private readonly AuthSnapshotReader _authSnapshotReader;

    public QuotaService(HttpClient httpClient, AuthSnapshotReader? authSnapshotReader = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authSnapshotReader = authSnapshotReader ?? new AuthSnapshotReader();
    }

    public async Task<QuotaUpdate> RefreshAccountAsync(
        AccountRecord account,
        string codexHome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);

        AuthSnapshot? snapshot = null;
        try
        {
            var snapshotPath = AccountSnapshotPathResolver.Resolve(codexHome, account.AccountKey);
            snapshot = await _authSnapshotReader.ReadAsync(snapshotPath, cancellationToken);
            if (!string.Equals(snapshot.AccountId, account.ChatGptAccountId, StringComparison.Ordinal))
            {
                return Failure(account, "The authentication snapshot does not match the selected account.", snapshot);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.AccessToken);
            request.Headers.Add("ChatGPT-Account-Id", account.ChatGptAccountId);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var requestCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellationSource.CancelAfter(RequestTimeout);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellationSource.Token);

            if (!response.IsSuccessStatusCode)
            {
                return Failure(
                    account,
                    $"The quota refresh request was rejected (HTTP {(int)response.StatusCode}).",
                    snapshot);
            }

            var responseBody = await response.Content.ReadAsStringAsync(requestCancellationSource.Token);
            var parsed = QuotaResponseParser.Parse(responseBody);
            if (parsed.Error is not null)
            {
                return Failure(account, parsed.Error, snapshot);
            }

            var display = parsed.Display is null
                ? null
                : await TryApplyWeeklyEstimateAsync(
                    parsed.Display,
                    account,
                    snapshot,
                    requestCancellationSource.Token,
                    cancellationToken);
            return new QuotaUpdate(account.AccountKey, display, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(account, "The quota refresh request timed out.", snapshot);
        }
        catch (InvalidDataException)
        {
            return Failure(account, "The quota refresh request failed.", snapshot);
        }
        catch (HttpRequestException)
        {
            return Failure(account, "The quota refresh request failed.", snapshot);
        }
        finally
        {
            snapshot?.Dispose();
        }
    }

    private async Task<QuotaDisplay> TryApplyWeeklyEstimateAsync(
        QuotaDisplay display,
        AccountRecord account,
        AuthSnapshot snapshot,
        CancellationToken requestCancellationToken,
        CancellationToken userCancellationToken)
    {
        if (display.Period != QuotaPeriod.Weekly ||
            display.UsedPercent <= 0 ||
            display.ResetsAt is null)
        {
            return display;
        }

        var resetStart = display.ResetsAt.Value - display.WindowDuration;
        var serverNow = display.ServerNow ?? DateTimeOffset.UtcNow;
        var startDate = DateOnly.FromDateTime(resetStart.UtcDateTime);
        var endDateExclusive = DateOnly.FromDateTime(serverNow.UtcDateTime).AddDays(1);
        if (endDateExclusive <= startDate)
        {
            return display;
        }

        var uri = new Uri(
            $"{AnalyticsEndpoint}?start_date={startDate:yyyy-MM-dd}" +
            $"&end_date={endDateExclusive:yyyy-MM-dd}&group_by=day");
        try
        {
            using var request = CreateAuthenticatedRequest(uri, account, snapshot);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return display;
            }

            var responseBody = await response.Content.ReadAsStringAsync(requestCancellationToken);
            var estimate = WeeklyQuotaEstimator.TryEstimate(
                responseBody,
                display.UsedPercent,
                startDate);
            return estimate is null
                ? display
                : display with
                {
                    EstimatedPeriodQuotaLowerUsd = estimate.LowerUsd,
                    EstimatedPeriodQuotaUpperUsd = estimate.UpperUsd,
                };
        }
        catch (OperationCanceledException) when (!userCancellationToken.IsCancellationRequested)
        {
            return display;
        }
        catch (HttpRequestException)
        {
            return display;
        }
        catch (InvalidDataException)
        {
            return display;
        }
    }

    public async Task RefreshAllAsync(
        IReadOnlyList<AccountRecord> accounts,
        string codexHome,
        IProgress<QuotaUpdate> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(progress);

        foreach (var account in accounts)
        {
            var update = await RefreshAccountAsync(account, codexHome, cancellationToken);
            progress.Report(update);
        }
    }

    private static QuotaUpdate Failure(AccountRecord account, string error, AuthSnapshot? snapshot)
    {
        var secrets = snapshot is null
            ? [account.ChatGptAccountId]
            : new[] { account.ChatGptAccountId, snapshot.AccessToken, snapshot.AccountId };
        return new QuotaUpdate(account.AccountKey, null, SensitiveTextRedactor.Redact(error, secrets));
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(
        Uri uri,
        AccountRecord account,
        AuthSnapshot snapshot)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.AccessToken);
        request.Headers.Add("ChatGPT-Account-Id", account.ChatGptAccountId);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }
}
