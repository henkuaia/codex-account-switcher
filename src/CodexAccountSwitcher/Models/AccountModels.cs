namespace CodexAccountSwitcher.Models;

public sealed record AccountRecord(
    string AccountKey,
    string ChatGptAccountId,
    string ChatGptUserId,
    string Email,
    string Alias,
    string? AccountName,
    string? Plan,
    string? AuthMode);

public sealed record AccountRegistry(
    int SchemaVersion,
    string? ActiveAccountKey,
    IReadOnlyList<AccountRecord> Accounts)
{
    public static AccountRegistry Empty { get; } = new(3, null, Array.Empty<AccountRecord>());
}
