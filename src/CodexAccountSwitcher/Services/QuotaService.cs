using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Security;

namespace CodexAccountSwitcher.Services;

public sealed class QuotaService
{
    private static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");
    private static readonly Uri ResetCreditHistoryEndpoint =
        new("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
    private const string AnalyticsEndpoint =
        "https://chatgpt.com/backend-api/wham/analytics/daily-workspace-usage-counts";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const string UserAgent = "CodexAccountSwitcher/1.0 codex-auth/0.2.10";

    private readonly HttpClient _httpClient;
    private readonly AuthSnapshotReader _authSnapshotReader;
    private readonly HybridQuotaEstimateService? _hybridEstimator;

    public QuotaService(
        HttpClient httpClient,
        AuthSnapshotReader? authSnapshotReader = null,
        HybridQuotaEstimateService? hybridEstimator = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authSnapshotReader = authSnapshotReader ?? new AuthSnapshotReader();
        _hybridEstimator = hybridEstimator;
    }

    public async Task<QuotaUpdate> RefreshAccountAsync(
        AccountRecord account,
        string codexHome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);

        var hybridContext = await TryBeginHybridRefreshAsync(cancellationToken);
        try
        {
            var update = await RefreshAccountCoreAsync(
                account,
                codexHome,
                hybridContext,
                cancellationToken);
            var warning = await TryCompleteHybridRefreshAsync(
                hybridContext,
                cancellationToken);
            return WithWarning(update, warning);
        }
        catch
        {
            await TryCompleteHybridRefreshAsync(
                hybridContext,
                CancellationToken.None);
            throw;
        }
    }

