using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class AuthSnapshotReaderTests
{
    [Fact]
    public async Task Reads_standard_chatgpt_tokens()
    {
        using var file = TemporaryFile.Json("""
        {"auth_mode":"chatgpt","OPENAI_API_KEY":null,"tokens":{
          "id_token":"id-secret","access_token":"access-secret",
          "refresh_token":"refresh-secret","account_id":"acct-1"},"last_refresh":"now"}
        """);

        using var result = await new AuthSnapshotReader().ReadAsync(file.Path, default);

        Assert.Equal("access-secret", result.AccessToken);
        Assert.Equal("acct-1", result.AccountId);
    }

    [Fact]
    public async Task Api_key_auth_is_rejected_for_chatgpt_quota_without_disclosing_secret()
    {
        const string apiKey = "sk-secret";
        using var file = TemporaryFile.Json($$"""{"auth_mode":"apikey","OPENAI_API_KEY":"{{apiKey}}"}""");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AuthSnapshotReader().ReadAsync(file.Path, default));

        Assert.DoesNotContain(apiKey, exception.Message, StringComparison.Ordinal);
    }
}
