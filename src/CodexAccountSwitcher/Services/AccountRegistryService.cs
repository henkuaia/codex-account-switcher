using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed class AccountRegistryService
{
    private const string RegistryRelativePath = "accounts/registry.json";

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

        if (registry.SchemaVersion < 2)
        {
            throw new InvalidDataException("The account registry schema is unsupported.");
        }

        var accounts = new List<AccountRecord>();
        foreach (var account in registry.Accounts ?? [])
        {
            if (string.IsNullOrWhiteSpace(account.AccountKey) || string.IsNullOrWhiteSpace(account.Email))
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

        return new AccountRegistry(
            registry.SchemaVersion,
            registry.ActiveAccountKey,
            Array.AsReadOnly(accounts.ToArray()));
    }

    private sealed class RegistryDto
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("active_account_key")]
        public string? ActiveAccountKey { get; init; }

        [JsonPropertyName("accounts")]
        public List<AccountDto>? Accounts { get; init; }
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
}
