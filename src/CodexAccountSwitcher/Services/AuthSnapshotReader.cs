using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountSwitcher.Services;

public sealed class AuthSnapshotReader
{
    private const string InvalidAuthSnapshotMessage = "The authentication snapshot is invalid.";

    public async Task<AuthSnapshot> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var snapshot = await JsonSerializer.DeserializeAsync<AuthSnapshotDto>(
                stream,
                cancellationToken: cancellationToken)
                ?? throw new InvalidDataException(InvalidAuthSnapshotMessage);

            if (!string.Equals(snapshot.AuthMode, "chatgpt", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(snapshot.Tokens?.AccessToken) ||
                string.IsNullOrWhiteSpace(snapshot.Tokens.AccountId))
            {
                throw new InvalidDataException(InvalidAuthSnapshotMessage);
            }

            return new AuthSnapshot(snapshot.Tokens.AccessToken, snapshot.Tokens.AccountId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(InvalidAuthSnapshotMessage);
        }
    }

    private sealed class AuthSnapshotDto
    {
        [JsonPropertyName("auth_mode")]
        public string? AuthMode { get; init; }

        [JsonPropertyName("tokens")]
        public TokensDto? Tokens { get; init; }
    }

    private sealed class TokensDto
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("account_id")]
        public string? AccountId { get; init; }
    }
}

public sealed class AuthSnapshot : IDisposable
{
    private char[]? _accessToken;
    private char[]? _accountId;

    internal AuthSnapshot(string accessToken, string accountId)
    {
        _accessToken = accessToken.ToCharArray();
        _accountId = accountId.ToCharArray();
    }

    public string AccessToken => new(GetAccessToken());

    public string AccountId => new(GetAccountId());

    public void Dispose()
    {
        Clear(_accessToken);
        Clear(_accountId);
        _accessToken = null;
        _accountId = null;
        GC.SuppressFinalize(this);
    }

    private ReadOnlySpan<char> GetAccessToken() => _accessToken
        ?? throw new ObjectDisposedException(nameof(AuthSnapshot));

    private ReadOnlySpan<char> GetAccountId() => _accountId
        ?? throw new ObjectDisposedException(nameof(AuthSnapshot));

    private static void Clear(char[]? value)
    {
        if (value is not null)
        {
            Array.Clear(value);
        }
    }
}
