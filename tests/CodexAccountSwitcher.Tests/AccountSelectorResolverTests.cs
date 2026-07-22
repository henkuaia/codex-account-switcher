using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class AccountSelectorResolverTests
{
    [Fact]
    public void Unique_alias_is_preferred()
    {
        var target = Accounts.Record("key-1", "first@example.com", "main");

        var result = AccountSelectorResolver.Resolve(target, [target]);

        Assert.True(result.IsAvailable);
        Assert.Equal("main", result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Empty_alias_falls_back_to_unique_exact_email()
    {
        var target = Accounts.Record("key-1", "first@example.com");

        var result = AccountSelectorResolver.Resolve(target, [target]);

        Assert.True(result.IsAvailable);
        Assert.Equal("first@example.com", result.Value);
    }

    [Fact]
    public void Duplicate_alias_falls_back_to_unique_exact_email()
    {
        var target = Accounts.Record("key-1", "first@example.com", "main");
        var other = Accounts.Record("key-2", "second@example.com", "MAIN");

        var result = AccountSelectorResolver.Resolve(target, [target, other]);

        Assert.True(result.IsAvailable);
        Assert.Equal("first@example.com", result.Value);
    }

    [Fact]
    public void Ambiguous_alias_and_email_are_rejected()
    {
        var target = Accounts.Record("key-1", "same@example.com", "same");
        var other = Accounts.Record("key-2", "SAME@example.com", "SAME");

        var result = AccountSelectorResolver.Resolve(target, [target, other]);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Alias_from_a_different_account_is_rejected_when_target_is_absent()
    {
        var target = Accounts.Record("key-1", "first@example.com", "main");
        var other = Accounts.Record("key-2", "second@example.com", "main");

        var result = AccountSelectorResolver.Resolve(target, [other]);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Email_from_a_different_account_is_rejected_when_target_is_absent()
    {
        var target = Accounts.Record("key-1", "first@example.com");
        var other = Accounts.Record("key-2", "first@example.com");

        var result = AccountSelectorResolver.Resolve(target, [other]);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Alias_substring_collision_falls_back_to_safe_exact_email()
    {
        var target = Accounts.Record("key-1", "first@example.com", "main");
        var other = Accounts.Record("key-2", "second@example.com", "MAIN-ARCHIVE");

        var result = AccountSelectorResolver.Resolve(target, [target, other]);

        Assert.True(result.IsAvailable);
        Assert.Equal("first@example.com", result.Value);
    }

    [Fact]
    public void Alias_substring_collision_in_another_accounts_email_falls_back_to_safe_email()
    {
        var target = Accounts.Record("key-1", "first@example.com", "main");
        var other = Accounts.Record("key-2", "main-archive@example.com", "archive");

        var result = AccountSelectorResolver.Resolve(target, [target, other]);

        Assert.True(result.IsAvailable);
        Assert.Equal("first@example.com", result.Value);
    }

    [Fact]
    public void Alias_substring_collision_in_another_accounts_name_falls_back_to_safe_email()
    {
        var target = Accounts.Record("key-1", "first@example.com", "main");
        var other = Accounts.Record("key-2", "second@example.com", "archive") with
        {
            AccountName = "MAIN-ARCHIVE",
        };

        var result = AccountSelectorResolver.Resolve(target, [target, other]);

        Assert.True(result.IsAvailable);
        Assert.Equal("first@example.com", result.Value);
    }

    [Fact]
    public void Exact_email_is_rejected_when_helper_substring_query_matches_another_field()
    {
        var target = Accounts.Record("key-1", "first@example.com");
        var other = Accounts.Record("key-2", "second@example.com", "archive") with
        {
            AccountName = "backup first@example.com account",
        };

        var result = AccountSelectorResolver.Resolve(target, [target, other]);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Value);
    }
}
