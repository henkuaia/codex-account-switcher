using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record SelectorResolution(bool IsAvailable, string? Value, string? Error)
{
    public static SelectorResolution Available(string value) => new(true, value, null);

    public static SelectorResolution Unavailable(string error) => new(false, null, error);
}

public static class AccountSelectorResolver
{
    private const string UnavailableError = "No unique account selector is available.";

    public static SelectorResolution Resolve(AccountRecord target, IReadOnlyList<AccountRecord> all)
    {
        if (IsUnique(target.Alias, all, static account => account.Alias))
        {
            return SelectorResolution.Available(target.Alias);
        }

        if (IsUnique(target.Email, all, static account => account.Email))
        {
            return SelectorResolution.Available(target.Email);
        }

        return SelectorResolution.Unavailable(UnavailableError);
    }

    private static bool IsUnique(
        string value,
        IReadOnlyList<AccountRecord> accounts,
        Func<AccountRecord, string> selector) =>
        !string.IsNullOrEmpty(value) &&
        accounts.Count(account => string.Equals(selector(account), value, StringComparison.OrdinalIgnoreCase)) == 1;
}
