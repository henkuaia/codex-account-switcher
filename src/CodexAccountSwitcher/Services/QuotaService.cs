using System.Net.Http;
using System.Net.Http.Headers;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Security;

namespace CodexAccountSwitcher.Services;

public sealed class QuotaService
{
    private static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");
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
            return parsed.Error is null
                ? new QuotaUpdate(account.AccountKey, parsed.Display, null)
                : Failure(account, parsed.Error, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(account, "The quota refresh request timed out.", snapshot);
        }
        catch (Exception)
        {
            return Failure(account, "The quota refresh request failed.", snapshot);
        }
        finally
        {
            snapshot?.Dispose();
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
}
