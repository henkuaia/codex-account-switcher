using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Security;

public static partial class SensitiveTextRedactor
{
    private const string RedactedValue = "[REDACTED]";

    public static string Redact(string text, IEnumerable<string> exactSecrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(exactSecrets);

        var redacted = text;
        foreach (var secret in exactSecrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                redacted = redacted.Replace(secret, RedactedValue, StringComparison.Ordinal);
            }
        }

        redacted = AuthorizationHeaderRegex().Replace(redacted, "${prefix}" + RedactedValue);
        return JsonTokenFieldRegex().Replace(redacted, "${prefix}" + RedactedValue + "\"");
    }

    [GeneratedRegex(@"(?im)(?<prefix>\bAuthorization\s*:\s*Bearer\s+)[^\s,;\r\n]+")]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex("(?<prefix>\"(?:access_token|refresh_token|id_token|OPENAI_API_KEY)\"\\s*:\\s*\")(?:\\\\.|[^\"\\\\])*(?<suffix>\")")]
    private static partial Regex JsonTokenFieldRegex();
}
