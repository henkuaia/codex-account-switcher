using CodexAccountSwitcher.Security;

namespace CodexAccountSwitcher.Tests;

public sealed class SensitiveTextRedactorTests
{
    [Fact]
    public void Redacts_exact_secrets_and_token_bearing_text()
    {
        const string exactSecret = "exact-secret";
        const string bearerToken = "bearer-secret";
        const string accessToken = "access-secret";
        const string refreshToken = "refresh-secret";
        const string idToken = "id-secret";
        const string apiKey = "sk-secret";
        var text = $$"""
        status: connected
        raw={{exactSecret}}
        Authorization: Bearer {{bearerToken}}
        {"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","id_token":"{{idToken}}","OPENAI_API_KEY":"{{apiKey}}"}
        """;

        var redacted = SensitiveTextRedactor.Redact(text, [exactSecret]);

        Assert.Contains("status: connected", redacted, StringComparison.Ordinal);
        Assert.Contains("raw=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer [REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("\"access_token\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"refresh_token\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"id_token\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"OPENAI_API_KEY\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(exactSecret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(bearerToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(idToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "request failed: Authorization: Bearer bearer-secret; retry later",
        "request failed: Authorization: Bearer [REDACTED]; retry later")]
    [InlineData(
        "prefix Authorization : Bearer bearer-secret suffix",
        "prefix Authorization : Bearer [REDACTED] suffix")]
    public void Redacts_bearer_authorization_anywhere_in_a_line(
        string input,
        string expected)
    {
        var redacted = SensitiveTextRedactor.Redact(input, []);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("bearer-secret", redacted, StringComparison.Ordinal);
    }
}
