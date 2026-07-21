using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class AccountRegistryServiceTests
{
    [Fact]
    public async Task Missing_registry_returns_empty_registry()
    {
        using var home = new TemporaryDirectory();

        var result = await new AccountRegistryService().LoadAsync(home.Path, default);

        Assert.Empty(result.Accounts);
        Assert.Null(result.ActiveAccountKey);
    }

    [Fact]
    public async Task Valid_v3_registry_preserves_identity_and_active_key()
    {
        using var home = new TemporaryDirectory();
        home.Write("accounts/registry.json", """
            {
              "schema_version": 3,
              "active_account_key": "user-1::acct-1",
              "accounts": [{
                "account_key": "user-1::acct-1",
                "chatgpt_account_id": "acct-1",
                "chatgpt_user_id": "user-1",
                "email": "first@example.com",
                "alias": "main",
                "account_name": null,
                "plan": "plus",
                "auth_mode": "chatgpt",
                "created_at": 1,
                "last_used_at": null,
                "last_usage": null,
                "last_usage_at": null,
                "last_local_rollout": null
              }]
            }
            """);

        var result = await new AccountRegistryService().LoadAsync(home.Path, default);

        var account = Assert.Single(result.Accounts);
        Assert.Equal(3, result.SchemaVersion);
        Assert.Equal("user-1::acct-1", result.ActiveAccountKey);
        Assert.Equal("user-1::acct-1", account.AccountKey);
        Assert.Equal("acct-1", account.ChatGptAccountId);
        Assert.Equal("user-1", account.ChatGptUserId);
        Assert.Equal("first@example.com", account.Email);
        Assert.Equal("main", account.Alias);
        Assert.Null(account.AccountName);
        Assert.Equal("plus", account.Plan);
        Assert.Equal("chatgpt", account.AuthMode);
    }

    [Fact]
    public async Task Valid_v2_registry_is_loaded()
    {
        using var home = new TemporaryDirectory();
        home.Write("accounts/registry.json", """
            {
              "schema_version": 2,
              "accounts": [{
                "account_key": "user-2::acct-2",
                "chatgpt_account_id": "acct-2",
                "chatgpt_user_id": "user-2",
                "email": "second@example.com",
                "alias": "secondary"
              }]
            }
            """);

        var result = await new AccountRegistryService().LoadAsync(home.Path, default);

        Assert.Equal(2, result.SchemaVersion);
        Assert.Null(result.ActiveAccountKey);
        Assert.Equal("secondary", Assert.Single(result.Accounts).Alias);
    }

    [Fact]
    public async Task Malformed_registry_throws_invalid_data_without_raw_json()
    {
        using var home = new TemporaryDirectory();
        const string malformedJson = "{not-json-secret";
        home.Write("accounts/registry.json", malformedJson);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new AccountRegistryService().LoadAsync(home.Path, default));

        Assert.DoesNotContain(malformedJson, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schema_version\": 1, \"accounts\": []}")]
    [InlineData("{\"schema_version\": 3, \"accounts\": [{\"email\": \"first@example.com\"}]}")]
    [InlineData("{\"schema_version\": 3, \"accounts\": [{\"account_key\": \"user-1::acct-1\"}]}")]
    public async Task Invalid_registry_throws_invalid_data(string registryJson)
    {
        using var home = new TemporaryDirectory();
        home.Write("accounts/registry.json", registryJson);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new AccountRegistryService().LoadAsync(home.Path, default));
    }
}
