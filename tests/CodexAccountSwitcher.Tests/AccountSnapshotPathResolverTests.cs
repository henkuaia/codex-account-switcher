using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class AccountSnapshotPathResolverTests
{
    [Theory]
    [InlineData("simple-key", "simple-key.auth.json")]
    [InlineData("user-1::acct-1", "dXNlci0xOjphY2N0LTE.auth.json")]
    [InlineData("user_name.1", "user_name.1.auth.json")]
    [InlineData("é", "w6k.auth.json")]
    [InlineData(".", "Lg.auth.json")]
    [InlineData("..", "Li4.auth.json")]
    public void Resolves_v0210_snapshot_filename(string key, string expectedName)
    {
        var path = AccountSnapshotPathResolver.Resolve("C:/codex", key);

        Assert.Equal(Path.Combine("C:/codex", "accounts", expectedName), path);
    }
}
