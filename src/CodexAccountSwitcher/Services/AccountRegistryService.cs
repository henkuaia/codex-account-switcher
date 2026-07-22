using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed class AccountRegistryService
{
    private const string RegistryRelativePath = "accounts/registry.json";
    private const string ChatGptAuthClaim = "https://api.openai.com/auth";

    public async Task<AccountRegistry> LoadAsync(string codexHome, CancellationToken cancellationToken)
    {
        var registryPath = Path.Combine(codexHome, RegistryRelativePath);
        if (!File.Exists(registryPath))
        {
            return AccountRegistry.Empty;
        }

        RegistryDto registry;
        try
        {
            await using var stream = new FileStream(
                registryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            registry = await JsonSerializer.DeserializeAsync<RegistryDto>(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("The account registry is invalid.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The account registry is invalid.");
        }

        var schemaVersion = registry.SchemaVersion ?? registry.Version ?? 0;
        if (schemaVersion < 2 || schemaVersion > 3)
        {
            throw new InvalidDataException("The account registry schema is unsupported.");
        }

        var accounts = schemaVersion == 2
            ? await LoadLegacyAccountsAsync(codexHome, registry.Accounts ?? [], cancellationToken)
            : LoadCurrentAccounts(registry.Accounts ?? []);

        var activeAccountKey = schemaVersion == 2
            ? ResolveLegacyActiveAccountKey(registry.ActiveEmail, accounts)
            : registry.ActiveAccountKey;
        ValidateStructure(accounts, activeAccountKey);

        return new AccountRegistry(
            schemaVersion,
            activeAccountKey,
            Array.AsReadOnly(accounts.ToArray()));
    }

    private static List<AccountRecord> LoadCurrentAccounts(IReadOnlyList<AccountDto?> registryAccounts)
    {
        var accounts = new List<AccountRecord>();
        foreach (var account in registryAccounts)
        {
            if (account is null ||
                string.IsNullOrWhiteSpace(account.AccountKey) ||
                string.IsNullOrWhiteSpace(account.Email))
            {
                throw new InvalidDataException("The account registry contains an invalid account.");
            }

            accounts.Add(new AccountRecord(
                account.AccountKey,
                account.ChatGptAccountId ?? string.Empty,
                account.ChatGptUserId ?? string.Empty,
                account.Email,
                account.Alias ?? string.Empty,
                account.AccountName,
                account.Plan,
                account.AuthMode));
        }

        return accounts;
    }

    private static async Task<List<AccountRecord>> LoadLegacyAccountsAsync(
        string codexHome,
        IReadOnlyList<AccountDto?> registryAccounts,
        CancellationToken cancellationToken)
    {
        var accounts = new List<AccountRecord>();
        var emails = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in registryAccounts)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.Email))
            {
                throw new InvalidDataException("The account registry contains an invalid account.");
            }

            if (!emails.Add(NormalizeEmail(account.Email)))
            {
                throw new InvalidDataException("The account registry contains an invalid account.");
            }

            var snapshotPath = Path.Combine(codexHome, "accounts", $"{Base64UrlEncode(account.Email)}.auth.json");
            if (!File.Exists(snapshotPath))
            {
                throw new InvalidDataException("The account registry contains an invalid account.");
            }

            LegacyAuthSnapshotDto snapshot;
            try
            {
                await using var stream = new FileStream(
                    snapshotPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                snapshot = await JsonSerializer.DeserializeAsync<LegacyAuthSnapshotDto>(stream, cancellationToken: cancellationToken)
                    ?? throw new InvalidDataException("The account registry contains an invalid account.");
            }
            catch (JsonException)
            {
                throw new InvalidDataException("The account registry contains an invalid account.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.Tokens?.AccountId) || string.IsNullOrWhiteSpace(snapshot.Tokens.IdToken))
            {
                throw new InvalidDataException("The account registry contains an invalid account.");
            }

            var claims = ParseIdToken(snapshot.Tokens.IdToken);
            var userId = claims.Auth?.ChatGptUserId ?? claims.Auth?.UserId;
            var accountId = claims.Auth?.ChatGptAccountId;
            if (string.IsNullOrWhiteSpace(claims.Email) ||
                string.IsNullOrWhiteSpace(userId) ||
                string.IsNullOrWhiteSpace(accountId) ||
                !string.Equals(NormalizeEmail(claims.Email), NormalizeEmail(account.Email), StringComparison.Ordinal) ||
                !string.Equals(snapshot.Tokens.AccountId, accountId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The account registry contains an invalid account.");
            }

            accounts.Add(new AccountRecord(
                $"{userId}::{accountId}",
                accountId,
                userId,
                account.Email,
                account.Alias ?? string.Empty,
                null,
                account.Plan,
                account.AuthMode));
        }

        return accounts;
    }

    private static void ValidateStructure(
        IReadOnlyList<AccountRecord> accounts,
        string? activeAccountKey)
    {
        if (accounts
            .GroupBy(account => account.AccountKey, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("The account registry contains an invalid account.");
        }

        if (activeAccountKey is not null &&
            accounts.Count(account => string.Equals(
                account.AccountKey,
                activeAccountKey,
                StringComparison.Ordinal)) != 1)
        {
            throw new InvalidDataException("The account registry contains an invalid active account.");
        }
    }

    private static string? ResolveLegacyActiveAccountKey(string? activeEmail, IReadOnlyList<AccountRecord> accounts)
    {
        if (activeEmail is null)
        {
            return null;
        }

        var activeAccount = accounts.SingleOrDefault(account =>
            string.Equals(account.Email, activeEmail, StringComparison.Ordinal));
        return activeAccount?.AccountKey
            ?? throw new InvalidDataException("The account registry contains an invalid active account.");
    }

    private static JwtClaimsDto ParseIdToken(string idToken)
    {
        var segments = idToken.Split('.');
        if (segments.Length != 3 || string.IsNullOrEmpty(segments[1]))
        {
            throw new InvalidDataException("The account registry contains an invalid account.");
        }

        try
        {
            var payload = Encoding.UTF8.GetString(Base64UrlDecode(segments[1]));
            return JsonSerializer.Deserialize<JwtClaimsDto>(payload)
                ?? throw new InvalidDataException("The account registry contains an invalid account.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The account registry contains an invalid account.");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("The account registry contains an invalid account.");
        }
    }

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static byte[] Base64UrlDecode(string value)
    {
        var paddedValue = value.Replace('-', '+').Replace('_', '/');
        paddedValue += (paddedValue.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException(),
        };

        return Convert.FromBase64String(paddedValue);
    }

    private sealed class RegistryDto
    {
        [JsonPropertyName("schema_version")]
        public int? SchemaVersion { get; init; }

        [JsonPropertyName("version")]
        public int? Version { get; init; }

        [JsonPropertyName("active_account_key")]
        public string? ActiveAccountKey { get; init; }

        [JsonPropertyName("active_email")]
        public string? ActiveEmail { get; init; }

        [JsonPropertyName("accounts")]
        public List<AccountDto?>? Accounts { get; init; }
    }

    private sealed class AccountDto
    {
        [JsonPropertyName("account_key")]
        public string? AccountKey { get; init; }

        [JsonPropertyName("chatgpt_account_id")]
        public string? ChatGptAccountId { get; init; }

        [JsonPropertyName("chatgpt_user_id")]
        public string? ChatGptUserId { get; init; }

        [JsonPropertyName("email")]
        public string? Email { get; init; }

        [JsonPropertyName("alias")]
        public string? Alias { get; init; }

        [JsonPropertyName("account_name")]
        public string? AccountName { get; init; }

        [JsonPropertyName("plan")]
        public string? Plan { get; init; }

        [JsonPropertyName("auth_mode")]
        public string? AuthMode { get; init; }
    }

    private sealed class LegacyAuthSnapshotDto
    {
        [JsonPropertyName("tokens")]
        public LegacyTokensDto? Tokens { get; init; }
    }

    private sealed class LegacyTokensDto
    {
        [JsonPropertyName("account_id")]
        public string? AccountId { get; init; }

        [JsonPropertyName("id_token")]
        public string? IdToken { get; init; }
    }

    private sealed class JwtClaimsDto
    {
        [JsonPropertyName("email")]
        public string? Email { get; init; }

        [JsonPropertyName(ChatGptAuthClaim)]
        public JwtAuthClaimsDto? Auth { get; init; }
    }

    private sealed class JwtAuthClaimsDto
    {
        [JsonPropertyName("chatgpt_account_id")]
        public string? ChatGptAccountId { get; init; }

        [JsonPropertyName("chatgpt_plan_type")]
        public string? ChatGptPlanType { get; init; }

        [JsonPropertyName("chatgpt_user_id")]
        public string? ChatGptUserId { get; init; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; init; }
    }
}