    private async Task<QuotaUpdate> RefreshAccountCoreAsync(
        AccountRecord account,
        string codexHome,
        HybridQuotaRefreshContext? hybridContext,
        CancellationToken cancellationToken)
    {
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
                : await TryApplyEstimateAsync(
                    parsed.Display,
                    account,
                    snapshot,
                    hybridContext,
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

    private async Task<QuotaDisplay> TryApplyEstimateAsync(
        QuotaDisplay display,
        AccountRecord account,
        AuthSnapshot snapshot,
        HybridQuotaRefreshContext? hybridContext,
        CancellationToken requestCancellationToken,
        CancellationToken userCancellationToken)
    {
        return display.Period switch
        {
            QuotaPeriod.Weekly => await TryApplyWeeklyEstimateAsync(
                display,
                account,
                snapshot,
                hybridContext,
                requestCancellationToken,
                userCancellationToken),
            QuotaPeriod.Monthly => await TryApplyMonthlyEstimateAsync(
                display,
                account,
                snapshot,
                hybridContext,
                requestCancellationToken,
                userCancellationToken),
            _ => display,
        };
    }

    private async Task<QuotaDisplay> TryApplyWeeklyEstimateAsync(
        QuotaDisplay display,
        AccountRecord account,
        AuthSnapshot snapshot,
        HybridQuotaRefreshContext? hybridContext,
        CancellationToken requestCancellationToken,
        CancellationToken userCancellationToken)
    {
        if (display.Period != QuotaPeriod.Weekly ||
            display.ResetsAt is null)
        {
            return display;
        }

        var resetStart = display.ResetsAt.Value - display.WindowDuration;
        if (display.UsedPercent <= 0)
        {
            return ApplyEstimate(
                display,
                account,
                hybridContext,
                resetStart,
                analytics: null,
                AnalyticsAvailability.NotRequested);
        }

        return await TryApplyPeriodEstimateAsync(
            display,
            account,
            snapshot,
            hybridContext,
            resetStart,
            resetStart.UtcDateTime.TimeOfDay == TimeSpan.Zero,
            requestCancellationToken,
            userCancellationToken);
    }

    private async Task<QuotaDisplay> TryApplyMonthlyEstimateAsync(
        QuotaDisplay display,
        AccountRecord account,
        AuthSnapshot snapshot,
        HybridQuotaRefreshContext? hybridContext,
        CancellationToken requestCancellationToken,
        CancellationToken userCancellationToken)
    {
        if (display.ResetsAt is null ||
            display.ServerNow is null ||
            display.WindowDuration <= TimeSpan.Zero)
        {
            return display;
        }

        if (display.UsedPercent <= 0 &&
            (_hybridEstimator is null || hybridContext is null))
        {
            return display;
        }

        var naturalStart = display.ResetsAt.Value - display.WindowDuration;
        try
        {
            using var request = CreateAuthenticatedRequest(
                ResetCreditHistoryEndpoint,
                account,
                snapshot);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return WithEstimateStatus(
                    display,
                    "无法确定当前月额度片段，已跳过额度估算");
            }

            var responseBody = await response.Content.ReadAsStringAsync(requestCancellationToken);
            if (!ResetCreditHistoryParser.TryFindLatestRedeemedAt(
                    responseBody,
                    naturalStart,
                    display.ServerNow.Value,
                    out var latestRedeemedAt))
            {
                return WithEstimateStatus(
                    display,
                    "无法确定当前月额度片段，已跳过额度估算");
            }

            var segmentStart = latestRedeemedAt ?? naturalStart;
            if (display.UsedPercent <= 0)
            {
                return ApplyEstimate(
                    display,
                    account,
                    hybridContext,
                    segmentStart,
                    analytics: null,
                    AnalyticsAvailability.NotRequested);
            }

            return await TryApplyPeriodEstimateAsync(
                display,
                account,
                snapshot,
                hybridContext,
                segmentStart,
                segmentStart.UtcDateTime.TimeOfDay == TimeSpan.Zero,
                requestCancellationToken,
                userCancellationToken);
        }
        catch (OperationCanceledException) when (!userCancellationToken.IsCancellationRequested)
        {
            return WithEstimateStatus(
                display,
                "无法确定当前月额度片段，已跳过额度估算");
        }
        catch (HttpRequestException)
        {
            return WithEstimateStatus(
                display,
                "无法确定当前月额度片段，已跳过额度估算");
        }
        catch (InvalidDataException)
        {
            return WithEstimateStatus(
                display,
                "无法确定当前月额度片段，已跳过额度估算");
        }
    }

    private async Task<QuotaDisplay> TryApplyPeriodEstimateAsync(
        QuotaDisplay display,
        AccountRecord account,
        AuthSnapshot snapshot,
        HybridQuotaRefreshContext? hybridContext,
        DateTimeOffset segmentStart,
        bool includeStartDayInLower,
        CancellationToken requestCancellationToken,
        CancellationToken userCancellationToken)
    {
        if (display.ServerNow is null &&
            _hybridEstimator is not null &&
            hybridContext is not null)
        {
            return ApplyEstimate(
                display,
                account,
                hybridContext,
                segmentStart,
                analytics: null,
                AnalyticsAvailability.NotRequested);
        }

        var serverNow = display.ServerNow ?? DateTimeOffset.UtcNow;
        var startDate = DateOnly.FromDateTime(segmentStart.UtcDateTime);
        var endDateExclusive = DateOnly.FromDateTime(serverNow.UtcDateTime).AddDays(1);
        if (endDateExclusive <= startDate)
        {
            return display;
        }

        var uri = new Uri(
            $"{AnalyticsEndpoint}?start_date={startDate:yyyy-MM-dd}" +
            $"&end_date={endDateExclusive:yyyy-MM-dd}&group_by=day");
        AnalyticsUsageParseResult? analytics = null;
        var availability = AnalyticsAvailability.Failed;
        try
        {
            using var request = CreateAuthenticatedRequest(uri, account, snapshot);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(
                    requestCancellationToken);
                analytics = PeriodQuotaEstimator.Parse(
                    responseBody,
                    startDate,
                    includeStartDayInLower);
                availability = AnalyticsAvailability.Available;
            }
        }
        catch (OperationCanceledException) when (!userCancellationToken.IsCancellationRequested)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (InvalidDataException)
        {
        }

        return ApplyEstimate(
            display,
            account,
            hybridContext,
            segmentStart,
            analytics,
            availability);
    }

