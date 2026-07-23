namespace CodexAccountSwitcher.Models;

public sealed record AccountMetadata(decimal? PeriodQuotaUsd, int UsedResetCount);

public sealed record AccountMetadataLoadResult(
    IReadOnlyDictionary<string, AccountMetadata> Accounts,
    string? Error);
