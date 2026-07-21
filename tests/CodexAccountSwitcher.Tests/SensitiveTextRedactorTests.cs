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
        Assert.DoesNotContain(exactSecret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(bearerToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(idToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, redacted, StringComparison.Ordinal);
    }
}