    public async Task<string?> RefreshAllAsync(
        IReadOnlyList<AccountRecord> accounts,
        string codexHome,
        Func<QuotaUpdate, CancellationToken, Task> reportAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(reportAsync);

        return await RefreshAllCoreAsync(
            accounts,
            codexHome,
            reportAsync,
            cancellationToken);
    }

    private async Task<string?> RefreshAllCoreAsync(
        IReadOnlyList<AccountRecord> accounts,
        string codexHome,
        Func<QuotaUpdate, CancellationToken, Task> reportAsync,
        CancellationToken cancellationToken)
    {
        var hybridContext = await TryBeginHybridRefreshAsync(cancellationToken);
        try
        {
            foreach (var account in accounts)
            {
                var update = await RefreshAccountCoreAsync(
                    account,
                    codexHome,
                    hybridContext,
                    cancellationToken);
                await reportAsync(update, cancellationToken);
            }
        }
        catch
        {
            await TryCompleteHybridRefreshAsync(
                hybridContext,
                CancellationToken.None);
            throw;
        }

        var warning = await TryCompleteHybridRefreshAsync(
            hybridContext,
            CancellationToken.None);
        return warning;
    }

    private QuotaDisplay ApplyEstimate(
        QuotaDisplay display,
        AccountRecord account,
        HybridQuotaRefreshContext? hybridContext,
        DateTimeOffset segmentStart,
        AnalyticsUsageParseResult? analytics,
        AnalyticsAvailability availability)
    {
        if (_hybridEstimator is not null &&
            hybridContext is not null &&
            display.ResetsAt is { } resetsAt)
        {
            try
            {
                return _hybridEstimator.ApplyObservation(
                    hybridContext,
                    account,
                    display,
                    new QuotaSegment(display.Period, segmentStart, resetsAt),
                    analytics,
                    availability);
            }
            catch (Exception exception) when (IsEstimatorFailure(exception))
            {
                return display;
            }
        }

        if (availability != AnalyticsAvailability.Available ||
            analytics?.State != AnalyticsUsageState.Valid)
        {
            return display;
        }

        var estimate = QuotaEstimateMath.TryCreateFullInterval(
            analytics.LowerCredits,
            analytics.UpperCredits,
            display.UsedPercent,
            percentResolution: 1);
        return estimate is null || estimate.UpperUsd <= 0
            ? display
            : display with
            {
                EstimatedPeriodQuotaLowerUsd = estimate.LowerUsd,
                EstimatedPeriodQuotaUpperUsd = estimate.UpperUsd,
            };
    }

    private async Task<HybridQuotaRefreshContext?> TryBeginHybridRefreshAsync(
        CancellationToken cancellationToken)
    {
        if (_hybridEstimator is null)
        {
            return null;
        }

        try
        {
            return await _hybridEstimator.BeginRefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (IsEstimatorFailure(exception))
        {
            return null;
        }
    }

    private async Task<string?> TryCompleteHybridRefreshAsync(
        HybridQuotaRefreshContext? context,
        CancellationToken cancellationToken)
    {
        if (_hybridEstimator is null || context is null)
        {
            return null;
        }

        try
        {
            return await _hybridEstimator.CompleteRefreshAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return "本地额度估算账本暂时无法保存。本次本地估算结果未保存，将稍后重试。";
        }
        catch (Exception exception) when (IsEstimatorFailure(exception))
        {
            return "本地额度估算账本暂时无法保存。本次本地估算结果未保存，将稍后重试。";
        }
    }

    private static QuotaUpdate WithWarning(QuotaUpdate update, string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return update;
        }

        return update with
        {
            Display = update.Display is null
                ? null
                : WithEstimateStatus(update.Display, warning),
            Warning = warning,
        };
    }

    private static QuotaDisplay WithEstimateStatus(
        QuotaDisplay display,
        string status)
    {
        var existing = display.EstimateStatus;
        if (string.IsNullOrWhiteSpace(existing))
        {
            return display with { EstimateStatus = status };
        }

        return existing.Split('；', StringSplitOptions.RemoveEmptyEntries)
            .Contains(status, StringComparer.Ordinal)
            ? display
            : display with { EstimateStatus = $"{existing}；{status}" };
    }

    private static bool IsEstimatorFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            ArgumentException or
            OverflowException;

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
