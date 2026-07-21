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
    public void Matching_uses_case_insensitive_full_string_equality()
    {
        var target = Accounts.Record("key-1", "first@example.com", "main");
        var other = Accounts.Record("key-2", "second@example.com", "MAIN-ARCHIVE");

        var result = AccountSelectorResolver.Resolve(target, [target, other]);

        Assert.True(result.IsAvailable);
        Assert.Equal("main", result.Value);
    }
}
