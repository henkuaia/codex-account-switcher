namespace CodexAccountSwitcher.Models;

public sealed record QuotaCacheEntry(
    QuotaDisplay Display,
    DateTimeOffset RefreshedAt);

public sealed record QuotaCacheLoadResult(
    IReadOnlyDictionary<string, QuotaCacheEntry> Accounts,
    string? Error);
