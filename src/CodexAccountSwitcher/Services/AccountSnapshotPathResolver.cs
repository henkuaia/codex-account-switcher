using System.IO;
using System.Text;

namespace CodexAccountSwitcher.Services;

public static class AccountSnapshotPathResolver
{
    public static string Resolve(string codexHome, string accountKey) =>
        Path.Combine(codexHome, "accounts", $"{ResolveFileStem(accountKey)}.auth.json");

    private static string ResolveFileStem(string accountKey)
    {
        if (accountKey.Length > 0 &&
            accountKey is not "." and not ".." &&
            accountKey.All(IsSafeAsciiFileNameCharacter))
        {
            return accountKey;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(accountKey))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool IsSafeAsciiFileNameCharacter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.';
}
